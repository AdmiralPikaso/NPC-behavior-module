using Godot;
using Zero.Actors;
using Zero.Ai.Actions;
using Zero.Ai.Nodes;
using Zero.Ai.Tactics;

namespace Zero.Ai.Stealth;

/// <summary>
/// «Мозг» NPC для стелс-экшена: автомат состояний тревоги (верхний уровень архитектуры),
/// внутри каждого состояния — своё поведение из действий <see cref="NpcAction"/>:
///   Спокоен      — патруль по маршруту;
///   Подозревает  — замер и взгляд на источник, осторожный шаг к нему, осмотр;
///   Тревога      — крик союзникам и бой по приказам отряда (тактики итерации 4)
///                  от последней известной позиции цели;
///   Ищет         — контакт потерян: отряд прочёсывает места рядом с последней позицией.
/// Переходы задаёт шкала подозрения <see cref="Awareness"/>.
/// </summary>
public sealed class StealthBrain : NpcAction
{
    public const float LoseContactAfter = 5f;
    public const float SearchDuration = 30f;
    public const float ShoutRadius = 20f;
    /// <summary>Печатать смены состояний в Output.</summary>
    public static bool Log { get; set; }

    public AlertState State { get; private set; } = AlertState.Calm;
    public Awareness Awareness { get; }
    /// <summary>Когда NPC впервые поднял тревогу (секунды отряда), или null.</summary>
    public float? AlarmedAt { get; private set; }

    private readonly Squad _squad;
    private readonly CombatOrderAction _combat;
    private NpcAction? _patrol;
    private NpcAgent? _npc;
    private float _stateTime;

    // подозрение: фазы осмотра
    private enum Look { Freeze, Approach, LookAround, Done }
    private Look _look;
    private float _lookTime;
    private Vector3 _investigate;

    // поиск
    private bool _searchLooking;
    private float _searchLookTime;

    public StealthBrain(Squad squad, NpcAgent npc)
    {
        _squad = squad;
        _combat = new CombatOrderAction(squad);
        Awareness = new Awareness(npc, squad.World);
        Awareness.SetTarget(squad.Target);
    }

    public override string Name => State switch
    {
        AlertState.Calm => _patrol?.Name ?? "Спокоен",
        AlertState.Suspicious => _look switch
        {
            Look.Freeze => "? Что это было",
            Look.Approach => "? Проверяет",
            _ => "? Осматривается",
        },
        AlertState.Searching => "?? Ищет",
        _ => "! " + _combat.Name,
    };

    public override void Start(NpcAgent npc)
    {
        _npc = npc;
        NoiseSystem.Subscribe(OnNoise);
        npc.Damaged += OnDamaged;
        npc.ShotAt += OnShotAt;
        if (npc.Route is { } route && route.Points.Count > 0)
            _patrol = new PatrolAction(route, 0);
        Enter(npc, AlertState.Calm);
    }

    public override void Stop(NpcAgent npc)
    {
        NoiseSystem.Unsubscribe(OnNoise);
        npc.Damaged -= OnDamaged;
        npc.ShotAt -= OnShotAt;
        StopChild(npc);
    }

    private void OnNoise(NoiseEvent n)
    {
        if (_npc is null || _npc.IsDead)
            return;
        // свои шаги и свои выстрелы не настораживают; тревогу между NPC передаёт крик
        if (n.Source is NpcAgent && n.Kind != NoiseKind.Shout)
            return;
        Awareness.Hear(n);
    }

    /// <summary>По охраннику стреляют (пуля рядом) — сразу тревога, источник — стрелок.</summary>
    private void OnShotAt(ActorBody a, ActorBody shooter) => Awareness.Raise(100f, shooter.GlobalPosition);

    private void OnDamaged(ActorBody a, float amount, Node? source)
    {
        if (source is Node3D s)
            Awareness.Raise(100f, s.GlobalPosition);
    }

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        _stateTime += dt;
        Awareness.Update(dt, State == AlertState.Alarm);
        if (Awareness.SeesTarget && (State == AlertState.Alarm || Awareness.Suspicion >= Awareness.AlarmThreshold))
            _squad.Report(_squad.Target.GlobalPosition);

