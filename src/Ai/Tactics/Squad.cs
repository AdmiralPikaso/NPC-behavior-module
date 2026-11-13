using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Actors;
using Zero.Ai.Actions;
using Zero.Ai.Nodes;
using Zero.Ai.Stealth;

namespace Zero.Ai.Tactics;

public enum Stance
{
    /// <summary>Стоит (стреляет при возможности).</summary>
    Stand,
    /// <summary>Сидит в укрытии и не высовывается.</summary>
    Crouch,
    /// <summary>Сидит в укрытии, встаёт на выстрел и снова приседает.</summary>
    Peek,
}

/// <summary>Приказ отряда бойцу: роль, куда идти, как стоять, можно ли стрелять.</summary>
public sealed record Order
{
    public string Role { get; init; } = "—";
    public Vector3? Destination { get; init; }
    public Gait Gait { get; init; } = Gait.Run;
    public bool FireAllowed { get; init; } = true;
    public bool FireWhileMoving { get; init; }
    public Stance Stance { get; init; } = Stance.Stand;
    /// <summary>Сколько секунд держаться высунувшись (стойка «выглядывать»).</summary>
    public float PeekTime { get; init; } = 0.9f;
    /// <summary>Пауза в укрытии между выглядываниями (у прикрывающего огнём — короче).</summary>
    public float PeekPause { get; init; } = 0.7f;
    /// <summary>По пути не приближаться к цели ближе этого (обход по дуге); 0 — идти кратчайшим путём.</summary>
    public float KeepAway { get; init; }
    /// <summary>
    /// Под огнём прятаться, а не отбиваться (тактика «Роли»): попадание прерывает выглядывание и сажает
    /// в укрытие на 1,5–2 с, попадание по сидящему не поднимает его отбиваться, обход считается по
    /// собственному препятствию точки (<see cref="TacticalWorld.IsOwnSideCover"/>). По умолчанию выключено.
    /// </summary>
    public bool HideWhenHit { get; init; }
    /// <summary>
    /// Штурм до конца (смельчак, финальный рывок): под огнём не прячется, не приседает, не уходит в укрытие,
    /// стреляет на ходу. Перекрывает <see cref="HideWhenHit"/>.
    /// </summary>
    public bool Push { get; init; }
}

/// <summary>Отрядная тактика: по состоянию боя раздаёт приказы бойцам.</summary>
public interface ITactic
{
    string Name { get; }
    void Begin(Squad squad);
    void Update(Squad squad, float dt);
}

/// <summary>
/// Отрядный координатор (уровень «squad» из F.E.A.R. / Killzone 2 / The Last of Us):
/// держит общее знание о цели (последняя известная позиция и когда её видели),
/// текущую тактику и приказы бойцам, а после потери контакта — общий поиск.
///
/// В режиме <see cref="Omniscient"/> (сравнение тактик в чистом бою) отряд всегда знает,
/// где цель, и все живые — бойцы. В стелс-режиме бойцы — только те, кто в состоянии «Тревога»,
/// а цель для тактик — последняя известная позиция, которую сообщают видящие её бойцы.
/// </summary>
public sealed class Squad
{
    public TacticalWorld World { get; }
    public ActorBody Target { get; set; }
    public IReadOnlyList<NpcAgent> Members => _members;
    /// <summary>Отряд всегда знает настоящую позицию цели (без стелса).</summary>
    public bool Omniscient { get; set; } = true;

    /// <summary>Живые бойцы, участвующие в бою (в стелс-режиме — только поднявшие тревогу).</summary>
    public IEnumerable<NpcAgent> Fighters =>
        _members.Where(m => !m.IsDead && (Omniscient || m.Alert == AlertState.Alarm));

    public ITactic? Tactic { get; private set; }
    public Random Rng { get; private set; } = new(1);
    public float Time { get; private set; }

    // ───── общее знание о цели ─────
    public Vector3 LastKnownPosition { get; private set; }
    public float LastSeenTime { get; private set; } = -999f;
    public float TimeSinceSeen => Time - LastSeenTime;
    /// <summary>Цель видел хоть кто-то в этом или прошлом кадре.</summary>
    public bool TargetVisible => TimeSinceSeen < 0.25f;

    // ───── метрики ─────
    /// <summary>Сколько секунд цель была под перекрёстным огнём (два видящих её бойца под углом ≥ 60°).</summary>
    public float CrossfireTime { get; private set; }
    /// <summary>Сколько секунд цель видел хотя бы один боец.</summary>
    public float EngagedTime { get; private set; }
    public int SearchesStarted { get; private set; }
    public int SearchesSucceeded { get; private set; }

    private readonly List<NpcAgent> _members = new();
    private readonly Dictionary<NpcAgent, Order> _orders = new();
    private int _fightersKey;

    // поиск
    private bool _searching;
    private readonly List<Vector3> _searchPoints = new();
    private readonly HashSet<int> _visited = new();
    private readonly Dictionary<NpcAgent, int> _searchAssigned = new();

