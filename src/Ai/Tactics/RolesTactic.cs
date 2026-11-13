using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Actors;
using Zero.Ai.Nodes;

namespace Zero.Ai.Tactics;

/// <summary>
/// Сильная позиция уровня (hard point из Uncharted 4): область, которую размечает дизайнер, с минимумом
/// и максимумом бойцов. Это не точка, а площадка размером с комнату: укрытие внутри неё боец выбирает сам.
/// </summary>
public sealed record HardPoint
{
    public string Name { get; init; } = "точка";
    /// <summary>Центр области (на уровне пола).</summary>
    public Vector3 Center { get; init; }
    /// <summary>Половины размеров области по X и Z.</summary>
    public Vector2 HalfSize { get; init; } = new(4f, 4f);
    /// <summary>Сколько бойцов обязаны держать точку (заполняется раньше всех остальных ролей).</summary>
    public int Min { get; init; }
    /// <summary>Сколько бойцов точка может принять.</summary>
    public int Max { get; init; } = 1;
    public bool Enabled { get; init; } = true;

    public bool Contains(Vector3 p) =>
        Mathf.Abs(p.X - Center.X) <= HalfSize.X && Mathf.Abs(p.Z - Center.Z) <= HalfSize.Y;
}

/// <summary>
/// Параметры боя (global combat params из Uncharted 4): числа, которыми дизайнер настраивает конкретный бой,
/// не трогая код тактики. Значения по умолчанию взяты со слайда лекции; расстояния засады уменьшены под
/// арену 40×40 м (в лекции 20 м по навмешу).
/// </summary>
public sealed record EncounterParams
{
    /// <summary>Сколько бойцов давят на игрока (engagers).</summary>
    public int Engagers { get; init; } = 2;
    /// <summary>Сколько бойцов ждут в засаде на пути отхода игрока (ambushers).</summary>
    public int Ambushers { get; init; } = 1;
    /// <summary>Пауза перед заменой выбывшего давящего, с.</summary>
    public float EngagerReplacementDelay { get; init; } = 6f;
    /// <summary>Сколько бойцов стреляют одновременно: не меньше и не больше.</summary>
    public int MinShooters { get; init; } = 2;
    public int MaxShooters { get; init; } = 3;
    /// <summary>Сколько секунд стрелок держит право огня до «перезарядки» (если его есть кому сменить).</summary>
    public float ShooterHoldTime { get; init; } = 4f;
    /// <summary>Пауза стрелка после того, как он отдал право огня, с.</summary>
    public float ShooterRestTime { get; init; } = 1.5f;
    /// <summary>Игрок сидит на месте дольше этого — отряд давит: фланговый или смельчак (wait-to-flank-time).</summary>
    public float CampTime { get; init; } = 6f;
    /// <summary>Игрок сместился дальше этого — таймер сидения сбрасывается (move-…-distance-reset), м.</summary>
    public float CampResetDistance { get; init; } = 6f;
    /// <summary>Минимальная оценка маршрута обхода 0..1 (доля пути, скрытая от игрока). Ниже — обхода нет.</summary>
    public float MinFlankPathRating { get; init; } = 0.3f;
    /// <summary>На каком расстоянии от игрока ставится засада, м.</summary>
    public float AmbushDistance { get; init; } = 12f;
    /// <summary>Игрок подошёл ближе этого к засаде — она срабатывает, м.</summary>
    public float AmbushTriggerDistance { get; init; } = 9f;
    /// <summary>Ближе этого к игроку давящие укрытий не берут, м.</summary>
    public float EngageStopLine { get; init; } = 8f;
    /// <summary>Дальняя граница полосы, в которой давящий держит укрытие, м.</summary>
    public float EngageMaxRange { get; init; } = 13f;
    public IReadOnlyList<HardPoint> HardPoints { get; init; } = Array.Empty<HardPoint>();

    /// <summary>Параметры текущего боя: задаёт сцена (арена), читает тактика «Роли».</summary>
    public static EncounterParams Current { get; set; } = new();

    /// <summary>
    /// Разметка тестовой арены 40×40 м: отряд стартует на севере. «Северные ворота» — условный выход,
    /// его всегда держит один боец; две боковые площадки принимают лишних бойцов большого отряда.
    /// </summary>
    public static EncounterParams ForArena(int squadSize) => new()
    {
        Engagers = squadSize <= 4 ? 2 : 3,
        Ambushers = squadSize <= 4 ? 1 : 2,
        HardPoints = new[]
        {
            new HardPoint { Name = "северные ворота", Center = new Vector3(0f, 0f, -10f), HalfSize = new Vector2(6.5f, 4.5f), Min = 1, Max = 2 },
            new HardPoint { Name = "западная площадка", Center = new Vector3(-12f, 0f, -9f), HalfSize = new Vector2(4.5f, 5.5f), Min = 0, Max = 1 },
            new HardPoint { Name = "восточная площадка", Center = new Vector3(12f, 0f, -9f), HalfSize = new Vector2(4.5f, 5.5f), Min = 0, Max = 1 },
        },
    };
}

/// <summary>
/// 5. Роли (Uncharted 4, доклад «Authored vs. Systemic», GDC 2017). Отряд не выбирает одну тактику на всех:
/// бойцы одновременно получают разные роли, а числа задаёт <see cref="EncounterParams"/>.
///
/// Основные роли (слои боя по удалению от игрока):
/// — <b>давящие</b> идут к игроку от укрытия к укрытию по одному, остальные в это время прикрывают огнём;
/// — <b>засадники</b> молча занимают укрытие сбоку от игрока и стреляют, когда он подошёл или увидел их;
/// — <b>защитники</b> держат сильную позицию и ищут укрытие внутри её области, а не вокруг игрока.
///
/// Раздача ролей (пять шагов из лекции): минимумы сильных позиций, затем давящие, затем засадники, затем
/// остальные по позициям до максимума, лишние становятся давящими (и на экране появляется предупреждение).
/// Давящего, который выбыл, заменяют не сразу, а через <see cref="EncounterParams.EngagerReplacementDelay"/>.
///
/// Роли поверх основных:
/// — <b>стрелок</b>: одновременно стреляют не больше <see cref="EncounterParams.MaxShooters"/>; право огня получает
///   ближний к игроку и отдаёт его, когда потерял выстрел, получил попадание или «перезаряжается»;
/// — <b>фланговый</b> и <b>смельчак</b>: когда игрок засиделся на месте (<see cref="EncounterParams.CampTime"/> с
///   в радиусе <see cref="EncounterParams.CampResetDistance"/> м), один боец идёт в обход или на штурм.
///   Фланговым берут того, кто сидит без дела, и только если есть скрытый маршрут и остальные давят.
/// </summary>
public sealed class RolesTactic : PeriodicTactic
{
    public override string Name => "Роли (Uncharted 4)";

    /// <summary>Сколько обходов начато за раунд (для отчёта).</summary>
    public static int FlanksStarted;

    private enum Role { Engager, Ambusher, Defender }
    private enum LostCheck { None, Full }

    private EncounterParams P = new();

    // основные роли
    private readonly Dictionary<NpcAgent, Role> _role = new();
    private readonly Dictionary<NpcAgent, HardPoint> _post = new();
    private float _replaceAt;

    /// <summary>Сколько засад сработало за раунд (для отчёта).</summary>
    public static int AmbushesSprung;
    private const float AmbushFireTime = 6f;
    private const float AmbushCooldown = 15f;