        // ── переходы ──
        if (State != AlertState.Alarm
            && (Awareness.Suspicion >= Awareness.AlarmThreshold || Awareness.AlarmRaisedByOther))
        {
            var byOther = Awareness.AlarmRaisedByOther;
            Awareness.ConsumeAlarmFlag();
            Enter(npc, AlertState.Alarm);
            if (Awareness.SeesTarget)
                _squad.Report(_squad.Target.GlobalPosition);
            else if (Awareness.PointOfInterest is { } poi)
                _squad.Hint(poi);
            if (!byOther)
                NoiseSystem.Emit(new NoiseEvent(NoiseKind.Shout, npc.GlobalPosition, ShoutRadius, npc, _squad.TargetPos));
        }
        else if (State == AlertState.Alarm && _stateTime > LoseContactAfter && _squad.TimeSinceSeen > LoseContactAfter)
        {
            Enter(npc, AlertState.Searching);
        }
        else if (State == AlertState.Searching && _stateTime > SearchDuration)
        {
            Enter(npc, AlertState.Suspicious);
        }
        else if (State == AlertState.Calm && Awareness.Suspicion >= Awareness.SuspiciousThreshold)
        {
            Enter(npc, AlertState.Suspicious);
        }
        else if (State == AlertState.Suspicious && _look == Look.Done && Awareness.Suspicion < Awareness.CalmThreshold)
        {
            Enter(npc, AlertState.Calm);
        }

        // ── поведение состояния ──
        switch (State)
        {
            case AlertState.Calm:
                if (_patrol is not null)
                    _patrol.Tick(npc, dt);
                else
                    npc.StopMoving();
                break;
            case AlertState.Suspicious:
                TickSuspicious(npc, dt);
                break;
            case AlertState.Searching:
                TickSearching(npc, dt);
                break;
            case AlertState.Alarm:
                _combat.Tick(npc, dt);
                break;
        }
        return ActionStatus.Running;
    }

    private void Enter(NpcAgent npc, AlertState state)
    {
        StopChild(npc);
        var prev = State;
        if (Log && prev != state)
            GD.Print($"[стелс {_squad.Time:0.0} с] {npc.Name}: {prev} → {state} (подозрение {Awareness.Suspicion:0})");
        State = state;
        npc.Alert = state;
        _stateTime = 0f;
        switch (state)
        {
            case AlertState.Calm:
                npc.SetCrouching(false);
                _patrol?.Start(npc);
                break;
            case AlertState.Suspicious:
                _look = Look.Freeze;
                _lookTime = 0f;
                _investigate = Awareness.PointOfInterest ?? npc.GlobalPosition;
                if (prev is AlertState.Searching or AlertState.Alarm)
                    Awareness.Heightened = true;
                break;
            case AlertState.Searching:
                Awareness.Heightened = true;
                Awareness.Raise(-100f, null);
                Awareness.Raise(50f, null);
                _searchLooking = false;
                break;
            case AlertState.Alarm:
                AlarmedAt ??= _squad.Time;
                Awareness.Heightened = true;
                _combat.Start(npc);
                break;
        }
    }

    private void StopChild(NpcAgent npc)
    {
        switch (State)
        {
            case AlertState.Calm: _patrol?.Stop(npc); break;
            case AlertState.Alarm: _combat.Stop(npc); break;
            default:
                npc.StopMoving();
                npc.ClearAim();
                break;
        }
    }

    private void TickSuspicious(NpcAgent npc, float dt)
    {
        _lookTime += dt;
        // новый источник подозрения — переключиться на него
        if (Awareness.PointOfInterest is { } poi && Squad.FlatDistance(poi, _investigate) > 2f)
        {
            _investigate = poi;
            _look = Look.Freeze;
            _lookTime = 0f;
        }
        switch (_look)
        {
            case Look.Freeze:
                npc.StopMoving();
                npc.AimAt(_investigate + Vector3.Up);
                if (_lookTime > 1f)
                {
                    _look = Look.Approach;
                    _lookTime = 0f;
                }
                break;
            case Look.Approach:
                npc.ClearAim();
                var res = npc.NavigateTo(_investigate, Gait.Walk, 1.2f);
                if (res is NavResult.Arrived or NavResult.Unreachable || _lookTime > 10f)
                {
                    _look = Look.LookAround;
                    _lookTime = 0f;
                }
                break;
            case Look.LookAround:
                npc.StopMoving();
                var a = _lookTime * 1.6f;
                npc.AimAt(npc.GlobalPosition + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * 5f + Vector3.Up);
                if (_lookTime > 3.5f)
                {
                    _look = Look.Done;
                    npc.ClearAim();
                    Awareness.ClearPointOfInterest();
                }
                break;
            case Look.Done:
                npc.StopMoving();
                break;
        }
    }

    private void TickSearching(NpcAgent npc, float dt)
    {
        if (_searchLooking)
        {
            _searchLookTime += dt;
            npc.StopMoving();
            var a = _searchLookTime * 2.2f;
            npc.AimAt(npc.GlobalPosition + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * 5f + Vector3.Up);
            if (_searchLookTime > 1.3f)
            {
                _searchLooking = false;
                _squad.MarkSearched(npc);
            }
            return;
        }
        npc.ClearAim();
        var point = _squad.SearchPointFor(npc) ?? _squad.LastKnownPosition;
        var res = npc.NavigateTo(point, Gait.Run, 0.8f);
        if (res is NavResult.Arrived or NavResult.Unreachable)
        {
            _searchLooking = true;
            _searchLookTime = 0f;
        }
    }
}