    public Squad(TacticalWorld world, ActorBody target)
    {
        World = world;
        Target = target;
    }

    public void Add(NpcAgent npc)
    {
        _members.Add(npc);
        if (Omniscient)
            npc.Brain = new CombatOrderAction(this);
    }

    public void SetTactic(ITactic tactic, int seed)
    {
        Tactic = tactic;
        Rng = new Random(seed);
        Time = 0f;
        LastSeenTime = -999f;
        LastKnownPosition = Target.GlobalPosition;
        CrossfireTime = 0f;
        EngagedTime = 0f;
        SearchesStarted = 0;
        SearchesSucceeded = 0;
        _searching = false;
        _tokenUntil.Clear();
        _tokenRest.Clear();
        ProvokeSortie = false;
        ShooterGate = null;
        Brave.Begin(this, seed);
        _orders.Clear();
        _fightersKey = 0;
        if (Omniscient)
            foreach (var m in _members)
                m.Brain = new CombatOrderAction(this);
        tactic.Begin(this);
    }

    public Order OrderFor(NpcAgent npc) => _orders.TryGetValue(npc, out var o) ? o : new Order { Role = "Ждёт" };
    public void Give(NpcAgent npc, Order order) => _orders[npc] = order;

    /// <summary>Точки, занятые (назначенные) другими бойцами, — чтобы двое не шли в одно место.</summary>
    public List<Vector3> ClaimedExcept(NpcAgent npc)
    {
        var list = new List<Vector3>();
        foreach (var m in Fighters)
        {
            if (m == npc)
                continue;
            if (_orders.TryGetValue(m, out var o) && o.Destination is { } d)
            {
                list.Add(d);
                // выглянувший вбок стоит в шаге от своей точки — это место тоже его
                var off = FlatDistance(m.GlobalPosition, d);
                if (off is > 0.5f and < 2.5f)
                    list.Add(m.GlobalPosition);
            }
            else
                list.Add(m.GlobalPosition);
        }
        return list;
    }

    /// <summary>Где цель по мнению отряда (для тактик) — последняя известная позиция.</summary>
    public Vector3 TargetPos => LastKnownPosition;
    public Vector3 TargetEye => LastKnownPosition + Vector3.Up * 1.6f;

    /// <summary>Боец видит цель и сообщает отряду её позицию (аналог сервиса Hitman / пакета TLOU).</summary>
    public void Report(Vector3 position)
    {
        if (_searching)
        {
            _searching = false;
            SearchesSucceeded++;
        }
        LastKnownPosition = position;
        LastSeenTime = Time;
    }

    /// <summary>Отряд узнал о позиции из крика союзника (без пометки «видели сейчас»).</summary>
    public void Hint(Vector3 position)
    {
        if (TimeSinceSeen <= 1f)
            return;
        LastKnownPosition = position;
        LastSeenTime = Time; // свежая наводка: сначала бой в эту сторону, потом поиск вокруг
    }

    /// <summary>
    /// Может ли боец сейчас попасть в цель хоть в какую-то видимую часть тела
    /// (с учётом союзников на линии огня).
    /// </summary>
    public bool CanHit(NpcAgent npc) => AimPartFor(npc) is not null;

    /// <summary>Видимая часть тела цели для выстрела бойца (грудь, голова, ноги, плечо) или null.</summary>
    public Vector3? AimPartFor(NpcAgent npc) =>
        Target.IsDead ? null : World.VisiblePart(npc.MuzzlePosition, Target, npc.GetRid());

    private readonly Dictionary<NpcAgent, CombatOrderAction> _combat = new();

    // ───── токены огня (DOOM 2016): настройка сложности поверх любой тактики ─────
    /// <summary>Сколько бойцов одновременно стреляют по приказу (0 — без ограничения). Самооборона токен не тратит.</summary>
    public int MaxShooters { get; set; }

    // ───── смельчаки: общий слой бойца поверх любой тактики ─────
    public BraveLayer Brave { get; } = new();
    /// <summary>Опция «смельчаки» (клавиша B на арене).</summary>
    public bool BraveEnabled { get; set; }
    public bool BraveOn => BraveEnabled;
    /// <summary>Клавиша P на арене: отправить смельчака на штурм сейчас, не дожидаясь конца терпения.</summary>
    public bool ProvokeSortie { get; set; }
    private readonly Dictionary<NpcAgent, float> _tokenUntil = new();
    private readonly Dictionary<NpcAgent, float> _tokenRest = new();

    /// <summary>
    /// Своё правило права огня от тактики (тактика «Роли» раздаёт его по приоритету). Пока задано,
    /// заменяет токены <see cref="MaxShooters"/>; сбрасывается при смене тактики.
    /// </summary>
    public Func<NpcAgent, bool>? ShooterGate { get; set; }

