using Godot;
using Zero.Actors;
using Zero.Ai.Actions;
using Zero.Ai.Nodes;

namespace Zero.Ai.Tactics;

/// <summary>
/// Уровень «боец в бою». Одинаков для всех тактик — тактики различаются только приказами,
/// поэтому сравнение честное. Приказ отряда — лишь одна из целей бойца (как в F.E.A.R., где
/// у каждого солдата свои цели и приказ отряда их не отменяет):
///
/// 1. <b>Стрельба по видимой части тела.</b> Боец ищет, куда выстрелить: грудь, голова, ноги, плечо.
///    Игрок выглянул из-за укрытия — по нему стреляют.
/// 2. <b>Самооборона важнее приказа.</b> Если цель ближе <see cref="CloseRange"/> м и видит бойца,
///    если его обошли и укрытие простреливается, или по нему попали — он отстреливается,
///    даже на перебежке и даже без разрешения отряда; вблизи смещается боком с огнём.
/// 3. <b>Работа с укрытием.</b> Стойка «выглядывать»: за низким укрытием встаёт и стреляет,
///    за высоким — шаг вбок из-за края, выстрел и назад (<see cref="TacticalWorld.PeekSpot"/>).
/// 4. <b>Под огнём не стоит столбом.</b> Ранен на открытом месте — пригибается и уходит в ближайшее
///    укрытие на несколько секунд, отстреливаясь.
/// </summary>
public sealed class CombatOrderAction : NpcAction
{
    public const float CloseRange = 6f;
    /// <summary>Подавлен: рядом пролетела пуля меньше <see cref="SuppressTime"/> с назад.</summary>
    public const float SuppressTime = 1.2f;
    /// <summary>Множитель точности под подавлением.</summary>
    public const float SuppressedAccuracy = 0.6f;

    private enum Peek { Hidden, SideOut, Up, SideBack }

    private readonly Squad _squad;
    private Order _order = new();
    private float _sinceHit = 99f;
    private float _sinceShotAt = 99f;
    private float _exposedFor;

    // выглядывание
    private Peek _peek = Peek.Hidden;
    private float _peekUp;
    private float _peekCooldown;
    private Vector3? _peekSpot;
    private Vector3? _peekSpotFor;
    private float _peekSpotAge;

    // уход из-под огня
    private Vector3? _evadeTo;
    private float _evadeLeft;

    // стрейф вблизи
    private float _strafeSide = 1f;
    private float _strafeTimer;

    public CombatOrderAction(Squad squad) => _squad = squad;

    public bool Suppressed => _sinceShotAt < SuppressTime;
    /// <summary>Боец сейчас отбивается сам, поверх приказа.</summary>
    public bool SelfDefense { get; private set; }
    /// <summary>Сколько секунд боец стоит на позиции и не имеет никакого выстрела (сигнал тактике сменить точку).</summary>
    public float NoShotTime { get; private set; }
    /// <summary>Сколько секунд подряд цель видит бойца, пока он на позиции (укрытие обошли).</summary>
    public float FlankedTime { get; private set; }
    /// <summary>Боец выглядывает из укрытия (вверх или вбок) или возвращается за него.</summary>
    public bool Peeking => _peek != Peek.Hidden;
    /// <summary>Сколько секунд боец пытается идти, но стоит на месте.</summary>
    public float StuckTime { get; private set; }
    /// <summary>Вероятность попадания последнего выстрела (для подписи и отчётов).</summary>
    public float LastHitChance { get; private set; }

    public override string Name
    {
        get
        {
            var suffix = _evadeLeft > 0f ? " (уходит в укрытие)"
                : SelfDefense ? " (отбивается)"
                : Suppressed ? " (подавлен)"
                : _peek != Peek.Hidden ? " (выглядывает)"
                : "";
            return _order.Role + suffix;
        }
    }

    public override void Start(NpcAgent npc)
    {
        npc.Damaged += OnDamaged;
        npc.ShotAt += OnShotAt;
        _squad.Register(npc, this);
    }

    public override void Stop(NpcAgent npc)
    {
        npc.Damaged -= OnDamaged;
        npc.ShotAt -= OnShotAt;
        npc.StopMoving();
        npc.ClearAim();
    }