    /// <summary>Засада одного засадника: точка сбоку от пути игрока к давящим и её состояние.</summary>
    private sealed class AmbushState
    {
        public Vector3? Spot;
        /// <summary>Линия пути на момент выбора точки: откуда шёл игрок и куда (к давящим).</summary>
        public Vector3 Origin, Dir;
        public float Side;
        public float RetreatTime;
        public float SeenTime;
        public float ChosenAt;
        public float RetryAt;
        public bool Sprung;
        public float SprungAt;
        public string Reason = "";
        public float LastHp = -1f;
    }

    private readonly Dictionary<NpcAgent, AmbushState> _amb = new();
    private float _noAmbusherUntil;
    private int _overflow;

    // позиции
    private readonly Dictionary<NpcAgent, Vector3> _spot = new();
    private readonly Dictionary<NpcAgent, float> _spotAt = new();

    // давление, когда игрок сидит на месте
    private Vector3 _campAnchor;
    private float _campTime;
    private float _pressureRestUntil;
    private bool _preferFlank = true;
    private NpcAgent? _flanker;
    private Vector3 _flankDest;
    private float _flankStartedAt;
    private float _flankArrivedAt = -1f;
    private NpcAgent? _brave;
    private float _braveAbortFar = BraveLayer.AbortDistance;
    private readonly AssaultRun _assault = new(push: true); // вылазка — штурм до конца

    // стрелки
    private readonly HashSet<NpcAgent> _shooters = new();
    private readonly Dictionary<NpcAgent, float> _shooterSince = new();
    private readonly Dictionary<NpcAgent, float> _shooterRestUntil = new();
    private readonly Dictionary<NpcAgent, float> _lastHp = new();

    // огонь игрока и удержание точек
    private Squad? _sq;
    private ActorBody? _target;
    private float _playerShotAt = -99f;
    private readonly Dictionary<NpcAgent, float> _hpSeen = new();
    private readonly Dictionary<NpcAgent, float> _hitAt = new();
    private readonly Dictionary<NpcAgent, float> _badSide = new();
    private readonly Dictionary<NpcAgent, float> _arrivedAt = new();
    private readonly Dictionary<NpcAgent, List<float>> _hitsHere = new();
    private readonly Dictionary<NpcAgent, (Vector3 P, float Until)> _abandoned = new();

    public override void Begin(Squad s)
    {
        // выстрелы игрока: по ним видно, кто под огнём (подписка одна на цель)
        _sq = s;
        if (!ReferenceEquals(_target, s.Target))
        {
            if (_target is not null)
                _target.Fired -= OnPlayerFired;
            _target = s.Target;
            _target.Fired += OnPlayerFired;
        }
        // в стелс-режиме отряд зовёт Begin при каждой смене состава бойцов: тогда состояние боя сохраняем
        // и только пристраиваем новичков; полный сброс — в начале раунда
        if (s.Time > 0.01f && _role.Count > 0)
        {
            s.ShooterGate = MayShoot;
            base.Begin(s);
            return;
        }
        P = EncounterParams.Current;
        _role.Clear();
        _post.Clear();
        _amb.Clear();
        _noAmbusherUntil = 0f;
        _spot.Clear();
        _spotAt.Clear();
        _shooters.Clear();
        _shooterSince.Clear();
        _shooterRestUntil.Clear();
        _lastHp.Clear();
        _playerShotAt = -99f;
        _hpSeen.Clear();
        _hitAt.Clear();
        _badSide.Clear();
        _arrivedAt.Clear();
        _hitsHere.Clear();
        _abandoned.Clear();
        _assault.Clear();
        _replaceAt = 0f;
        _overflow = 0;
        _campAnchor = Flat(s.TargetPos);
        _campTime = 0f;
        _pressureRestUntil = 0f;
        _preferFlank = true;
        _flanker = null;
        _flankArrivedAt = -1f;
        _brave = null;
        s.ShooterGate = MayShoot;
        Assign(s, s.Fighters.ToList());
        base.Begin(s);
    }

    private bool MayShoot(NpcAgent m) => _shooters.Contains(m);

    // ───────────── раздача ролей ─────────────

    private List<HardPoint> ActivePoints => P.HardPoints.Where(h => h.Enabled && h.Max > 0).ToList();

    private int CountAt(HardPoint h) => _post.Count(kv => kv.Value == h && !kv.Key.IsDead);

    private int CountOf(Role r) => _role.Count(kv => kv.Value == r && !kv.Key.IsDead);

    /// <summary>Минимумы позиций соблюдаются, пока после них остаётся хотя бы один боец для атаки.</summary>
    private bool MinimumsApply(int alive) => alive > ActivePoints.Sum(h => h.Min);

    /// <summary>Чем осторожнее боец и чем ближе он к позиции, тем охотнее его ставят защитником; назад не гоняют.</summary>
    private float DefenderCost(Squad s, NpcAgent m, HardPoint h)
    {
        var back = Mathf.Max(0f, Squad.FlatDistance(h.Center, s.TargetPos) - TacticUtil.DistToTarget(s, m));
        return Squad.FlatDistance(m.GlobalPosition, h.Center) + s.Brave.Bravery(m) * 8f + back * 0.5f;
    }

    /// <summary>Давящим охотнее становится ближний к игроку и смелый.</summary>
    private static float EngagerCost(Squad s, NpcAgent m) => TacticUtil.DistToTarget(s, m) - s.Brave.Bravery(m) * 6f;

    private void SetRole(Squad s, NpcAgent m, Role role, HardPoint? post = null)
    {
        _role[m] = role;
        if (post is not null)
            _post[m] = post;
        else
            _post.Remove(m);
        Forget(m);
        // сторону и точку засады выбирают позже, по линии пути игрока (см. ChooseAmbush), а не по месту спавна
        if (role == Role.Ambusher)
            _amb[m] = new AmbushState();
        else
            _amb.Remove(m);
    }

    private void Forget(NpcAgent m)
    {
        _spot.Remove(m);
        _spotAt.Remove(m);
    }

    /// <summary>Пять шагов раздачи ролей из лекции.</summary>
    private void Assign(Squad s, List<NpcAgent> alive)
    {
        var pool = new List<NpcAgent>(alive);
        var points = ActivePoints;
        // 1) минимумы сильных позиций
        if (MinimumsApply(alive.Count))
            foreach (var h in points)
                for (var i = 0; i < h.Min && pool.Count > 0; i++)
                {
                    var m = pool.OrderBy(x => DefenderCost(s, x, h)).First();
                    pool.Remove(m);
                    SetRole(s, m, Role.Defender, h);
                }
        // 2) давящие
        for (var i = 0; i < P.Engagers && pool.Count > 0; i++)
        {
            var m = pool.OrderBy(x => EngagerCost(s, x)).First();
            pool.Remove(m);
            SetRole(s, m, Role.Engager);
        }
        // 3) засадники
        for (var i = 0; i < P.Ambushers && pool.Count > 0; i++)
        {
            var m = pool.OrderBy(x => TacticUtil.DistToTarget(s, x)).First();
            pool.Remove(m);
            SetRole(s, m, Role.Ambusher);
        }
        // 4) остальные — по позициям до максимума; 5) лишние — давящие
        _overflow = 0;
        foreach (var m in pool)
            PlaceSpare(s, m);
    }

