using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Actors;
using Zero.Ai.Nodes;

namespace Zero.Ai.Tactics;

/// <summary>
/// Номера тактик (клавиши 1–4 на арене). «Кольцо с токенами» убрано после плейтеста: токены огня —
/// теперь настройка сложности отряда (<see cref="Squad.MaxShooters"/>), а не отдельная тактика.
/// Штурм остался контрольной группой и общей фазой «финальный рывок» (<see cref="AssaultRun"/>).
/// «Оборона с вылазками» убрана: смельчаки стали опцией поверх любой тактики (<see cref="BraveLayer"/>).
/// Пятая, «Роли», — схема Uncharted 4: бойцы одновременно получают разные роли (<see cref="RolesTactic"/>).
/// </summary>
public enum TacticKind { Rush, BoundingOverwatch, Flank, DefensiveCover, Roles }

public static class TacticFactory
{
    public static ITactic Create(TacticKind kind) => kind switch
    {
        TacticKind.BoundingOverwatch => new BoundingOverwatchTactic(),
        TacticKind.Flank => new FlankTactic(),
        TacticKind.DefensiveCover => new DefensiveCoverTactic(),
        TacticKind.Roles => new RolesTactic(),
        _ => new RushTactic(),
    };
}

/// <summary>Базовый класс: перепланирование не каждый кадр, а с периодом.</summary>
public abstract class PeriodicTactic : ITactic
{
    public abstract string Name { get; }
    protected virtual float Period => 0.4f;
    private float _timer;

    public virtual void Begin(Squad squad)
    {
        _timer = 0f;
        Replan(squad);
    }

    public void Update(Squad squad, float dt)
    {
        _timer += dt;
        if (_timer < Period)
            return;
        _timer = 0f;
        Replan(squad);
    }

    protected abstract void Replan(Squad squad);

    protected static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Общие проверки для тактик поверх уровня «боец в бою».</summary>
internal static class TacticUtil
{
    public static bool Arrived(NpcAgent m, Vector3 p, float tol = 1.1f) => Squad.FlatDistance(m.GlobalPosition, p) < tol;

    public static float DistToTarget(Squad s, NpcAgent m) => Squad.FlatDistance(m.GlobalPosition, s.TargetPos);