    public bool MayShoot(NpcAgent npc)
    {
        if (ShooterGate is { } gate)
            return gate(npc);
        if (MaxShooters <= 0)
            return true;
        if (_tokenUntil.TryGetValue(npc, out var until))
        {
            if (Time < until)
                return true;
            _tokenUntil.Remove(npc);
            _tokenRest[npc] = Time + 1.5f; // отдал токен — перерыв
        }
        foreach (var k in _tokenUntil.Where(kv => Time >= kv.Value || kv.Key.IsDead).Select(kv => kv.Key).ToList())
        {
            _tokenUntil.Remove(k);
            _tokenRest[k] = Time + 1.5f;
        }
        if (_tokenUntil.Count >= MaxShooters || Time < _tokenRest.GetValueOrDefault(npc, -1f))
            return false;
        _tokenUntil[npc] = Time + 3f;
        return true;
    }
    internal void Register(NpcAgent npc, CombatOrderAction action) => _combat[npc] = action;

    /// <summary>Состояние бойца на уровне «боец в бою» (сколько без выстрела, в самообороне ли и т.п.).</summary>
    public CombatOrderAction? CombatOf(NpcAgent npc) => _combat.TryGetValue(npc, out var a) ? a : null;

    public static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    public void Update(float dt)
    {
        if (Tactic is null || Target.IsDead)
            return;
        Time += dt;
        if (Omniscient)
            Report(Target.GlobalPosition);

        // состав бойцов изменился (кто-то поднял тревогу или погиб) — тактика перераспределяет роли
        var key = 17;
        foreach (var f in Fighters)
            key = key * 31 + f.GetInstanceId().GetHashCode();
        if (!Omniscient && key != _fightersKey)
        {
            _fightersKey = key;
            if (Fighters.Any())
                Tactic.Begin(this);
        }
        if (Fighters.Any())
        {
            Tactic.Update(this, dt);
            // смельчак получает приказ штурма поверх приказа тактики
            if (BraveOn)
                Brave.Apply(this, dt);
            else
            {
                Brave.Recall();
                ProvokeSortie = false;
            }
        }
        MeasureCrossfire(dt);
    }

    // ───────────── поиск ─────────────

    /// <summary>
    /// Следующая точка поиска для бойца: места рядом с последней известной позицией,
    /// которые ещё никто не проверял (по духу «карты поиска» The Last of Us).
    /// </summary>
    public Vector3? SearchPointFor(NpcAgent npc)
    {
        if (!_searching)
            StartSearch();
        if (_searchAssigned.TryGetValue(npc, out var cur) && !_visited.Contains(cur))
            return _searchPoints[cur];
        var best = -1;
        var bestScore = float.MaxValue;
        for (var i = 0; i < _searchPoints.Count; i++)
        {
            if (_visited.Contains(i) || _searchAssigned.ContainsValue(i))
                continue;
            var p = _searchPoints[i];
            var score = FlatDistance(p, LastKnownPosition) * 1.5f + FlatDistance(p, npc.GlobalPosition);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        if (best < 0)
            return null;
        _searchAssigned[npc] = best;
        return _searchPoints[best];
    }

    /// <summary>Боец проверил точку поиска; заодно отмечаются точки, видимые от него.</summary>
    public void MarkSearched(NpcAgent npc)
    {
        if (_searchAssigned.Remove(npc, out var idx))
            _visited.Add(idx);
        for (var i = 0; i < _searchPoints.Count; i++)
            if (!_visited.Contains(i) && FlatDistance(_searchPoints[i], npc.GlobalPosition) < 7f
                && World.ClearLine(npc.EyePosition, _searchPoints[i] + Vector3.Up * 0.7f))
                _visited.Add(i);
    }

    public bool IsSearching => _searching;

    private void StartSearch()
    {
        _searching = true;
        SearchesStarted++;
        _searchPoints.Clear();
        _visited.Clear();
        _searchAssigned.Clear();
        foreach (var p in TacticalQuery.Candidates(World, LastKnownPosition))
            if (FlatDistance(p, LastKnownPosition) <= 14f)
                _searchPoints.Add(p);
    }

    private void MeasureCrossfire(float dt)
    {
        var dirs = new List<Vector3>();
        foreach (var m in Fighters)
        {
            if (!World.ClearLine(m.EyePosition, Target.CenterMass))
                continue;
            var d = m.GlobalPosition - Target.GlobalPosition;
            d.Y = 0f;
            if (d.LengthSquared() > 0.01f)
                dirs.Add(d.Normalized());
        }
        if (dirs.Count > 0)
            EngagedTime += dt;
        for (var i = 0; i < dirs.Count; i++)
            for (var j = i + 1; j < dirs.Count; j++)
                if (Mathf.RadToDeg(dirs[i].AngleTo(dirs[j])) >= 60f)
                {
                    CrossfireTime += dt;
                    return;
                }
    }
}