    private void PlaceSpare(Squad s, NpcAgent m)
    {
        var free = ActivePoints.Where(h => CountAt(h) < h.Max).OrderBy(h => DefenderCost(s, m, h)).FirstOrDefault();
        if (free is not null)
        {
            SetRole(s, m, Role.Defender, free);
            return;
        }
        SetRole(s, m, Role.Engager);
        _overflow++;
    }

    /// <summary>Состав изменился: убрать выбывших, пристроить новичков, восполнить роли.</summary>
    private void Maintain(Squad s, List<NpcAgent> alive)
    {
        foreach (var m in _role.Keys.Where(m => m.IsDead || !alive.Contains(m)).ToList())
        {
            if (_role[m] == Role.Engager && m.IsDead)
                _replaceAt = s.Time + P.EngagerReplacementDelay;
            _role.Remove(m);
            _post.Remove(m);
            _amb.Remove(m);
            _shooters.Remove(m);
            Forget(m);
        }
        if (_flanker is { } f && !_role.ContainsKey(f))
            EndFlank(s, rest: true);
        if (_brave is { } b && !_role.ContainsKey(b))
            EndSortie(s);

        var useMins = MinimumsApply(alive.Count);
        // новички (в стелсе бойцы поднимают тревогу по одному)
        foreach (var m in alive.Where(m => !_role.ContainsKey(m)).ToList())
        {
            var need = useMins ? ActivePoints.FirstOrDefault(h => CountAt(h) < h.Min) : null;
            if (need is not null)
                SetRole(s, m, Role.Defender, need);
            else if (CountOf(Role.Engager) < P.Engagers)
                SetRole(s, m, Role.Engager);
            else if (CountOf(Role.Ambusher) < P.Ambushers && s.Time >= _noAmbusherUntil)
                SetRole(s, m, Role.Ambusher);
            else
                PlaceSpare(s, m);
        }

        if (!useMins)
        {
            // бойцов осталось не больше, чем нужно позициям: держать их некем — все давят
            foreach (var m in _role.Where(kv => kv.Value != Role.Engager).Select(kv => kv.Key).ToList())
                SetRole(s, m, Role.Engager);
            return;
        }

        // минимум позиции важнее всего: защитника заменяют сразу — засадником, а если его нет, давящим
        foreach (var h in ActivePoints)
            while (CountAt(h) < h.Min)
            {
                var m = Free(Role.Ambusher).OrderBy(x => DefenderCost(s, x, h)).FirstOrDefault()
                        ?? Free(Role.Engager).OrderBy(x => DefenderCost(s, x, h)).FirstOrDefault();
                if (m is null)
                    break;
                SetRole(s, m, Role.Defender, h);
            }

        // давящего заменяют с задержкой и по одному: засадником, потом защитником сверх минимума
        if (CountOf(Role.Engager) < P.Engagers && s.Time >= _replaceAt)
        {
            var m = Free(Role.Ambusher).OrderBy(x => EngagerCost(s, x)).FirstOrDefault()
                    ?? Free(Role.Defender).Where(x => CountAt(_post[x]) > _post[x].Min)
                        .OrderBy(x => EngagerCost(s, x)).FirstOrDefault();
            if (m is not null)
            {
                SetRole(s, m, Role.Engager);
                _replaceAt = s.Time + P.EngagerReplacementDelay;
            }
        }

        // засадник после срабатывания стал давящим; через паузу лишнего давящего снова сажают в засаду
        if (_noAmbusherUntil > 0f && s.Time >= _noAmbusherUntil && CountOf(Role.Ambusher) < P.Ambushers
            && CountOf(Role.Engager) > P.Engagers)
        {
            var m = Free(Role.Engager).OrderByDescending(x => TacticUtil.DistToTarget(s, x)).FirstOrDefault();
            if (m is not null)
                SetRole(s, m, Role.Ambusher);
        }
    }

    /// <summary>Бойцы с этой ролью, которых можно переназначить (не на обходе и не на штурме).</summary>
    private IEnumerable<NpcAgent> Free(Role r) =>
        _role.Where(kv => kv.Value == r && !kv.Key.IsDead && kv.Key != _flanker && kv.Key != _brave).Select(kv => kv.Key);

    // ───────────── план ─────────────

    /// <summary>Фронт: направление от игрока на давящих (или на весь отряд, если давящих нет).</summary>
    private Vector3 Front(Squad s)
    {
        var target = Flat(s.TargetPos);
        var group = _role.Where(kv => kv.Value == Role.Engager && !kv.Key.IsDead).Select(kv => kv.Key).ToList();
        if (group.Count == 0)
            group = s.Fighters.ToList();
        var sum = Vector3.Zero;
        foreach (var m in group)
            sum += Flat(m.GlobalPosition) - target;
        return sum.LengthSquared() > 0.01f ? sum.Normalized() : Vector3.Forward;
    }

    protected override void Replan(Squad s)
    {
        var alive = s.Fighters.ToList();
        if (alive.Count == 0)
            return;
        TrackHits(s, alive);
        Maintain(s, alive);
        var front = Front(s);

        UpdatePressure(s, alive, front);

        // давящие перебегают по одному: пока один бежит, остальные прикрывают
        var engagers = alive.Where(m => _role.GetValueOrDefault(m) == Role.Engager && m != _flanker && m != _brave
                                        && !s.Brave.IsOut(m)).ToList();
        var someoneMoving = engagers.Any(m => _spot.TryGetValue(m, out var p) && !TacticUtil.Arrived(m, p));
        var covering = someoneMoving || _brave is not null || s.Brave.Active;
        foreach (var m in engagers.OrderBy(m => TacticUtil.DistToTarget(s, m)))
            someoneMoving |= Engage(s, m, someoneMoving, covering);

        foreach (var m in alive)
        {
            if (m == _flanker || m == _brave || s.Brave.IsOut(m))
                continue;
            switch (_role.GetValueOrDefault(m))
            {
                case Role.Ambusher:
                    Ambush(s, m);
                    break;
                case Role.Defender:
                    Defend(s, m);
                    break;
            }
        }

        if (_flanker is { } f)
            OrderFlanker(s, f);
        if (_brave is { } b)
        {
            var order = _assault.Next(s, b, Period, "Смельчак");
            Give(s, b, order);
        }

        UpdateShooters(s, alive);
    }


    // ───────────── позиции ─────────────