    /// <summary>
    /// Позицию пора менять: укрытие обошли (от цели видно саму точку дольше 1 с, а цель не вплотную —
    /// вплотную боец отбивается, а не бежит), или с позиции давно нет выстрела.
    /// </summary>
    public static bool PositionLost(Squad s, NpcAgent m, out string why)
    {
        why = "";
        var c = s.CombatOf(m);
        if (c is null || InCloseFight(s, m))
            return false;
        if (c.FlankedTime > 1f && DistToTarget(s, m) >= CombatOrderAction.CloseRange)
        {
            why = "укрытие обошли";
            return true;
        }
        if (c.NoShotTime > 3.5f)
        {
            why = "отсюда нет выстрела";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Бой вплотную (плейтест: «если укрытие запушили в упор, не надо перебегать — драться до конца»):
    /// цель ближе <see cref="CombatOrderAction.CloseRange"/> м и видит бойца или его укрытие обошли.
    /// Тогда никаких смен укрытия и отходов: боец стоит и стреляет (самооборона).
    /// </summary>
    public static bool InCloseFight(Squad s, NpcAgent m) =>
        DistToTarget(s, m) < CombatOrderAction.CloseRange
        && (s.World.ClearLine(s.Target.EyePosition, m.CenterMass) || (s.CombatOf(m)?.FlankedTime ?? 0f) > 0f);

    /// <summary>Боец идёт к точке, но стоит: упёрся в своего или место занято — точку надо сменить.</summary>
    public static bool Blocked(Squad s, NpcAgent m) => (s.CombatOf(m)?.StuckTime ?? 0f) > 1.5f;

    /// <summary>Занятые точки плюс ещё одна (старая точка, в которую не удалось пройти).</summary>
    public static List<Vector3> ClaimedPlus(Squad s, NpcAgent m, Vector3? extra)
    {
        var list = s.ClaimedExcept(m);
        if (extra is { } e)
            list.Add(e);
        return list;
    }

    /// <summary>Перевес отряда: цель серьёзно ранена, или ранена и бойцов ещё трое.</summary>
    public static bool Advantage(Squad s)
    {
        var hp = s.Target.Health / Mathf.Max(1f, s.Target.MaxHealth);
        return hp < 0.35f || (hp < 0.55f && s.Fighters.Count() >= 3);
    }
}

/// <summary>
/// Штурм как фаза, общая для тактик: рывками от укрытия к укрытию (пауза ~1 с с выстрелом),
/// последние метры — в упор. Используется «Штурмом» целиком, а остальными — как финальный рывок
/// при перевесе и как вылазка смелых бойцов.
/// </summary>
internal sealed class AssaultRun
{
    private readonly Dictionary<NpcAgent, Vector3> _hop = new();
    private readonly Dictionary<NpcAgent, float> _pause = new();
    private readonly HashSet<NpcAgent> _final = new();

    /// <summary>
    /// Штурм до конца (смельчак, финальный рывок; плейтест): не от укрытия к укрытию, а на огневую
    /// позицию — точку, откуда есть выстрел по видимой части цели; цель за укрытием — обойти укрытие.
    /// Без него — прежний штурм рывками (контрольная группа «Штурм»).
    /// </summary>
    private readonly bool _push;
    private readonly Dictionary<NpcAgent, Vector3> _attack = new();
    private readonly Dictionary<NpcAgent, float> _attackAge = new();
    private readonly Dictionary<NpcAgent, float> _noShotThere = new();

    public AssaultRun(bool push = false) => _push = push;

    /// <summary>На огневой позиции — не дальше этого от цели (дальше подходит).</summary>
    public const float FireRange = 7f;

    public void Clear()
    {
        _hop.Clear();
        _pause.Clear();
        _final.Clear();
        _attack.Clear();
        _attackAge.Clear();
        _noShotThere.Clear();
    }

    public void Forget(NpcAgent m)
    {
        _hop.Remove(m);
        _pause.Remove(m);
        _final.Remove(m);
        _attack.Remove(m);
        _attackAge.Remove(m);
        _noShotThere.Remove(m);
    }

    /// <summary>Есть ли из точки (стоя, ствол на 1,4 м) выстрел по видимой части цели.</summary>
    private static bool ShotFrom(Squad s, Vector3 feet, NpcAgent self) =>
        s.World.VisiblePart(feet + Vector3.Up * 1.4f, s.Target, self.GetRid()) is not null;

    private Order PushNext(Squad s, NpcAgent m, float period, string name)
    {
        var dist = TacticUtil.DistToTarget(s, m);
        var target = s.TargetPos;
        var toMe = new Vector3(m.GlobalPosition.X - target.X, 0f, m.GlobalPosition.Z - target.Z);
        toMe = toMe.LengthSquared() > 0.01f ? toMe.Normalized() : Vector3.Back;
        var flatTarget = new Vector3(target.X, 0f, target.Z);

        // есть выстрел и цель рядом — стоять и стрелять, дальше 5 м — поджимать к ~4 м
        if (dist <= FireRange && ShotFrom(s, m.GlobalPosition, m))
        {
            _attack.Remove(m);
            _noShotThere.Remove(m);
            var dest = dist > 5f ? s.World.SnapToNav(flatTarget + toMe * 4f) : m.GlobalPosition;
            return new Order
            {
                Role = name + ": огонь в упор", Destination = dest, Gait = Gait.Run,
                FireWhileMoving = true, Stance = Stance.Stand, Push = true,
            };
        }

        // нет выстрела — огневая позиция на дуге вокруг цели (обойти её укрытие); пересчёт раз в секунду
        // или если на позиции выстрела нет дольше 0,6 с («не стоять у обратной стороны укрытия»)
        _attackAge[m] = _attackAge.GetValueOrDefault(m) + period;
        var has = _attack.TryGetValue(m, out var spot);
        if (has && TacticUtil.Arrived(m, spot, 0.8f))
            _noShotThere[m] = _noShotThere.GetValueOrDefault(m) + period;
        Vector3? avoid = has && _noShotThere.GetValueOrDefault(m) > 0.6f ? spot : null;
        if (!has || _attackAge[m] >= 1f || avoid is not null || (has && !ShotFrom(s, spot, m)))
        {
            spot = AttackPoint(s, m, avoid) ?? s.World.SnapToNav(flatTarget + toMe * 4f);
            _attack[m] = spot;
            _attackAge[m] = 0f;
            _noShotThere[m] = 0f;
        }
        return new Order
        {
            Role = name + (dist > 9f ? ": штурм" : ": обходит укрытие"), Destination = spot, Gait = Gait.Run,
            FireWhileMoving = true, Stance = Stance.Stand, Push = true,
        };
    }

    /// <summary>
    /// Огневая позиция для штурма: точка навмеша на кольце 3–6 м вокруг цели, из которой есть выстрел;
    /// из таких — ближайшая к бойцу (короткий обход укрытия). Если цель закрыта с двух сторон, годится
    /// любая доступная сторона.
    /// </summary>
    private static Vector3? AttackPoint(Squad s, NpcAgent m, Vector3? avoid)
    {
        var t = new Vector3(s.TargetPos.X, 0f, s.TargetPos.Z);
        Vector3? best = null;
        var bestCost = float.MaxValue;
        foreach (var r in new[] { 4.5f, 3.5f, 6f })
            for (var i = 0; i < 20; i++)
            {
                var a = Mathf.Tau * i / 20f;
                var p = t + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r;
                if (!s.World.IsOnNavmesh(p, 0.4f))
                    continue;
                if (avoid is { } av && Squad.FlatDistance(av, p) < 1.5f)
                    continue;
                var cost = Squad.FlatDistance(p, m.GlobalPosition) + Mathf.Abs(r - 4.5f) * 0.5f;
                if (cost >= bestCost || !ShotFrom(s, p, m))
                    continue;
                bestCost = cost;
                best = p;
            }
        return best;
    }

    public Order Next(Squad s, NpcAgent m, float period, string name)
    {
        if (_push)
            return PushNext(s, m, period, name);
        var dist = TacticUtil.DistToTarget(s, m);
        if (!_final.Contains(m) && dist >= 9f)
        {
            var has = _hop.TryGetValue(m, out var hop);
            if (has && TacticUtil.Arrived(m, hop))
                _pause[m] = _pause.GetValueOrDefault(m, 1f) - period;
            Vector3? blockedAt = null;
            if (has && !TacticUtil.Arrived(m, hop) && TacticUtil.Blocked(s, m))
            {
                blockedAt = hop; // место занято — выбрать другое
                has = false;
            }
            if (!has || _pause.GetValueOrDefault(m, 1f) <= 0f)
            {
                var next = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye,
                    new PositionWeights
                    {
                        Advance = 4f, Cover = 1.5f, Travel = 0.5f, Range = 0.5f, MinRange = 5f, MaxRange = 9f,
                        Spacing = 1f,
                    }, TacticUtil.ClaimedPlus(s, m, blockedAt), maxTravel: 9f);
                if (next is not { } n || Squad.FlatDistance(n, s.TargetPos) > dist - 2f)
                    _final.Add(m); // ближе укрытий нет — последний рывок в упор
                else
                {
                    _hop[m] = n;
                    _pause[m] = 1f;
                }
            }
        }
        if (_final.Contains(m) || dist < 9f || !_hop.ContainsKey(m))
        {
            _final.Add(m);
            var dir = new Vector3(m.GlobalPosition.X - s.TargetPos.X, 0f, m.GlobalPosition.Z - s.TargetPos.Z);
            dir = dir.LengthSquared() > 0.01f ? dir.Normalized() : Vector3.Back;
            var flat = new Vector3(s.TargetPos.X, 0f, s.TargetPos.Z);
            return new Order
            {
                Role = name + ": в упор", Destination = s.World.SnapToNav(flat + dir * 5f),
                Gait = Gait.Run, FireWhileMoving = true,
            };
        }
        var there = TacticUtil.Arrived(m, _hop[m]);
        return new Order
        {
            Role = there ? name + ": рывок, пауза" : name + ": рывок",
            Destination = _hop[m], Gait = Gait.Run, FireWhileMoving = true, Stance = Stance.Peek,
            PeekTime = 0.8f, PeekPause = 0.2f,
        };
    }
}

/// <summary>
/// 1. Штурм (контрольная группа): все рвутся к цели рывками от укрытия к укрытию, стреляя на ходу,
/// последние метры — в упор. Координации нет вовсе; нужен для сравнения в отчёте.
/// </summary>
public sealed class RushTactic : PeriodicTactic
{
    public override string Name => "Штурм (контроль)";
    private readonly AssaultRun _assault = new();