    private void OnDamaged(ActorBody a, float amount, Node? source)
    {
        _sinceHit = 0f;
    }
    private void OnShotAt(ActorBody a, ActorBody shooter) => _sinceShotAt = 0f;

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        _order = _squad.OrderFor(npc);
        _sinceHit += dt;
        _sinceShotAt += dt;
        var target = _squad.Target;
        if (target.IsDead)
        {
            npc.StopMoving();
            npc.ClearAim();
            return ActionStatus.Running;
        }
        var world = _squad.World;

        // ───── оценка обстановки ─────
        var dist = Squad.FlatDistance(npc.GlobalPosition, target.GlobalPosition);
        var seen = world.ClearLine(target.EyePosition, npc.CenterMass); // цель видит мой корпус
        _exposedFor = seen ? _exposedFor + dt : 0f;
        var dest = _order.Destination;
        // выглянувший вбок тоже «на позиции», хоть и отошёл от точки на шаг
        var atDest = dest is null || _peek != Peek.Hidden || Squad.FlatDistance(npc.GlobalPosition, dest.Value) < 1.0f;
        var holdsCover = atDest && _order.Stance != Stance.Stand;
        // «обошли» — когда от цели видно саму точку укрытия, а не бойца: боец, по которому попали, пока он
        // выглядывал или возвращался из-за угла, на виду законно, и это не обход (ложные срабатывания на плейтесте)
        // с флагом «прятаться» укрытие считается своим, только если его закрывает собственное препятствие
        // (IsCoveredFrom засчитывал и дальние препятствия у цели)
        // штурм до конца: смельчак не прячется, даже если тактика ставит «прятаться»
        var push = _order.Push;
        var hide = _order.HideWhenHit && !push;
        FlankedTime = holdsCover && _peek == Peek.Hidden && dest is { } cp
            && !(hide ? world.IsOwnSideCover(cp, target.GlobalPosition, target.EyePosition)
                      : world.IsCoveredFrom(cp, target.EyePosition)) ? FlankedTime + dt : 0f;

        var close = dist < CloseRange && seen;
        var flanked = FlankedTime > 0.3f;
        var hurt = _sinceHit < 1.5f && seen && _peek == Peek.Hidden;
        // с флагом попадание не поднимает бойца отбиваться: он прячется (самооборона — только вплотную и при обходе)
        SelfDefense = close || flanked || (hurt && !hide);
        // вплотную не прячется: стоит и стреляет до конца
        if (hide && !close && _sinceHit <= dt)
            DuckAfterHit(npc);

        // ранен на открытом месте, стоя по приказу, — уйти в ближайшее укрытие
        // (но не вплотную — убегать бесполезно — и не на штурме)
        _evadeLeft -= dt;
        if (_evadeLeft <= 0f || close || push)
            _evadeTo = null;
        if (_sinceHit < 0.1f && _evadeTo is null && atDest && _order.Stance == Stance.Stand && !close && !push
            && !world.IsCoveredFrom(npc.GlobalPosition, target.EyePosition))
        {
            _evadeTo = TacticalQuery.FindBest(world, npc.GlobalPosition, target.GlobalPosition, target.EyePosition,
                new PositionWeights
                {
                    Travel = 3f, Range = 0.5f, MinRange = 4f, MaxRange = 25f, Spacing = 0.5f, LineOfFire = 0.5f,
                    RequireCover = true,
                }, _squad.ClaimedExcept(npc), maxTravel: 8f);
            if (_evadeTo is not null)
            {
                _evadeLeft = 3f;
                CombatStats.Evades++;
            }
        }

        // ───── движение ─────
        UpdatePeekSpot(dest, dt);
        Vector3? navTarget = dest;
        var gait = _order.Gait;
        if (_evadeTo is { } ev)
            navTarget = ev;
        else if (_peek == Peek.Hidden && dest is { } far && _order.KeepAway > 0f && !atDest)
            navTarget = RouteAround(world, npc.GlobalPosition, far, target.GlobalPosition, _order.KeepAway);
        else if (_peek is Peek.SideOut or Peek.Up && _peekSpot is { } ps)
        {
            navTarget = ps;
            gait = Gait.Walk;
        }
        // бой вплотную: приказ уйти дальше от цели (смена укрытия, отход раненого) не исполняется — боец
        // остаётся и дерётся (стрейфом, если нечем укрыться). Это правило уровня бойца, для всех тактик.
        var holdGround = close && navTarget is { } goingTo
                         && Squad.FlatDistance(goingTo, target.GlobalPosition) > dist + 0.5f;
        if (holdGround)
            navTarget = null;