    /// <summary>
    /// Держать выбранную точку и менять её только по причине: точки нет, приказано сменить, место занято
    /// или позиция потеряна (обошли, нет выстрела). Возвращает точку и признак, что она сменилась.
    /// </summary>
    private Vector3 Keep(Squad s, NpcAgent m, PositionWeights w, float maxTravel, Func<Vector3, bool>? allow,
        bool force, Vector3 fallback, LostCheck lost, float minHold, out bool changed)
    {
        changed = false;
        var has = _spot.TryGetValue(m, out var spot);
        var arrived = has && TacticUtil.Arrived(m, spot);
        var held = s.Time - _spotAt.GetValueOrDefault(m, -99f);
        Vector3? avoid = null;
        var need = !has || force;
        if (has && !arrived && TacticUtil.Blocked(s, m))
        {
            need = true;
            avoid = spot;
        }
        else if (has && arrived && lost == LostCheck.Full && held > minHold && TacticUtil.PositionLost(s, m, out _))
            need = true;
        if (!need)
            return spot;
        // под огнём новую точку ищут только назад или вбок
        var best = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye, w,
            Claimed(s, m, avoid), maxTravel, And(allow, NotCloser(s, m)));
        var p = best ?? (has ? spot : fallback);
        changed = !has || Squad.FlatDistance(p, spot) > 0.5f;
        _spot[m] = p;
        _spotAt[m] = s.Time;
        return p;
    }

    /// <summary>Давящий: укрытие с выстрелом в полосе перед игроком; издалека подходит перебежками.</summary>
    private bool Engage(Squad s, NpcAgent m, bool someoneMoving, bool covering)
    {
        var has = _spot.TryGetValue(m, out var cur);
        var arrived = has && TacticUtil.Arrived(m, cur);
        var far = arrived && Squad.FlatDistance(cur, s.TargetPos) > P.EngageMaxRange + 1.5f;
        // перебежка вперёд под огнём — верная смерть: только когда игрок занят другим, перезаряжается или молчит
        var underFire = UnderFire(s, m);
        var advance = far && !someoneMoving && !underFire && s.Time - _spotAt.GetValueOrDefault(m) > 1.5f;
        var w = new PositionWeights
        {
            Cover = 3f, LineOfFire = 2f, Range = 1.5f, MinRange = P.EngageStopLine + 1f, MaxRange = P.EngageMaxRange,
            HardMinRange = P.EngageStopLine, Advance = 1.5f, Travel = 1f, Spacing = 2f,
            RequireCover = true, RequireShot = true, RequireOwnSide = true,
        };
        var spot = Keep(s, m, w, 14f, null, advance, m.GlobalPosition, LostCheck.Full, 2f, out _);
        // начатую перебежку вперёд под огнём прервать в ближайшее укрытие (если до цели ещё далеко)
        if (underFire && !TacticUtil.Arrived(m, spot) && Squad.FlatDistance(m.GlobalPosition, spot) > 4f
            && Squad.FlatDistance(spot, s.TargetPos) < TacticUtil.DistToTarget(s, m) - 0.5f
            && NearestCorrectCover(s, m, null, 8f) is { } stop)
            spot = SetSpot(s, m, stop);
        if (Recheck(s, m, spot, null, out var moved))
            spot = SetSpot(s, m, moved);
        var there = TacticUtil.Arrived(m, spot);
        var supporting = there && covering;
        Give(s, m, new Order
        {
            Role = !there ? "Давящий: перебежка" : supporting ? "Давящий: прикрывает" : "Давящий: огонь",
            Destination = spot, Stance = Stance.Peek, FireWhileMoving = true, KeepAway = P.EngageStopLine,
            PeekTime = supporting ? 1.4f : 1.0f, PeekPause = supporting ? 0.3f : 0.6f,
        });
        return !there;
    }

    /// <summary>Защитник: укрытие внутри области своей позиции (центр поиска — позиция, а не игрок).</summary>
    private void Defend(Squad s, NpcAgent m)
    {
        if (!_post.TryGetValue(m, out var h))
            return;
        var w = new PositionWeights
        {
            Cover = 3f, LineOfFire = 2f, Range = 0.5f, MinRange = 8f, MaxRange = 22f, Travel = 1.5f, Spacing = 2f,
            RequireCover = true, RequireShot = true, RequireOwnSide = true,
        };
        // Только точки укрытий внутри области и с дальней от игрока стороны препятствия (RequireOwnSide).
        var fallback = s.World.CoverPoints.Where(c => h.Contains(c.Position))
            .OrderBy(c => Squad.FlatDistance(c.Position, m.GlobalPosition))
            .Select(c => (Vector3?)c.Position).FirstOrDefault(p => OwnSide(s, p!.Value))
            ?? s.World.CoverPoints.Where(c => h.Contains(c.Position))
                .OrderBy(c => Squad.FlatDistance(c.Position, m.GlobalPosition))
                .Select(c => (Vector3?)c.Position).FirstOrDefault()
            ?? s.World.SnapToNav(h.Center);
        var spot = Keep(s, m, w, 80f, h.Contains, false, fallback, LostCheck.Full, 5f, out _);
        if (Recheck(s, m, spot, h.Contains, out var moved))
            spot = SetSpot(s, m, moved);
        var there = TacticUtil.Arrived(m, spot);
        Give(s, m, new Order
        {
            Role = there ? "Защитник: держит" : "Защитник: идёт",
            Destination = spot, Stance = Stance.Peek, FireWhileMoving = true, PeekTime = 1.0f, PeekPause = 0.7f,
        });
    }

    // ───────────── сторона укрытия и огонь игрока ─────────────

    /// <summary>Точку закрывает от игрока её собственное препятствие (боец с дальней от игрока стороны).</summary>
    private static bool OwnSide(Squad s, Vector3 p) => s.World.IsOwnSideCover(p, s.TargetPos, s.TargetEye);

    /// <summary>Все приказы «Ролей» — с флагом «под огнём прятаться, а не отбиваться на месте».</summary>
    private static void Give(Squad s, NpcAgent m, Order o) => s.Give(m, o with { HideWhenHit = true });

    private void OnPlayerFired(ActorBody _) => _playerShotAt = _sq?.Time ?? 0f;

    /// <summary>Отметить попадания по бойцам (раз за такт планирования).</summary>
    private void TrackHits(Squad s, List<NpcAgent> alive)
    {
        foreach (var m in alive)
        {
            var hp = m.Health;
            if (_hpSeen.TryGetValue(m, out var was) && hp < was - 0.5f)
                _hitAt[m] = s.Time;
            _hpSeen[m] = hp;
        }
    }

    private bool HitWithin(Squad s, NpcAgent m, float seconds) => s.Time - _hitAt.GetValueOrDefault(m, -99f) < seconds;

    /// <summary>
    /// Боец под огнём: по нему попали за последние 4 с, или игрок стрелял за последние 1,5 с, видит бойца
    /// и целится в его сторону (угол меньше 30°; без направления прицела — достаточно линии видимости).
    /// Перезаряжающийся игрок не держит под огнём никого: это окно для перебежек.
    /// </summary>
    private bool UnderFire(Squad s, NpcAgent m)
    {
        var t = s.Target;
        if (t.IsReloading)
            return false;
        if (HitWithin(s, m, 4f))
            return true;
        if (s.Time - _playerShotAt >= 1.5f || !s.World.ClearLine(t.EyePosition, m.CenterMass))
            return false;
        if (t.AimPoint is not { } aim)
            return true;
        var a = Flat(aim) - Flat(t.GlobalPosition);
        var b = Flat(m.GlobalPosition) - Flat(t.GlobalPosition);
        return a.LengthSquared() < 0.01f || b.LengthSquared() < 0.01f || Mathf.RadToDeg(a.AngleTo(b)) < 30f;
    }

    /// <summary>Под огнём позицию меняют только назад или вбок: не ближе к игроку, чем сейчас.</summary>
    private Func<Vector3, bool>? NotCloser(Squad s, NpcAgent m)
    {
        if (!UnderFire(s, m))
            return null;
        var d = TacticUtil.DistToTarget(s, m) - 0.5f;
        return p => Squad.FlatDistance(p, s.TargetPos) >= d;
    }

    private static Func<Vector3, bool>? And(Func<Vector3, bool>? a, Func<Vector3, bool>? b) =>
        a is null ? b : b is null ? a : p => a(p) && b(p);

    /// <summary>Занятые точки и точка, брошенная этим бойцом недавно (на неё не возвращаются 5 с).</summary>
    private List<Vector3> Claimed(Squad s, NpcAgent m, Vector3? avoid)
    {
        var list = TacticUtil.ClaimedPlus(s, m, avoid);
        if (_abandoned.TryGetValue(m, out var ab) && s.Time < ab.Until)
            list.Add(ab.P);
        return list;
    }

    private Vector3 SetSpot(Squad s, NpcAgent m, Vector3 p)
    {
        _spot[m] = p;
        _spotAt[m] = s.Time;
        return p;
    }

    /// <summary>
    /// Ближайшее корректное укрытие: своя сторона, не ближе к игроку, чем боец сейчас, свободное,
    /// не брошенное недавно, в пределах <paramref name="maxTravel"/> м.
    /// </summary>
    private Vector3? NearestCorrectCover(Squad s, NpcAgent m, Func<Vector3, bool>? area, float maxTravel)
    {
        var claimed = Claimed(s, m, null);
        var minDist = TacticUtil.DistToTarget(s, m) - 0.5f;
        foreach (var c in s.World.CoverPoints.OrderBy(c => Squad.FlatDistance(c.Position, m.GlobalPosition)))
        {
            var p = c.Position;
            if (Squad.FlatDistance(p, m.GlobalPosition) > maxTravel)
                break;
            if (Squad.FlatDistance(p, s.TargetPos) < minDist || (area is not null && !area(p))
                || claimed.Any(q => Squad.FlatDistance(q, p) < 2.5f))
                continue;
            if (OwnSide(s, p))
                return p;
        }
        return null;
    }

    /// <summary>
    /// Проверка точки на месте: укрытие не защищает, пора в другое. Причины: (а) своя сторона нарушена
    /// дольше 0,6 с (игрок обошёл; боец просидел на точке не меньше 1,5 с); (б) по нему попали, пока он сидел
    /// не выглядывая; (в) на этой точке по нему попали дважды за 4 с — в любом состоянии, включая
    /// выглядывание и самооборону. Новая точка — ближайшее корректное укрытие не ближе к игроку, а если
    /// такого нет — отход к любому укрытию своей стороны в пределах 12 м подальше от игрока.
    /// Брошенная точка запрещена на 5 с.
    /// </summary>
    private bool Recheck(Squad s, NpcAgent m, Vector3 spot, Func<Vector3, bool>? area, out Vector3 next)
    {
        next = spot;
        if (!TacticUtil.Arrived(m, spot))
        {
            _arrivedAt.Remove(m);
            _badSide.Remove(m);
            _hitsHere.Remove(m);
            return false;
        }
        if (!_arrivedAt.ContainsKey(m))
            _arrivedAt[m] = s.Time;
        var bad = OwnSide(s, spot) ? 0f : _badSide.GetValueOrDefault(m) + Period;
        _badSide[m] = bad;
        var hitNow = HitWithin(s, m, 0.01f);
        if (hitNow)
        {
            if (!_hitsHere.TryGetValue(m, out var list))
                _hitsHere[m] = list = new List<float>();
            list.Add(s.Time);
            list.RemoveAll(t => s.Time - t > 4f);
        }
        var hitHidden = hitNow && !(s.CombatOf(m)?.Peeking ?? false);
        var hitTwice = _hitsHere.TryGetValue(m, out var hits) && hits.Count >= 2;
        var flanked = bad > 0.6f && s.Time - _arrivedAt[m] >= 1.5f;
        // цель вплотную — укрытие не меняют: драться до конца (обошли на дистанции — перебежать можно)
        if ((!flanked && !hitHidden && !hitTwice) || TacticUtil.InCloseFight(s, m))
            return false;
        var p = NearestCorrectCover(s, m, area, 12f) ?? RetreatCover(s, m, 12f);
        if (p is null)
            return false;
        _abandoned[m] = (spot, s.Time + 5f);
        _badSide.Remove(m);
        _arrivedAt.Remove(m);
        _hitsHere.Remove(m);
        next = p.Value;
        return true;
    }

    /// <summary>Отход: любое укрытие своей стороны в пределах <paramref name="maxTravel"/> м дальше от игрока, чем боец.</summary>
    private Vector3? RetreatCover(Squad s, NpcAgent m, float maxTravel)
    {
        var d = TacticUtil.DistToTarget(s, m);
        var claimed = s.ClaimedExcept(m);
        foreach (var c in s.World.CoverPoints.OrderBy(c => Squad.FlatDistance(c.Position, m.GlobalPosition)))
        {
            var p = c.Position;
            if (Squad.FlatDistance(p, m.GlobalPosition) > maxTravel)
                break;
            if (Squad.FlatDistance(p, s.TargetPos) > d + 1f && !claimed.Any(q => Squad.FlatDistance(q, p) < 1.2f)
                && OwnSide(s, p))
                return p;
        }
        return null;
    }

    /// <summary>
    /// Засада «сбоку от пути». Игрок идёт к давящим, значит его путь — отрезок от игрока к их центру.
    /// Засадник заранее и скрытно садится в укрытие сбоку от этого отрезка, впереди игрока, и не двигается,
    /// пока игрок не придёт. Срабатывает: игрок рядом, увидел засадника, попал по нему или прошёл мимо.
    /// После срабатывания засадник <see cref="AmbushFireTime"/> с бьёт из укрытия с приоритетом права огня,
    /// затем навсегда становится давящим; нового засадника не назначают <see cref="AmbushCooldown"/> с.
    /// </summary>
    private void Ambush(Squad s, NpcAgent m)
    {
        if (!_amb.TryGetValue(m, out var a))
            _amb[m] = a = new AmbushState();
        var hp = m.Health;
        var hit = a.LastHp >= 0f && hp < a.LastHp - 0.5f;
        a.LastHp = hp;

        if (a.Sprung)
        {
            if (s.Time - a.SprungAt > AmbushFireTime)
            {
                _noAmbusherUntil = s.Time + AmbushCooldown;
                SetRole(s, m, Role.Engager);
                Engage(s, m, true, true);
                return;
            }
            if (a.Spot is { } held && Recheck(s, m, held, null, out var moved))
                a.Spot = moved;
            GiveSprung(s, m, a);
            return;
        }

        // точку выбирают один раз; кандидатов нет — боец давит и пробует снова раз в 2 с
        if (a.Spot is null)
        {
            if (s.Time >= a.RetryAt)
            {
                a.Spot = ChooseAmbush(s, m, a, false, null) ?? ChooseAmbush(s, m, a, true, null);
                if (a.Spot is null)
                    a.RetryAt = s.Time + 2f;
                else
                {
                    Forget(m);
                    a.RetreatTime = 0f;
                }
            }
            if (a.Spot is null)
            {
                Engage(s, m, true, true);
                return;
            }
        }
        var spot = a.Spot.Value;
        var there = TacticUtil.Arrived(m, spot);

        // срабатывание
        // «увидел»: корпус бойца виден от глаз игрока дольше одного такта (в пути и на точке), или от игрока
        // видна сама точка укрытия (FlankedTime считается только на точке, а в начале раунда в нём может
        // остаться значение прошлого приказа — поэтому только спустя 0,5 с после выбора точки)
        a.SeenTime = s.World.ClearLine(s.TargetEye, m.CenterMass) ? a.SeenTime + Period : 0f;
        var seen = a.SeenTime > Period * 1.5f
                   || (s.Time - a.ChosenAt > 0.5f && (s.CombatOf(m)?.FlankedTime ?? 0f) > 0.2f);
        var progress = (Flat(s.TargetPos) - a.Origin).Dot(a.Dir);
        string? why = null;
        if (seen)
            why = there ? "игрок увидел" : "заметили в пути";
        else if (hit)
            why = "попали";
        else if (TacticUtil.DistToTarget(s, m) < P.AmbushTriggerDistance)
            why = "игрок рядом";
        else if (progress > (Flat(spot) - a.Origin).Dot(a.Dir))
            why = "игрок прошёл мимо";
        if (why is not null)
        {
            a.Sprung = true;
            a.SprungAt = s.Time;
            a.Reason = why;
            if (!there)
                a.Spot = NearestCorrectCover(s, m, null, 8f) ?? m.GlobalPosition; // раскрыт в пути: бьёт из ближайшего укрытия
            AmbushesSprung++;
            GiveSprung(s, m, a);
            return;
        }

        // удержание: точку не пересчитывают; сменить, только если игрок 6 с отступает (точка дальше 20 м)
        // или до точки не дойти
        a.RetreatTime = progress < -1f && Squad.FlatDistance(spot, s.TargetPos) > 20f ? a.RetreatTime + Period : 0f;
        var blocked = !there && TacticUtil.Blocked(s, m);
        if (a.RetreatTime > 6f || blocked)
        {
            Vector3? avoid = blocked ? spot : null;
            var next = ChooseAmbush(s, m, a, false, avoid) ?? ChooseAmbush(s, m, a, true, avoid);
            a.RetreatTime = 0f;
            if (next is { } nx)
                spot = (a.Spot = nx).Value;
            else if (blocked)
            {
                a.Spot = null;
                a.RetryAt = s.Time + 2f;
                Engage(s, m, true, true);
                return;
            }
            there = TacticUtil.Arrived(m, spot);
        }
        if (there && Recheck(s, m, spot, null, out var shifted))
        {
            a.Spot = spot = shifted;
            there = false;
        }
        Give(s, m, new Order
        {
            Role = there ? "Засада: ждёт" : "Засада: идёт", Destination = spot,
            Stance = Stance.Crouch, FireAllowed = false, KeepAway = 12f,
        });
    }

    private void GiveSprung(Squad s, NpcAgent m, AmbushState a) =>
        Give(s, m, new Order
        {
            Role = "Засада: огонь", Destination = a.Spot ?? m.GlobalPosition,
            Stance = Stance.Peek, FireWhileMoving = true, PeekTime = 1.3f, PeekPause = 0.3f,
        });

    private bool IsSprungAmbusher(NpcAgent m) =>
        _role.GetValueOrDefault(m) == Role.Ambusher && _amb.TryGetValue(m, out var a) && a.Sprung;

    /// <summary>Путь игрока: от него к центру давящих (если давящих нет — к остальному отряду).</summary>
    private bool PathLine(Squad s, out Vector3 origin, out Vector3 dir, out float length)
    {
        origin = Flat(s.TargetPos);
        dir = Vector3.Zero;
        length = 0f;
        var group = _role.Where(kv => kv.Value == Role.Engager && !kv.Key.IsDead).Select(kv => kv.Key).ToList();
        if (group.Count == 0)
            group = s.Fighters.Where(x => _role.GetValueOrDefault(x) != Role.Ambusher).ToList();
        if (group.Count == 0)
            return false;
        var e = Vector3.Zero;
        foreach (var x in group)
            e += Flat(x.GlobalPosition);
        e /= group.Count;
        var v = e - origin;
        length = v.Length();
        if (length < 6f)
            return false;
        dir = v / length;
        return true;
    }

    /// <summary>
    /// Точка засады из точек укрытий: проекция на путь игрока в доле 0,3–0,7 от игрока, сбоку 4–8 м,
    /// не ближе дистанции срабатывания + 2 м, укрыта от игрока, маршрут бойца к ней скрыт хотя бы на 60%.
    /// <paramref name="relaxed"/>: доля 0,2–0,8, сбоку 3–10 м, маршрут скрыт на 40%.
    /// Два засадника садятся по разные стороны пути, одиночный — туда, где лучшая точка.
    /// </summary>
    private Vector3? ChooseAmbush(Squad s, NpcAgent m, AmbushState a, bool relaxed, Vector3? avoid)
    {
        if (!PathLine(s, out var origin, out var dir, out var len))
            return null;
        var perp = dir.Cross(Vector3.Up);
        float tMin = relaxed ? 0.2f : 0.3f, tMax = relaxed ? 0.8f : 0.7f;
        float latMin = relaxed ? 3f : 4f, latMax = relaxed ? 10f : 8f, hideMin = relaxed ? 0.4f : 0.6f;
        float? side = null;
        foreach (var (x, st) in _amb)
            if (x != m && !x.IsDead && _role.GetValueOrDefault(x) == Role.Ambusher && st.Spot is not null && !st.Sprung)
                side = -st.Side;
        var claimed = TacticUtil.ClaimedPlus(s, m, avoid);
        var cands = new List<(Vector3 P, float Lat, float S)>();
        foreach (var c in s.World.CoverPoints)
        {
            var rel = Flat(c.Position) - origin;
            var t = rel.Dot(dir) / len;
            var lat = rel.Dot(perp);
            if (t < tMin || t > tMax || Mathf.Abs(lat) < latMin || Mathf.Abs(lat) > latMax)
                continue;
            if (side is { } sd && Mathf.Sign(lat) != sd)
                continue;
            if (rel.Length() < P.AmbushTriggerDistance + 2f)
                continue;
            if (claimed.Any(q => Squad.FlatDistance(q, c.Position) < 2.5f))
                continue;
            var travel = Squad.FlatDistance(c.Position, m.GlobalPosition);
            var score = -Mathf.Abs(t - 0.5f) * 4f - Mathf.Abs(Mathf.Abs(lat) - 6f) * 0.3f - travel / 15f;
            cands.Add((c.Position, lat, score));
        }
        var map = m.GetWorld3D().NavigationMap;
        var checks = 0;
        foreach (var (p, lat, _) in cands.OrderByDescending(x => x.S))
        {
            if (!s.World.IsCoveredFrom(p, s.TargetEye) || !OwnSide(s, p))
                continue;
            if (++checks > 15)
                break;
            if (HiddenPathShare(s, map, m.GlobalPosition, p) < hideMin)
                continue;
            a.Origin = origin;
            a.Dir = dir;
            a.ChosenAt = s.Time;
            a.Side = Mathf.Sign(lat);
            return p;
        }
        return null;
    }

    /// <summary>Доля точек маршрута по навмешу, скрытых от игрока (за препятствием или дальше 18 м).</summary>
    private static float HiddenPathShare(Squad s, Rid map, Vector3 from, Vector3 to)
    {
        var path = NavigationServer3D.MapGetPath(map, from, to, true);
        if (path.Length < 2)
            return 0f;
        var total = 0f;
        for (var i = 1; i < path.Length; i++)
            total += path[i - 1].DistanceTo(path[i]);
        if (total < 0.5f)
            return 1f;
        const int n = 7;
        var hidden = 0;
        for (var k = 1; k <= n; k++)
        {
            var want = total * k / n;
            var p = path[^1];
            for (var i = 1; i < path.Length; i++)
            {
                var seg = path[i - 1].DistanceTo(path[i]);
                if (want <= seg)
                {
                    p = path[i - 1].Lerp(path[i], seg > 0.001f ? want / seg : 0f);
                    break;
                }
                want -= seg;
            }
            p.Y = 0f;
            if (Squad.FlatDistance(p, s.TargetPos) > 18f || s.World.IsCoveredFrom(p, s.TargetEye))
                hidden++;
        }
        return hidden / (float)n;
    }

    // ───────────── давление: игрок сидит на месте ─────────────

    private void UpdatePressure(Squad s, List<NpcAgent> alive, Vector3 front)
    {
        // смельчак давит до конца: ранение не повод вернуться; отбой — цель мертва или ушла далеко
        if (_brave is { } b && (s.Target.IsDead || TacticUtil.DistToTarget(s, b) > _braveAbortFar))
            EndSortie(s);
        // фланговый: вышел на место — бьёт оттуда ограниченное время; не дошёл за 20 с — отбой
        if (_flanker is { } f)
        {
            if (_flankArrivedAt < 0f && TacticUtil.Arrived(f, _flankDest))
                _flankArrivedAt = s.Time;
            var range = Squad.FlatDistance(_flankDest, s.TargetPos);
            var done = _flankArrivedAt >= 0f
                ? s.Time - _flankArrivedAt > 10f || range > 17f || range < 4f
                  || (s.Time - _flankArrivedAt > 2f && TacticUtil.PositionLost(s, f, out _))
                : s.Time - _flankStartedAt > 20f || TacticUtil.Blocked(s, f) || range < 4f;
            if (done || f.Health < f.MaxHealth * 0.3f)
                EndFlank(s, rest: true);
        }

        // таймер «игрок сидит на месте»: сбрасывается, когда игрок сместился или пропал из виду
        var tp = Flat(s.TargetPos);
        if (s.TimeSinceSeen > 2f || Squad.FlatDistance(tp, _campAnchor) > P.CampResetDistance)
        {
            _campAnchor = tp;
            _campTime = 0f;
        }
        else
            _campTime += Period;
        if (_campTime < P.CampTime || s.Time < _pressureRestUntil)
            return;

        var launched = false;
        var wasFlank = false;
        if (_preferFlank && _flanker is null)
            launched = wasFlank = TryFlank(s, alive, front);
        if (!launched && _brave is null && !s.Brave.Active)
            launched = TrySortie(s, alive);
        if (!launched && !_preferFlank && _flanker is null)
            launched = wasFlank = TryFlank(s, alive, front);
        if (launched)
        {
            _preferFlank = !wasFlank; // в следующий раз — другое средство
            _campTime = 0f;
        }
        else
            _campTime = P.CampTime * 0.5f; // некого послать — повторить через полсрока
    }

    /// <summary>Отправить в обход одного бойца: того, кто сидит без дела, и только по скрытому маршруту.</summary>
    private bool TryFlank(Squad s, List<NpcAgent> alive, Vector3 front)
    {
        // засадник сидит на своей точке и в обход не идёт; боец под огнём в обход не выходит
        var candidates = alive.Where(m => _role.GetValueOrDefault(m) is not (Role.Defender or Role.Ambusher)
                                          && m != _brave && !s.Brave.IsOut(m) && !UnderFire(s, m)
                                          && m.Health > m.MaxHealth * 0.4f).ToList();
        foreach (var m in candidates.OrderByDescending(m => Boredom(s, m)))
        {
            // обход имеет смысл, только если кто-то другой продолжает давить на игрока с фронта
            if (!alive.Any(x => x != m && _role.GetValueOrDefault(x) == Role.Engager && x != _brave))
                continue;
            var side = front.SignedAngleTo(Flat(m.GlobalPosition) - Flat(s.TargetPos), Vector3.Up) >= 0f ? 1f : -1f;
            var dest = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye,
                new PositionWeights
                {
                    Angle = 4f, DesiredAngleDir = TacticalQuery.Rotate(front, 150f * side), Cover = 2f, LineOfFire = 1f,
                    Range = 1f, MinRange = 7f, MaxRange = 11f, HardMinRange = 6f, Travel = 0.2f, Spacing = 1f,
                    RequireCover = true, RequireOwnSide = true,
                }, Claimed(s, m, null), maxTravel: 60f);
            if (dest is not { } p)
                continue;
            // точка должна лежать вне «боевого вектора» — клина от игрока в сторону отряда
            var angle = Mathf.RadToDeg(front.AngleTo((Flat(p) - Flat(s.TargetPos)).Normalized()));
            if (angle < 75f || FlankRating(s, m.GlobalPosition, p) < P.MinFlankPathRating)
                continue;
            _flanker = m;
            _flankDest = p;
            _flankStartedAt = s.Time;
            _flankArrivedAt = -1f;
            _shooters.Remove(m);
            Forget(m);
            FlanksStarted++;
            return true;
        }
        return false;
    }

    /// <summary>Насколько боец «без дела»: давно нет выстрела, сидит в засаде, не стреляет.</summary>
    private float Boredom(Squad s, NpcAgent m) =>
        (s.CombatOf(m)?.NoShotTime ?? 0f)
        + (_role.GetValueOrDefault(m) == Role.Ambusher ? 3f : 0f)
        + (_shooters.Contains(m) ? 0f : 2f);

    /// <summary>
    /// Оценка маршрута обхода 0..1: доля точек дуги вокруг игрока (от бойца до точки атаки), которые
    /// скрыты от его глаз. Дуга, выходящая за пределы уровня, не годится.
    /// </summary>
    private static float FlankRating(Squad s, Vector3 from, Vector3 dest)
    {
        var t = Flat(s.TargetPos);
        var a = Flat(from) - t;
        var b = Flat(dest) - t;
        if (a.LengthSquared() < 0.01f || b.LengthSquared() < 0.01f)
            return 0f;
        var a0 = Mathf.Atan2(a.X, a.Z);
        var diff = Mathf.Wrap(Mathf.Atan2(b.X, b.Z) - a0, -Mathf.Pi, Mathf.Pi);
        var r0 = Mathf.Max(a.Length(), FlankKeepAway + 1.5f);
        var r1 = Mathf.Max(b.Length(), 6f);
        const int n = 8;
        int valid = 0, hidden = 0;
        for (var i = 1; i <= n; i++)
        {
            var k = i / (float)n;
            var ang = a0 + diff * k;
            // по пути держится на расстоянии обхода и только в конце подходит к точке атаки
            var r = k < 0.75f ? r0 : Mathf.Lerp(r0, r1, (k - 0.75f) / 0.25f);
            var p = t + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * r;
            if (!s.World.IsOnNavmesh(p, 1.5f))
                continue;
            valid++;
            if (!s.World.ClearLine(s.TargetEye, p + Vector3.Up * 1.0f))
                hidden++;
        }
        return valid < n * 3 / 4 ? 0f : hidden / (float)n;
    }

    private const float FlankKeepAway = 10f;

    private void OrderFlanker(Squad s, NpcAgent f)
    {
        if (_flankArrivedAt >= 0f && Recheck(s, f, _flankDest, null, out var moved))
            _flankDest = moved;
        if (_flankArrivedAt < 0f)
            Give(s, f, new Order
            {
                Role = "Фланговый: обход", Destination = _flankDest, FireAllowed = false,
                Stance = Stance.Crouch, KeepAway = FlankKeepAway,
            });
        else
            Give(s, f, new Order
            {
                Role = "Фланговый: огонь", Destination = _flankDest, FireWhileMoving = true,
                Stance = Stance.Peek, PeekTime = 1.3f, PeekPause = 0.3f,
            });
    }

    private void EndFlank(Squad s, bool rest)
    {
        if (_flanker is { } f)
            Forget(f);
        _flanker = null;
        _flankArrivedAt = -1f;
        if (rest)
            _pressureRestUntil = Mathf.Max(_pressureRestUntil, s.Time + P.EngagerReplacementDelay * 0.5f);
    }

    /// <summary>Отправить на штурм одного смелого бойца (личная черта «смелость» из слоя «Смельчак»).</summary>
    private bool TrySortie(Squad s, List<NpcAgent> alive)
    {
        var c = alive.Where(m => _role.GetValueOrDefault(m) is not (Role.Defender or Role.Ambusher) && m != _flanker
                                 && !UnderFire(s, m) && s.Brave.Bravery(m) >= BraveLayer.BraveFrom && m.Health > m.MaxHealth * 0.5f)
            .OrderByDescending(m => s.Brave.Bravery(m)).FirstOrDefault();
        if (c is null)
            return false;
        _brave = c;
        _braveAbortFar = BraveLayer.AbortFrom(TacticUtil.DistToTarget(s, c));
        _assault.Forget(c);
        Forget(c);
        BraveLayer.SortiesStarted++;
        return true;
    }

    private void EndSortie(Squad s)
    {
        if (_brave is { } b)
        {
            _assault.Forget(b);
            Forget(b);
        }
        _brave = null;
        _pressureRestUntil = Mathf.Max(_pressureRestUntil, s.Time + P.EngagerReplacementDelay);
    }

    // ───────────── стрелки ─────────────

    /// <summary>
    /// Право огня: координатор ранжирует бойцов и выбирает лучших (ближний к игроку — первый).
    /// Стрелок отдаёт право, когда потерял выстрел, получил попадание или «перезаряжается».
    /// Самооборона права огня не требует (это правило уровня «боец в бою»).
    /// </summary>
    private void UpdateShooters(Squad s, List<NpcAgent> alive)
    {
        var eligible = new List<NpcAgent>();
        foreach (var m in alive)
        {
            var hp = m.Health;
            var hit = hp < _lastHp.GetValueOrDefault(m, hp) - 0.5f;
            _lastHp[m] = hp;
            var canHit = s.CanHit(m);
            // выстрел есть сейчас, или есть откуда выглянуть, или боец на ходу
            var ok = s.OrderFor(m).FireAllowed && (canHit || (s.CombatOf(m)?.NoShotTime ?? 0f) < 1.5f);
            if (ok)
                eligible.Add(m);
            if (!_shooters.Contains(m))
                continue;
            if (!ok)
                _shooters.Remove(m);
            else if (hit)
                Release(s, m); // реакция на попадание: спрятаться, огонь ведёт другой
        }
        _shooters.RemoveWhere(m => m.IsDead || !alive.Contains(m));

        // сработавшая засада стреляет первой: при полном составе стрелков право отдаёт самый давний
        foreach (var m in eligible.Where(m => IsSprungAmbusher(m) && !_shooters.Contains(m)).ToList())
        {
            if (_shooters.Count >= P.MaxShooters)
            {
                var drop = _shooters.Where(x => !IsSprungAmbusher(x) && x != _brave)
                    .OrderBy(x => _shooterSince.GetValueOrDefault(x)).FirstOrDefault();
                if (drop is null)
                    break;
                Release(s, drop);
            }
            Grant(s, m);
        }

        var waiting = eligible.Where(m => !_shooters.Contains(m) && s.Time >= _shooterRestUntil.GetValueOrDefault(m, -1f))
            .OrderBy(m => ShooterCost(s, m)).ToList();
        // «перезарядка»: кто держит право дольше срока, уступает, если есть кому
        if (waiting.Count > 0 && _shooters.Count >= P.MaxShooters)
        {
            var tired = _shooters.Where(m => m != _brave && !IsSprungAmbusher(m)
                                             && s.Time - _shooterSince.GetValueOrDefault(m) > P.ShooterHoldTime)
                .OrderBy(m => _shooterSince.GetValueOrDefault(m)).FirstOrDefault();
            if (tired is not null)
                Release(s, tired);
        }
        foreach (var m in waiting)
        {
            if (_shooters.Count >= P.MaxShooters)
                break;
            Grant(s, m);
        }
        // стрелков меньше минимума — отдых не соблюдается, чтобы огонь не затихал
        if (_shooters.Count < P.MinShooters)
            foreach (var m in eligible.Where(m => !_shooters.Contains(m)).OrderBy(m => ShooterCost(s, m)).ToList())
            {
                if (_shooters.Count >= P.MinShooters)
                    break;
                Grant(s, m);
            }
    }

    private float ShooterCost(Squad s, NpcAgent m) =>
        TacticUtil.DistToTarget(s, m) - (s.CanHit(m) ? 5f : 0f) - (m == _brave ? 10f : 0f);

    private void Grant(Squad s, NpcAgent m)
    {
        _shooters.Add(m);
        _shooterSince[m] = s.Time;
    }

    private void Release(Squad s, NpcAgent m)
    {
        _shooters.Remove(m);
        _shooterRestUntil[m] = s.Time + P.ShooterRestTime;
    }

    // ───────────── экран арены ─────────────

    /// <summary>Строка для экрана арены: роли, таймер сидения игрока, фланговый, смельчак, стрелки.</summary>
    public string Status(Squad s)
    {
        string Num(NpcAgent m) => (s.Members.ToList().IndexOf(m) + 1).ToString();
        string Who(Role r)
        {
            var l = _role.Where(kv => kv.Value == r && !kv.Key.IsDead).Select(kv => Num(kv.Key)).OrderBy(x => x).ToList();
            return l.Count > 0 ? string.Join(",", l) : "—";
        }
        var text = $"Роли: давят {Who(Role.Engager)}; засада {Who(Role.Ambusher)}; держат позиции {Who(Role.Defender)}" +
                   $"\nИгрок на месте {Mathf.Min(_campTime, P.CampTime):0}/{P.CampTime:0} с; " +
                   $"фланговый: {(_flanker is { } f ? Num(f) : "—")}; смельчак: {(_brave is { } b ? Num(b) : "—")}; " +
                   $"стреляют: {(_shooters.Count > 0 ? string.Join(",", _shooters.Select(m => Num(m)).OrderBy(x => x)) : "—")} (не больше {P.MaxShooters})";
        var amb = _amb.Where(kv => !kv.Key.IsDead && _role.GetValueOrDefault(kv.Key) == Role.Ambusher)
            .Select(kv => $"{Num(kv.Key)} " + (kv.Value.Sprung ? $"сработала ({kv.Value.Reason})"
                : kv.Value.Spot is not { } sp ? "нет точки, давит"
                : TacticUtil.Arrived(kv.Key, sp) ? "ждёт" : "идёт"))
            .ToList();
        text += $"\nЗасада: {(amb.Count > 0 ? string.Join("; ", amb) : "—")}" +
                (s.Time < _noAmbusherUntil ? $" (новая через {_noAmbusherUntil - s.Time:0} с)" : "");
        if (_overflow > 0)
            text += $"\nВНИМАНИЕ: {_overflow} бойцам не хватило ролей, они давят сверх нормы. Увеличьте максимум у позиций.";
        return text;
    }
}