    public override void Begin(Squad s)
    {
        _assault.Clear();
        base.Begin(s);
    }

    protected override void Replan(Squad s)
    {
        foreach (var m in s.Fighters)
            s.Give(m, _assault.Next(s, m, Period, "Штурм"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 2. Прикрытие и перебежки (F.E.A.R. Advance-Cover / Get-to-Cover): бойцы разбиты на пары,
/// один из укрытия подавляет огнём (выглядывает часто), второй перебегает в укрытие ближе к цели;
/// дошёл — роли меняются.
///
/// После второго плейтеста (все сбивались в одно укрытие вплотную к игроку и там сидели):
/// — <b>линия остановки</b> <see cref="StopLine"/> м: ближе к цели укрытия не берутся вовсе;
/// — <b>своя полоса у каждой пары</b> (угол от цели), поэтому пары расходятся веером, а не идут гуськом;
/// — одно укрытие — один боец (бронь в <see cref="TacticalQuery"/>);
/// — <b>упёрлись в линию</b>: при перевесе пара идёт в финальный рывок, иначе по одному заходит сбоку,
///   растягивая цель под перекрёстный огонь, и оттуда держит её выглядывая.
/// </summary>
public sealed class BoundingOverwatchTactic : PeriodicTactic
{
    public override string Name => "Прикрытие и перебежки";
    public const float StopLine = 8f;

    private readonly Dictionary<NpcAgent, Vector3> _hold = new();
    private readonly Dictionary<NpcAgent, string> _why = new();
    private readonly HashSet<NpcAgent> _movers = new();
    private readonly Dictionary<NpcAgent, Vector3> _lane = new();
    private readonly Dictionary<NpcAgent, float> _outward = new();
    private readonly HashSet<NpcAgent> _widened = new();
    private readonly Dictionary<NpcAgent, float> _stall = new();
    private readonly HashSet<NpcAgent> _final = new();
    private readonly AssaultRun _assault = new(push: true); // штурм до конца: огневая позиция, обход укрытия
    private List<NpcAgent> _order = new();
    private int _laneKey;

    private static PositionWeights MoveWeights(Vector3 lane) => new()
    {
        Cover = 3f, Advance = 2f, Range = 1f, MinRange = StopLine + 1f, MaxRange = 13f, HardMinRange = StopLine,
        Spacing = 1.5f, Travel = 1f, LineOfFire = 1f, Angle = 2.5f, DesiredAngleDir = lane,
        RequireCover = true, RequireShot = true,
    };

    public override void Begin(Squad s)
    {
        _hold.Clear();
        _movers.Clear();
        _why.Clear();
        _lane.Clear();
        _outward.Clear();
        _widened.Clear();
        _stall.Clear();
        _final.Clear();
        _assault.Clear();
        _laneKey = 0;
        foreach (var m in s.Members)
            _hold[m] = m.GlobalPosition;
        base.Begin(s);
    }

    /// <summary>Пары и полосы: бойцы упорядочены по углу вокруг цели, соседи — пара, у пары своя полоса.</summary>
    private void AssignLanes(Squad s, List<NpcAgent> alive)
    {
        var target = Flat(s.TargetPos);
        var c = alive.Select(m => Flat(m.GlobalPosition) - target).Aggregate(Vector3.Zero, (a, b) => a + b);
        var squadDir = c.LengthSquared() > 0.01f ? c.Normalized() : Vector3.Forward;
        _order = alive.OrderBy(m => squadDir.SignedAngleTo(Flat(m.GlobalPosition) - target, Vector3.Up)).ToList();
        var pairs = (_order.Count + 1) / 2;
        for (var i = 0; i < _order.Count; i++)
        {
            var offset = (i / 2 - (pairs - 1) / 2f) * 50f;
            var m = _order[i];
            _lane[m] = TacticalQuery.Rotate(squadDir, offset);
            // куда «наружу» заходить сбоку: крайние пары — от центра, единственная пара — в разные стороны
            _outward[m] = offset != 0f ? Mathf.Sign(offset) : i % 2 == 0 ? -1f : 1f;
            if (offset != 0f && i % 2 == 1)
                _widened.Add(m); // у крайней пары сбоку заходит один, второй держит полосу
            else
                _widened.Remove(m);
        }
    }

    protected override void Replan(Squad s)
    {
        var alive = s.Fighters.ToList();
        if (alive.Count != _laneKey)
        {
            _laneKey = alive.Count;
            AssignLanes(s, alive);
        }
        var ordered = _order.Where(m => !m.IsDead).ToList();
        for (var i = 0; i < ordered.Count; i += 2)
        {
            var a = ordered[i];
            var b = i + 1 < ordered.Count ? ordered[i + 1] : null;
            // смельчак ушёл на штурм — напарник пока действует как одиночка
            if (b is not null && s.Brave.IsOut(b))
            {
                _movers.Remove(b);
                b = null;
            }
            if (s.Brave.IsOut(a))
            {
                _movers.Remove(a);
                if (b is null)
                    continue;
                a = b;
                b = null;
            }
            var pair = b is null ? new[] { a } : new[] { a, b };

            if (pair.Any(_final.Contains))
            {
                FinalRush(s, pair);
                continue;
            }

            foreach (var m in pair)
            {
                if (!_movers.Contains(m))
                    continue;
                if (TacticUtil.Arrived(m, _hold[m], 0.8f))
                {
                    _movers.Remove(m); // добежал — теперь он прикрывает, партнёр бежит
                    var partner = m == a ? b : a;
                    if (partner is not null)
                        TryAdvance(s, partner);
                }
                else if (TacticUtil.Blocked(s, m))
                    Relocate(s, m, "укрытие занято", _hold[m]); // упёрся — другое укрытие, а не толкаться
            }
            // позицию обошли или с неё нет выстрела — сменить, не дожидаясь очереди
            foreach (var m in pair)
                if (!_movers.Contains(m) && TacticUtil.PositionLost(s, m, out var why))
                    Relocate(s, m, why, null);

            // в паре ровно один перебегающий; одиночка перебегает и прикрывает сам себя
            if (!pair.Any(_movers.Contains))
            {
                if (TryAdvance(s, a) || (b is not null && TryAdvance(s, b)))
                    _stall[a] = 0f;
                else
                    _stall[a] = _stall.GetValueOrDefault(a) + Period;
                // упёрлись в линию остановки: при перевесе — финальный рывок, иначе — заход сбоку
                if (_stall.GetValueOrDefault(a) > 3f)
                {
                    if (TacticUtil.Advantage(s))
                    {
                        FinalRush(s, pair);
                        continue;
                    }
                    var side = pair.FirstOrDefault(m => !_widened.Contains(m));
                    if (side is not null)
                    {
                        _widened.Add(side);
                        _lane[side] = TacticalQuery.Rotate(_lane[side], 55f * _outward.GetValueOrDefault(side, 1f));
                        Relocate(s, side, "заходит сбоку", null, sideways: true);
                        _stall[a] = 0f;
                    }
                }
            }

            foreach (var m in pair)
            {
                if (_movers.Contains(m))
                    s.Give(m, new Order
                    {
                        Role = "Перебежка: " + _why.GetValueOrDefault(m, "вперёд"), Destination = _hold[m],
                        // на бегу стреляет навскидку (точность на ходу ниже): раньше перебегающий молчал,
                        // и половина отряда не вела огня
                        FireWhileMoving = true, Stance = Stance.Peek, KeepAway = StopLine,
                    });
                else
                {
                    var partnerMoving = (m == a ? b : a) is { } p && _movers.Contains(p);
                    s.Give(m, new Order
                    {
                        Role = partnerMoving ? "Прикрывает огнём перебежку" : "Держит укрытие: " + _why.GetValueOrDefault(m, "ждёт очереди"),
                        Destination = _hold[m],
                        Stance = Stance.Peek,
                        // прикрывающий выглядывает часто и надолго — подавляет цель
                        PeekTime = partnerMoving ? 1.5f : 1.1f,
                        PeekPause = partnerMoving ? 0.2f : 0.6f,
                    });
                }
            }
        }
    }

    private void FinalRush(Squad s, NpcAgent[] pair)
    {
        foreach (var m in pair)
        {
            _final.Add(m);
            _movers.Remove(m);
            s.Give(m, _assault.Next(s, m, Period, "Финальный рывок"));
        }
    }

    /// <summary>Перебежка вперёд по своей полосе, если есть укрытие заметно ближе (и не за линией остановки).</summary>
    private bool TryAdvance(Squad s, NpcAgent m)
    {
        var best = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye, MoveWeights(_lane[m]),
            s.ClaimedExcept(m), maxTravel: 10f);
        var gain = best is { } p
            ? Squad.FlatDistance(m.GlobalPosition, s.TargetPos) - Squad.FlatDistance(p, s.TargetPos) : 0f;
        if (best is null || gain < 1.5f)
        {
            _why[m] = TacticUtil.DistToTarget(s, m) < StopLine + 5f ? "дальше линия остановки" : "ближе укрытий нет";
            return false;
        }
        _hold[m] = best.Value;
        _movers.Add(m);
        _why[m] = $"укрытие ближе на {gain:0} м";
        return true;
    }

    /// <summary>Сменить укрытие без требования «ближе»: обошли, нет выстрела, занято или заход сбоку.</summary>
    private void Relocate(Squad s, NpcAgent m, string reason, Vector3? avoid, bool sideways = false)
    {
        var w = MoveWeights(_lane[m]);
        if (sideways)
            w = w with { Advance = 0f, Angle = 4f, Travel = 0.3f };
        var best = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye, w,
            TacticUtil.ClaimedPlus(s, m, avoid), maxTravel: sideways ? 22f : 10f);
        if (best is not { } p || Squad.FlatDistance(p, _hold[m]) <= 1f)
            return;
        _hold[m] = p;
        _movers.Add(m);
        _why[m] = reason;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 3. Обход с фланга (Killzone: оценка позиций + обход вне линии огня): ближайший к цели
/// «сковывает» её огнём с фронта — из укрытия, выглядывая часто; остальные сначала широко выходят
/// на фланги (на 13–18 м, от укрытия к укрытию), затем одновременно сходятся под углом ±90°
/// к фронту и атакуют из укрытий.
///
/// После второго плейтеста: обходят <b>по дуге</b>, не приближаясь к цели ближе <see cref="KeepAway"/> м
/// (раньше бежали к «своему флангу» кратчайшим путём — сквозь игрока); каждый идёт на свою сторону
/// (левый — налево), пути не пересекаются; фланговая атака ведётся с 8–12 м из укрытия, а не в упор;
/// упёрся в занятое место — берёт другое укрытие.
/// </summary>
public sealed class FlankTactic : PeriodicTactic
{
    public override string Name => "Обход с фланга";
    public const float KeepAway = 10f;
    private static readonly float[] Angles = { 90f, -90f, 140f, -140f };

    private NpcAgent? _pinner;
    private Vector3 _front;
    private bool _attack;
    private float _phaseTime;
    private float _pinAge;
    private readonly Dictionary<NpcAgent, float> _angle = new();
    private readonly Dictionary<NpcAgent, Vector3> _dest = new();
    private Vector3? _pinPoint;

    public override void Begin(Squad s)
    {
        StartCycle(s);
        base.Begin(s);
    }

    private void StartCycle(Squad s)
    {
        var alive = s.Fighters.Where(m => !s.Brave.IsOut(m)).ToList();
        var target = Flat(s.TargetPos);
        _pinner = alive.OrderBy(m => Squad.FlatDistance(m.GlobalPosition, s.TargetPos)).FirstOrDefault();
        _front = _pinner is null ? Vector3.Back : Flat(_pinner.GlobalPosition) - target;
        _front = _front.LengthSquared() > 0.01f ? _front.Normalized() : Vector3.Back;
        _attack = false;
        _phaseTime = 0f;
        _pinPoint = null;
        _angle.Clear();
        _dest.Clear();
        // каждый — на свою сторону: кто левее фронта, тому левый фланг, чтобы пути не пересекались
        var flankers = alive.Where(m => m != _pinner)
            .OrderByDescending(m => _front.SignedAngleTo(Flat(m.GlobalPosition) - target, Vector3.Up)).ToList();
        var angles = Angles.Take(flankers.Count).OrderByDescending(a => a).ToList();
        for (var i = 0; i < flankers.Count; i++)
            _angle[flankers[i]] = angles[i % angles.Count];
    }

    protected override void Replan(Squad s)
    {
        _phaseTime += Period;
        _pinAge += Period;
        // сковывающий ушёл смельчаком на штурм — фронт держит другой
        if (_pinner is null || _pinner.IsDead || s.Brave.IsOut(_pinner) || _angle.Keys.Any(m => m.IsDead) && !_attack)
            StartCycle(s);
        if (_pinner is null)
            return;

        // сковывающий: укрытие с фронта, из которого есть выстрел
        if (_pinPoint is null || _pinAge > 6f || (_pinAge > 2f && TacticUtil.PositionLost(s, _pinner, out _)))
        {
            _pinAge = 0f;
            _pinPoint = TacticalQuery.FindBest(s.World, _pinner.GlobalPosition, s.TargetPos, s.TargetEye,
                new PositionWeights
                {
                    LineOfFire = 2f, Cover = 2f, Range = 1.5f, MinRange = 9f, MaxRange = 14f, HardMinRange = 8f,
                    Angle = 1.5f, DesiredAngleDir = _front, Travel = 1.5f, Spacing = 1f,
                    RequireCover = true, RequireShot = true,
                }, s.ClaimedExcept(_pinner), maxTravel: 12f) ?? _pinner.GlobalPosition;
        }
        s.Give(_pinner, new Order
        {
            Role = "Сковывает огнём из укрытия", Destination = _pinPoint, FireWhileMoving = true,
            Stance = Stance.Peek, PeekTime = 1.4f, PeekPause = 0.3f,
        });

        var flankers = _angle.Keys.Where(m => !m.IsDead && !s.Brave.IsOut(m)).ToList();
        if (!_attack)
        {
            var allThere = true;
            foreach (var m in flankers)
            {
                var dir = TacticalQuery.Rotate(_front, _angle[m]);
                var has = _dest.TryGetValue(m, out var d);
                var blocked = has && TacticUtil.Blocked(s, m);
                // цель подошла к намеченной точке или место занято — выбрать другую
                if (!has || blocked || Squad.FlatDistance(d, s.TargetPos) < KeepAway)
                    _dest[m] = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye,
                        new PositionWeights
                        {
                            Angle = 4f, DesiredAngleDir = dir, Cover = 1.5f, Range = 1f, MinRange = 13f,
                            MaxRange = 18f, HardMinRange = KeepAway + 1f, Spacing = 1f, Travel = 0.3f,
                            RequireCover = true,
                        }, TacticUtil.ClaimedPlus(s, m, blocked ? d : null), maxTravel: 40f) ?? m.GlobalPosition;
                if (Squad.FlatDistance(m.GlobalPosition, _dest[m]) > 1.5f)
                    allThere = false;
                s.Give(m, new Order
                {
                    Role = "Выходит на фланг по дуге", Destination = _dest[m], FireAllowed = false,
                    Stance = Stance.Crouch, KeepAway = KeepAway,
                });
            }
            if (allThere || _phaseTime > 16f)
            {
                _attack = true;
                _phaseTime = 0f;
                _dest.Clear();
            }
            return;
        }

        foreach (var m in flankers)
        {
            var dir = TacticalQuery.Rotate(_front, _angle[m]);
            var has = _dest.TryGetValue(m, out var d);
            var arrived = has && TacticUtil.Arrived(m, d);
            var blocked = has && !arrived && TacticUtil.Blocked(s, m);
            var range = has ? Squad.FlatDistance(d, s.TargetPos) : 0f;
            // позицию не дёргаем каждые полторы секунды: меняем, только если она перестала годиться
            // цель подошла вплотную — не убегать на новую точку, а драться (range < 6.5 больше не повод)
            var closeFight = TacticUtil.InCloseFight(s, m);
            if (!has || blocked || (range < 6.5f && !closeFight) || range > 16f
                || (arrived && TacticUtil.PositionLost(s, m, out _)))
                _dest[m] = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye,
                    new PositionWeights
                    {
                        Angle = 4f, DesiredAngleDir = dir, LineOfFire = 2f, Cover = 2f, Range = 1f, MinRange = 8f,
                        MaxRange = 12f, HardMinRange = 7f, Spacing = 1f, Travel = 0.5f,
                        RequireCover = true, RequireShot = true,
                    }, TacticUtil.ClaimedPlus(s, m, blocked ? d : null), maxTravel: 25f) ?? m.GlobalPosition;
            s.Give(m, new Order
            {
                Role = "Атакует с фланга", Destination = _dest[m], FireWhileMoving = true, Stance = Stance.Peek,
                PeekTime = 1.2f, PeekPause = 0.3f, KeepAway = 7f,
            });
        }
        if (_phaseTime > 18f)
            StartCycle(s); // цель ушла — новый цикл с новым фронтом
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Общая оборона для тактик 4 и 5 (F.E.A.R.: «выходить из укрытия только под угрозой»): боец занимает
/// проверенное укрытие, из которого есть выстрел (встав или шагнув вбок), и выглядывает на выстрел.
/// По пути в укрытие отстреливается. Укрытие обошли — меняет его не сразу, а через секунду
/// (вплотную не бежит, а отбивается); мало здоровья — отходит в дальнее укрытие;
/// в укрытие не пройти (занято) — берёт другое.
/// </summary>
public abstract class DefensiveBase : PeriodicTactic
{
    protected override float Period => 0.5f;

    private readonly Dictionary<NpcAgent, Vector3> _spot = new();
    private readonly Dictionary<NpcAgent, string> _why = new();
    private readonly HashSet<NpcAgent> _retreated = new();
    private readonly Dictionary<NpcAgent, float> _changedAt = new();

    public override void Begin(Squad s)
    {
        _spot.Clear();
        _why.Clear();
        _retreated.Clear();
        _changedAt.Clear();
        base.Begin(s);
    }

    protected void Defend(Squad s, NpcAgent m, float retreatBelow = 0.4f, bool covering = false)
    {
        // ранен, но цель вплотную — отход бесполезен, дерётся до конца
        var low = m.Health < m.MaxHealth * retreatBelow && !TacticUtil.InCloseFight(s, m);
        var has = _spot.TryGetValue(m, out var spot);
        var settled = s.Time - _changedAt.GetValueOrDefault(m, -99f) > 2f;
        var arrivedNow = has && TacticUtil.Arrived(m, spot);
        var lost = has && settled && arrivedNow && TacticUtil.PositionLost(s, m, out var lostWhy) ? lostWhy : null;
        Vector3? blockedAt = null;
        if (has && !arrivedNow && TacticUtil.Blocked(s, m))
        {
            lost = "занято";
            blockedAt = spot;
        }
        var needNew = !has || lost is not null || (low && !_retreated.Contains(m));
        if (needNew)
        {
            var w = low
                ? new PositionWeights
                {
                    Cover = 3f, LineOfFire = 1f, Range = 2f, MinRange = 14f, MaxRange = 20f, Travel = 1f,
                    Spacing = 1.5f, Advance = -1f, RequireCover = true,
                }
                : new PositionWeights
                {
                    Cover = 3f, LineOfFire = 2f, Range = 1.5f, MinRange = 8f, MaxRange = 15f, Travel = 1.5f,
                    Spacing = 2f, RequireCover = true, RequireShot = true,
                };
            spot = TacticalQuery.FindBest(s.World, m.GlobalPosition, s.TargetPos, s.TargetEye, w,
                TacticUtil.ClaimedPlus(s, m, blockedAt), maxTravel: 16f) ?? m.GlobalPosition;
            _spot[m] = spot;
            _changedAt[m] = s.Time;
            _why[m] = low ? "Отходит, отстреливается" : lost is not null ? $"Меняет укрытие: {lost}" : "Занимает укрытие";
            if (low)
                _retreated.Add(m);
        }
        var arrived = TacticUtil.Arrived(m, spot);
        var role = !arrived ? _why[m]
            : _retreated.Contains(m) ? "Обороняется (отошёл)"
            : covering ? "Прикрывает смельчака огнём"
            : "Обороняется в укрытии";
        s.Give(m, new Order
        {
            Role = role,
            Destination = spot,
            Stance = Stance.Peek,
            // по пути в укрытие — отстреливаться, а не молча бежать
            FireWhileMoving = true,
            // прикрывая вылазку, выглядывает чаще и дольше — давит цель огнём
            PeekTime = covering ? 1.4f : 0.9f,
            PeekPause = covering ? 0.3f : 0.7f,
        });
    }
}

/// <summary>
/// 4. Оборона из укрытий: все держат укрытия и ждут цель, никто не идёт вперёд. Со смельчаками
/// (опция B) — бывшая «Оборона с вылазками»: пока смельчак на штурме, остальные прикрывают его огнём.
/// </summary>
public sealed class DefensiveCoverTactic : DefensiveBase
{
    public override string Name => "Оборона из укрытий";

    protected override void Replan(Squad s)
    {
        foreach (var m in s.Fighters)
            if (!s.Brave.IsOut(m))
                Defend(s, m, covering: s.Brave.Active);
    }
}

// ─────────────────────────────────────────────────────────────────────────────


/// <summary>
/// Смельчаки (идея Дмитрия с плейтеста): штурм всем отрядом не работает, но «особо храбрый» боец,
/// который в нужный момент один идёт на штурм, ломает позиционную перестрелку.
///
/// У каждого бойца на раунд есть смелость 0…100% (уровни равномерны и перемешаны). Смельчаки —
/// бойцы со смелостью от <see cref="BraveFrom"/> (при отряде из 4 — двое: 100% и 67%).
/// Если отряд давно не попадал по цели, у смельчака кончается терпение (<see cref="Patience"/>:
/// 10 с у самого смелого), и он <b>один</b> идёт на штурм, остальные продолжают свою тактику.
/// На штурме всегда не больше одного. Штурм до конца (плейтест): смельчак не прячется и не отходит
/// по ранению, а выходит на огневую позицию — откуда есть выстрел, при необходимости обходя укрытие
/// цели; отбой — только смерть смельчака или цели либо цель ушла дальше <see cref="AbortDistance"/> м.
/// Погиб или отбой — следующая вылазка не раньше чем через 6–8 с. Клавиша P на арене (<see cref="Squad.ProvokeSortie"/>) отправляет
/// смельчака сразу, не дожидаясь конца терпения.
/// </summary>
public sealed class BraveLayer
{
    public const float BraveFrom = 0.6f;
    public static float Patience(float brave) => 10f + (1f - brave) * 10f;
    public static float RetreatBelow(float brave) => 0.5f - 0.3f * brave;
    /// <summary>Штурм отменяется, только если цель ушла дальше этого (ранение смельчака штурм не прерывает).</summary>
    public const float AbortDistance = 20f;
    /// <summary>Порог отбоя для штурма, начатого с дистанции <paramref name="startDist"/>: цель «убежала» — не
    /// ближе 20 м и на 4 м дальше, чем была на старте (смельчак с дальнего края арены сразу не отменяется).</summary>
    public static float AbortFrom(float startDist) => Mathf.Max(AbortDistance, startDist + 4f);
    private float _abortFar = AbortDistance;

    /// <summary>Сколько вылазок смельчаков начато за раунд (для отчёта).</summary>
    public static int SortiesStarted;

    private readonly Dictionary<NpcAgent, float> _brave = new();
    private readonly AssaultRun _assault = new(push: true); // штурм до конца: огневая позиция, обход укрытия
    private NpcAgent? _out;
    private float _restUntil = -1f;
    private float _lastHp;
    private float _sinceResult;

    public bool Active => _out is not null;
    public bool IsOut(NpcAgent m) => _out == m;
    public float Bravery(NpcAgent m) => _brave.GetValueOrDefault(m);

    /// <summary>Отозвать смельчака (опцию выключили).</summary>
    public void Recall() => _out = null;

    /// <summary>Новый раунд: смелость раздаётся заново.</summary>
    public void Begin(Squad s, int seed)
    {
        _brave.Clear();
        var rng = new System.Random(seed); // свой генератор, чтобы опция не сдвигала случайность боя
        {
            var n = s.Members.Count;
            var levels = Enumerable.Range(0, n).Select(i => n > 1 ? i / (float)(n - 1) : 1f)
                .OrderBy(_ => rng.Next()).ToList();
            for (var i = 0; i < n; i++)
                _brave[s.Members[i]] = levels[i];
        }
        _out = null;
        _assault.Clear();
        _restUntil = -1f;
        _lastHp = s.Target.Health;
        _sinceResult = 0f;
    }

    private NpcAgent? Candidate(Squad s, bool provoked) => s.Fighters
        .Where(m => (provoked || Bravery(m) >= BraveFrom) && m.Health > m.MaxHealth * (provoked ? 0.3f : 0.5f))
        .OrderByDescending(Bravery).FirstOrDefault();

    /// <summary>Вызывается после плана тактики: смельчак на вылазке получает приказ штурма поверх него.</summary>
    public void Apply(Squad s, float period)
    {
        // «результат» — попадание по цели; пока его нет, терпение смельчаков тает
        var hp = s.Target.Health;
        _sinceResult = hp < _lastHp - 0.5f ? 0f : _sinceResult + period;
        _lastHp = hp;

        if (_out is { } o)
        {
            if (o.IsDead || !s.Fighters.Contains(o))
            {
                _out = null;
                _restUntil = s.Time + 6f;
            }
            else if (s.Target.IsDead || TacticUtil.DistToTarget(s, o) > _abortFar)
            {
                // штурм до конца: ранение не повод вернуться; отбой — только если цель мертва или убежала далеко
                _out = null;
                _restUntil = s.Time + 8f;
            }
        }

        var provoked = s.ProvokeSortie;
        s.ProvokeSortie = false;
        if (_out is null && (provoked || s.Time >= _restUntil) && Candidate(s, provoked) is { } c
            && (provoked || _sinceResult > Patience(Bravery(c))))
        {
            _out = c;
            _abortFar = AbortFrom(TacticUtil.DistToTarget(s, c));
            _assault.Forget(c);
            _sinceResult = 0f;
            SortiesStarted++;
        }

        if (_out is { } m)
        {
            var order = _assault.Next(s, m, period, "Смельчак");
            s.Give(m, order with { Role = order.Role + $" (смелость {Bravery(m) * 100f:0}%)" });
        }
    }

    /// <summary>Строка для экрана арены.</summary>
    public string Status(Squad s)
    {
        if (_out is { } o)
            return $"боец {s.Members.ToList().IndexOf(o) + 1} на штурме";
        if (Candidate(s, false) is not { } c)
            return "годных смельчаков нет (P — отправит самого смелого)";
        var wait = Mathf.Max(Patience(Bravery(c)) - _sinceResult, _restUntil - s.Time);
        return $"боец {s.Members.ToList().IndexOf(c) + 1} (смелость {Bravery(c) * 100f:0}%) пойдёт через {Mathf.Max(0f, wait):0} с без попаданий";
    }
}