        var moving = false;
        var strafing = false;
        if (navTarget is { } nt)
        {
            var r = npc.NavigateTo(nt, gait, _peek == Peek.Hidden && _order.Stance == Stance.Stand ? 0.6f : 0.3f);
            moving = r == NavResult.Moving;
            // приказано идти, а боец стоит (упёрся в своего, точка занята) — сигнал тактике
            StuckTime = moving && npc.HorizontalSpeed < 0.4f ? StuckTime + dt : 0f;
            if (_peek == Peek.SideOut && r != NavResult.Moving)
            {
                _peek = Peek.Up; // вышел из-за края
                _peekUp = _order.PeekTime;
            }
            if (_peek == Peek.SideBack && r != NavResult.Moving)
                _peek = Peek.Hidden;
        }
        else
            npc.StopMoving();

        // вблизи на месте — не стоять мишенью, а смещаться боком, отстреливаясь
        if (close && !moving && _peek == Peek.Hidden && _evadeTo is null)
        {
            strafing = Strafe(npc, target, holdGround ? null : dest, dt);
            moving = strafing;
        }

        // ───── стойка ─────
        CombatStats.FighterTime += dt;
        if (seen && !moving && _peek == Peek.Hidden && !SelfDefense)
            CombatStats.OpenStillTime += dt;
        // проверка замечаний плейтеста: толпятся ли у одного укрытия и срезают ли обход мимо цели
        if (!moving)
            foreach (var other in _squad.Fighters)
                if (other != npc && Squad.FlatDistance(other.GlobalPosition, npc.GlobalPosition) < 1.6f)
                {
                    CombatStats.CrowdTime += dt;
                    break;
                }
        if (_order.KeepAway > 0f && !atDest && dist < _order.KeepAway - 1.5f)
            CombatStats.TooCloseTime += dt;

        var aimPart = _squad.AimPartFor(npc);
        UpdateStance(npc, moving, holdsCover && !strafing && _evadeTo is null, aimPart is not null, dt);
        aimPart = _squad.AimPartFor(npc); // после смены стойки ствол на другой высоте

        // ───── огонь ─────
        // токены огня (настройка сложности): по приказу одновременно стреляют не больше Squad.MaxShooters
        var fireAllowed = SelfDefense || (_order.FireAllowed && _squad.MayShoot(npc));
        var fireOnMove = _order.FireWhileMoving || SelfDefense || _evadeTo is not null || _peek != Peek.Hidden;
        var mayFire = fireAllowed && aimPart is not null && (!moving || fireOnMove);
        var near = dist < 22f;

        if (mayFire || (!moving && near) || SelfDefense || _peek != Peek.Hidden)
            npc.AimAt(target.CenterMass);
        else
            npc.ClearAim();

        // пристрелка: время непрерывного прицеливания по цели; потерял её больше чем на секунду — заново
        if (aimPart is not null)
        {
            _aimTime += dt;
            _lostFor = 0f;
        }
        else if ((_lostFor += dt) > 1f)
        {
            _aimTime = 0f;
            _warningShotDone = false;
        }

        // нет выстрела ни сейчас, ни из точки выглядывания — тактике пора сменить позицию
        if (aimPart is null && !moving && atDest && _peekSpot is null)
            NoShotTime += dt;
        else if (aimPart is not null || moving)
            NoShotTime = 0f;

        if (mayFire && npc.CanFire && npc.IsFacing(target.GlobalPosition, 12f))
        {
            var odds = AccuracyModel.Evaluate(world, npc, target, _aimTime, moving);
            // первый выстрел после обнаружения — предупредительный промах
            var chance = odds.Chance * (Suppressed ? SuppressedAccuracy : 1f);
            var hit = _warningShotDone && _squad.Rng.NextDouble() < chance;
            _warningShotDone = true;
            npc.Fire(AccuracyModel.AimPoint(_squad.Rng, npc, target, hit, aimPart));
            LastHitChance = odds.Chance;
            CombatStats.Shots++;
            if (SelfDefense && !_order.FireAllowed)
                CombatStats.SelfDefenseShots++;
            if (_peek == Peek.Up)
                _peekUp = Mathf.Min(_peekUp, 0.25f); // выстрелил — скоро назад
        }
        return ActionStatus.Running;
    }

    private float _aimTime;
    private float _lostFor;
    private bool _warningShotDone;
    private float _sideOutFor;

    /// <summary>Попали — прервать выглядывание и сесть в укрытие; следующее выглядывание не раньше чем через 1,75 с.</summary>
    private void DuckAfterHit(NpcAgent npc)
    {
        if (_peek is Peek.SideOut or Peek.Up)
        {
            var side = _peekSpot is { } p && Squad.FlatDistance(p, _peekSpotFor ?? p) > 0.35f;
            _peek = side ? Peek.SideBack : Peek.Hidden;
        }
        if (_peek == Peek.Hidden)
            npc.SetCrouching(true);
        _peekCooldown = Mathf.Max(_peekCooldown, 1.75f);
    }

    private void UpdatePeekSpot(Vector3? dest, float dt)
    {
        _peekSpotAge += dt;
        if (dest is not { } d || _order.Stance != Stance.Peek)
        {
            _peekSpot = null;
            _peekSpotFor = null;
            if (_peek != Peek.Hidden && _peek != Peek.SideBack)
                _peek = Peek.Hidden;
            return;
        }
        var changed = _peekSpotFor is not { } f || Squad.FlatDistance(f, d) > 0.3f;
        if (changed)
            _peek = Peek.Hidden;
        // пока боец снаружи, точку не пересчитываем, чтобы не дёргался
        if ((changed || _peekSpotAge > 1f) && _peek == Peek.Hidden)
        {
            _peekSpot = _squad.World.PeekSpot(d, _squad.TargetPos);
            _peekSpotFor = d;
            _peekSpotAge = 0f;
        }
    }

    /// <summary>
    /// Обход цели по дуге: если прямой путь к точке проходит ближе <paramref name="keep"/> м от цели,
    /// идти к промежуточной точке на окружности вокруг цели (шаг 35° в сторону назначения).
    /// Без этого фланговый бежал к своему «флангу» прямо сквозь игрока.
    /// </summary>
    private static Vector3 RouteAround(TacticalWorld world, Vector3 from, Vector3 to, Vector3 target, float keep)
    {
        var a = new Vector2(from.X - target.X, from.Z - target.Z);
        var b = new Vector2(to.X - target.X, to.Z - target.Z);
        var ab = b - a;
        var t = ab.LengthSquared() > 0.001f ? Mathf.Clamp(-a.Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        if ((a + ab * t).Length() >= keep || a.Length() < 0.1f || b.Length() < 0.1f)
            return to;
        var diff = Mathf.Wrap(b.Angle() - a.Angle(), -Mathf.Pi, Mathf.Pi);
        var step = Mathf.DegToRad(35f);
        if (Mathf.Abs(diff) <= step)
            return to;
        var ang = a.Angle() + Mathf.Sign(diff) * step;
        var radius = Mathf.Max(keep + 1.5f, Mathf.Min(a.Length(), 20f));
        var p = target + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
        p.Y = from.Y;
        return world.SnapToNav(p);
    }

    /// <summary>Шаг вбок вокруг своей точки (в пределах 2.5 м), меняя сторону, если упёрся.</summary>
    private bool Strafe(NpcAgent npc, ActorBody target, Vector3? anchor, float dt)
    {
        _strafeTimer -= dt;
        var to = target.GlobalPosition - npc.GlobalPosition;
        to.Y = 0f;
        if (to.LengthSquared() < 0.01f)
            return false;
        var side = to.Normalized().Cross(Vector3.Up) * _strafeSide;
        var next = npc.GlobalPosition + side * 0.8f;
        var tooFar = anchor is { } a && Squad.FlatDistance(next, a) > 2.5f;
        if (_strafeTimer <= 0f || tooFar || !_squad.World.IsOnNavmesh(next, 0.35f))
        {
            _strafeSide = -_strafeSide;
            _strafeTimer = 1.2f + (float)_squad.Rng.NextDouble() * 0.8f;
            side = -side;
            next = npc.GlobalPosition + side * 0.8f;
            if (!_squad.World.IsOnNavmesh(next, 0.35f))
                return false;
        }
        npc.MoveDirect(side, Gait.Walk);
        return true;
    }

    private void UpdateStance(NpcAgent npc, bool moving, bool holdsCover, bool canHitNow, float dt)
    {
        if (moving)
        {
            npc.SetCrouching(false);
            return;
        }
        if (SelfDefense && !holdsCover)
        {
            // отбивается на месте: стоит, если присев не попасть
            npc.SetCrouching(canHitNow && npc.IsCrouching);
            return;
        }
        switch (_order.Stance)
        {
            case Stance.Stand:
                // под огнём пригибается, даже если приказано стоять (кроме штурма до конца)
                npc.SetCrouching(Suppressed && !_order.Push);
                break;
            case Stance.Crouch:
                npc.SetCrouching(!SelfDefense || canHitNow);
                break;
            case Stance.Peek:
                TickPeek(npc, canHitNow, dt);
                break;
        }
    }

    private void TickPeek(NpcAgent npc, bool canHitNow, float dt)
    {
        _peekCooldown -= dt;
        switch (_peek)
        {
            case Peek.Hidden:
                // обошли с фланга: укрытие не закрывает — отбиваться прямо отсюда
                if (SelfDefense)
                {
                    npc.SetCrouching(canHitNow && npc.IsCrouching);
                    return;
                }
                npc.SetCrouching(true);
                if (_peekCooldown <= 0f && !Suppressed && npc.CanFire && _order.FireAllowed && _peekSpot is { } ps)
                {
                    if (Squad.FlatDistance(ps, _peekSpotFor ?? ps) < 0.35f)
                    {
                        npc.SetCrouching(false); // низкое укрытие — просто встать
                        CombatStats.PeekUp++;
                        _peek = Peek.Up;
                        _peekUp = _order.PeekTime;
                    }
                    else
                    {
                        _peek = Peek.SideOut; // высокое — шаг вбок из-за края
                        _sideOutFor = 0f;
                        CombatStats.PeekSide++;
                    }
                }
                break;
            case Peek.SideOut:
                npc.SetCrouching(false);
                // с флагом «прятаться»: до края не дошёл за отведённое время — не торчать на открытом месте
                _sideOutFor += dt;
                if (_order.HideWhenHit && _sideOutFor > _order.PeekTime + 0.8f)
                {
                    _peek = Peek.SideBack;
                    _peekCooldown = _order.PeekPause;
                }
                break;
            case Peek.Up:
                npc.SetCrouching(false);
                _peekUp -= dt;
                // спрятаться после выстрела, по таймеру или сразу после попадания по себе
                if (_peekUp <= 0f || _sinceHit < 0.15f)
                {
                    var side = _peekSpot is { } p && Squad.FlatDistance(p, _peekSpotFor ?? p) > 0.35f;
                    _peek = side ? Peek.SideBack : Peek.Hidden;
                    if (!side)
                        npc.SetCrouching(true);
                    _peekCooldown = Suppressed ? _order.PeekPause * 2f : _order.PeekPause;
                }
                break;
            case Peek.SideBack:
                npc.SetCrouching(false);
                break;
        }
    }
}


/// <summary>Счётчики поведения бойцов за раунд (для отчёта сравнения тактик).</summary>
public static class CombatStats
{
    public static int PeekUp, PeekSide, Shots, SelfDefenseShots, Evades;
    /// <summary>Боец-секунды в бою и из них — стоит на месте на виду у цели, не выглядывая и не отбиваясь.</summary>
    public static float FighterTime, OpenStillTime;
    /// <summary>Боец-секунды: стоит ближе 1.6 м к своему (толпятся); идёт в обход, но ближе к цели, чем приказано.</summary>
    public static float CrowdTime, TooCloseTime;

    public static void Reset()
    {
        PeekUp = PeekSide = Shots = SelfDefenseShots = Evades = 0;
        FighterTime = OpenStillTime = CrowdTime = TooCloseTime = 0f;
    }
}
