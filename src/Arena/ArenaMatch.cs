using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Zero.Actors;
using Zero.Ai.Core;
using Zero.Ai.Navigation;
using Zero.Ai.Nodes;
using Zero.Ai.Stealth;
using Zero.Ai.Tactics;
using Zero.Player;

namespace Zero.Arena;

public enum ArenaMode
{
    /// <summary>Играет человек; клавиши 1–5 — тактика, R — новый раунд.</summary>
    Play,
    /// <summary>Автоматический прогон: бот-игрок против каждой тактики, итог в docs/results.</summary>
    Experiment,
}

/// <summary>
/// Арена для сравнения тактик: отряд из N NPC против одного игрока.
/// Раунд заканчивается смертью игрока (победа отряда), гибелью отряда (победа игрока)
/// или по таймауту (ничья). Стартовые позиции зависят только от номера раунда,
/// поэтому все тактики проходят одинаковые наборы стартов.
/// </summary>
[GlobalClass]
public partial class ArenaMatch : Node3D
{
    [Export] public ArenaMode Mode { get; set; } = ArenaMode.Play;
    [Export] public NavigationRegion3D? NavigationRegion { get; set; }
    [Export] public PlayerController? Player { get; set; }
    [Export] public PackedScene? NpcScene { get; set; }
    [Export] public int SquadSize { get; set; } = 4;
    [Export] public int RoundsPerTactic { get; set; } = 12;
    [Export] public float RoundTimeout { get; set; } = 120f;
    [Export] public int Seed { get; set; } = 7;
    [Export] public TacticKind StartTactic { get; set; } = TacticKind.Flank;
    /// <summary>Прогон: только эта тактика (-1 — все по очереди).</summary>
    [Export] public int OnlyTactic { get; set; } = -1;
    [ExportGroup("Игрок-бот")]
    [Export] public float BotHealth { get; set; } = 250f;
    [Export] public float BotFireCooldown { get; set; } = 0.45f;
    /// <summary>Стиль бота: false — напористый, true — обороняется у своей точки старта.</summary>
    [Export] public bool DefensiveBot { get; set; }
    /// <summary>Записывать итог в docs/results (выключено у сцен для записи роликов).</summary>
    [Export] public bool SaveReport { get; set; } = true;
    /// <summary>Добавка к имени файлов отчёта, чтобы отдельный прогон не затирал общие сводки.</summary>
    [Export] public string ReportSuffix { get; set; } = "";
    /// <summary>
    /// Токены огня (настройка сложности для любой тактики): сколько бойцов одновременно стреляют
    /// по приказу; 0 — без ограничения. В игре переключается клавишей T.
    /// </summary>
    [Export] public int MaxShooters { get; set; }
    /// <summary>
    /// Опция «смельчаки» поверх любой тактики: особо храбрый боец в одиночку идёт на штурм, когда отряд
    /// давно не попадал по цели. В игре: B — включить/выключить, P — отправить смельчака сейчас.
    /// </summary>
    [Export] public bool BraveFighters { get; set; }
    [ExportGroup("Стелс")]
    /// <summary>
    /// Стелс-режим: отряд стартует патрулём и не знает об игроке (зрение, слух, подозрение,
    /// тревога, поиск), ночь с факелами; бот-игрок подкрадывается и устраняет тихо.
    /// </summary>
    [Export] public bool Stealth { get; set; }
    [Export] public float StealthAmbient { get; set; } = 0.22f;
    [ExportGroup("Игровой профиль")]
    /// <summary>
    /// Только ручная игра (Play): быстрее убивает и восстанавливает здоровье. Враги не меняются; прогоны
    /// с ботом профиль не затрагивает. В игре F1 переключает игровой профиль и профиль замеров.
    /// </summary>
    [Export] public bool PlayProfile { get; set; } = true;
    /// <summary>Урон выстрела игрока: 25 — враг со 100 HP падает с четырёх попаданий.</summary>
    [Export] public float PlayShotDamage { get; set; } = 25f;
    [Export] public float PlayFireCooldown { get; set; } = 0.55f;
    /// <summary>Сколько секунд без урона до начала восстановления здоровья.</summary>
    [Export] public float PlayRegenDelay { get; set; } = 5f;
    [Export] public float PlayRegenPerSecond { get; set; } = 20f;
    /// <summary>Выстрелов в магазине (0 — без магазина); пустой магазин перезаряжается сам, F — вручную.</summary>
    [Export] public int PlayMagazine { get; set; } = 8;
    [Export] public float PlayReloadTime { get; set; } = 1.6f;

    private TacticalWorld _world = null!;
    private Squad _squad = null!;
    private readonly List<NpcAgent> _npcs = new();
    private Label _hud = null!;
    private bool _ready;
    private int _waitFrames;

    private TacticKind _tactic;
    private int _round;
    private float _roundTime;
    private bool _roundOver;
    private float _overDelay;
    private RoundStats _stats = new();
    private readonly List<RoundStats> _results = new();
    private PlayerBot? _bot;
    private readonly List<TacticKind> _queue = new();
    private int _score;
    private readonly List<StealthBrain> _brains = new();
    private readonly List<PatrolRoute> _routes = new();

    // ручная игра: исходные значения игрока (профиль замеров), наблюдатель, скорость времени
    private float _baseShotDamage, _baseFireCooldown;
    private float _sinceHit;
    private bool _observer;
    private static readonly float[] TimeScales = { 0.5f, 1f, 2f };
    private int _timeScaleIndex = 1;
    private int _savedAmmo = -1;
    /// <summary>Показан ли подробный текст арены (H); HP игрока и врагов видны всегда.</summary>
    private bool _showText = true;
    private Label? _hint;
    private PlayOverlay? _overlay;

    private sealed class RoundStats
    {
        public TacticKind Tactic;
        public int Round;
        public string Outcome = "";
        public float Time;
        public int SquadShots, SquadHits, PlayerShots, PlayerHits, FriendlyHits, SquadDeaths;
        public float DamageToPlayer, DamageToSquad, Crossfire, Engaged, PlayerHpLeft;
        public float DetectedAt = -1f;
        public int Takedowns, TakedownsBeforeAlarm, SearchesStarted, SearchesSucceeded;
        public int PeekUp, PeekSide, CombatShots, SelfDefenseShots, Evades, Sorties, Flanks, Ambushes;
        public float FighterTime, OpenStillTime, CrowdTime, TooCloseTime;
    }

    public override void _Ready()
    {
        NavigationRegion?.BakeNavigationMesh(onThread: false);
        _world = new TacticalWorld(this);
        BuildHud();
        if (Mode == ArenaMode.Experiment)
            _queue.AddRange(OnlyTactic >= 0 ? new[] { (TacticKind)OnlyTactic } : Enum.GetValues<TacticKind>());
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        if (!_ready)
        {
            // навигационная карта синхронизируется не сразу
            if (NavigationServer3D.MapGetIterationId(GetWorld3D().NavigationMap) == 0 || ++_waitFrames < 3)
                return;
            Setup();
            _ready = true;
            return;
        }

        if (Mode == ArenaMode.Play)
            Regenerate(dt);
        if (!_roundOver)
        {
            _roundTime += dt;
            _squad.Update(dt);
            if (Stealth && _stats.DetectedAt < 0f && _brains.Any(b => b.AlarmedAt is not null))
                _stats.DetectedAt = _roundTime;
            var outcome = Player!.IsDead ? "отряд" : _npcs.All(n => n.IsDead) ? "игрок"
                : _roundTime >= RoundTimeout ? "ничья" : null;
            if (outcome is not null)
                EndRound(outcome);
        }
        else
        {
            _overDelay -= dt;
            if (_overDelay <= 0f)
                NextRound();
        }
        UpdateHud();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Mode != ArenaMode.Play || !_ready || @event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode >= Key.Key1 && key.Keycode < Key.Key1 + Enum.GetValues<TacticKind>().Length)
        {
            _tactic = (TacticKind)(key.Keycode - Key.Key1);
            StartRound();
        }
        else if (key.Keycode == Key.R)
        {
            StartRound();
        }
        else if (key.Keycode == Key.P)
        {
            // отправить смельчака на штурм сейчас (заодно включает опцию)
            BraveFighters = true;
            _squad.BraveEnabled = true;
            _squad.ProvokeSortie = true;
        }
        else if (key.Keycode == Key.B)
        {
            BraveFighters = !BraveFighters;
            _squad.BraveEnabled = BraveFighters;
        }
        else if (key.Keycode == Key.T)
        {
            MaxShooters = MaxShooters == 0 ? 2 : 0;
            _squad.MaxShooters = MaxShooters;
        }
        else if (key.Keycode == Key.F1)
        {
            PlayProfile = !PlayProfile;
            ApplyPlayProfile();
        }
        else if (key.Keycode == Key.F)
        {
            Player!.Reload();
        }
        else if (key.Keycode == Key.H)
        {
            _showText = !_showText;
            NpcAgent.ShowLabelDetails = _showText; // над врагами остаётся только HP
        }
        else if (key.Keycode == Key.G)
        {
            _observer = !_observer;
            Player!.Invulnerable = _observer;
        }
        else if (key.Keycode is Key.Bracketleft or Key.Bracketright)
        {
            _timeScaleIndex = Mathf.Clamp(_timeScaleIndex + (key.Keycode == Key.Bracketright ? 1 : -1), 0, TimeScales.Length - 1);
            Engine.TimeScale = TimeScales[_timeScaleIndex];
        }
    }

    public override void _ExitTree()
    {
        if (Mode == ArenaMode.Play)
        {
            Engine.TimeScale = 1.0;
            NpcAgent.ShowLabelDetails = true;
        }
    }

    /// <summary>Игровой профиль включён — быстрое оружие; выключен — исходные значения игрока (как в замерах).</summary>
    private void ApplyPlayProfile()
    {
        if (Mode != ArenaMode.Play)
            return;
        Player!.ShotDamage = PlayProfile ? PlayShotDamage : _baseShotDamage;
        Player.FireCooldown = PlayProfile ? PlayFireCooldown : _baseFireCooldown;
        Player.ReloadTime = PlayReloadTime;
        // в профиле замеров магазина нет; переключение F1 туда и обратно патроны не добавляет
        if (PlayProfile)
            Player.ResizeMagazine(PlayMagazine, _savedAmmo >= 0 ? _savedAmmo : PlayMagazine);
        else
        {
            if (Player.MagazineSize > 0)
                _savedAmmo = Player.Ammo;
            Player.ResizeMagazine(0, 0);
        }
    }

    /// <summary>Восстановление здоровья игрока в игровом профиле: через паузу без урона, до максимума.</summary>
    private void Regenerate(float dt)
    {
        _sinceHit += dt;
        if (!PlayProfile || Player!.IsDead || _sinceHit < PlayRegenDelay || Player.Health >= Player.MaxHealth)
            return;
        Player.Heal(PlayRegenPerSecond * dt);
    }

    private void Setup()
    {
        LightField.Ambient = Stealth ? StealthAmbient : 1f;
        if (Stealth)
        {
            StealthBrain.Log = Mode == ArenaMode.Play || RoundsPerTactic <= 2;
            // ночь: приглушить солнце и фон (только картинка; для зрения NPC важен LightField)
            if (GetNodeOrNull<DirectionalLight3D>("Sun") is { } sun)
                sun.LightEnergy = 0.12f;
            if (GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment is { } env)
                env.AmbientLightEnergy = 0.15f;
        }
        _world.BuildCoverPoints(this);
        // параметры боя для тактики «Роли»: сильные позиции из узлов сцены, а если их нет — разметка арены из кода
        var marks = GetTree().GetNodesInGroup(HardPointMarker.Group).OfType<HardPointMarker>().ToList();
        var encounter = EncounterParams.ForArena(SquadSize);
        EncounterParams.Current = marks.Count > 0
            ? encounter with { HardPoints = marks.Select(m => m.ToHardPoint()).ToList() }
            : encounter;
        GD.Print($"[арена] точек укрытий: {_world.CoverPoints.Count}");
        for (var i = 0; i < SquadSize; i++)
        {
            var npc = NpcScene!.Instantiate<NpcAgent>();
            npc.Name = $"Enemy{i + 1}";
            npc.Behaviour = NpcBehaviour.Idle;
            npc.Team = 1;
            AddChild(npc);
            _npcs.Add(npc);
            npc.Fired += _ => _stats.SquadShots++;
            npc.Damaged += (a, amount, src) =>
            {
                _stats.DamageToSquad += amount;
                if (src is ActorBody { Team: 1 })
                    _stats.FriendlyHits++;
                else
                    _stats.PlayerHits++;
            };
            npc.Died += _ => _stats.SquadDeaths++;
        }
        _squad = new Squad(_world, Player!) { Omniscient = !Stealth };
        foreach (var n in _npcs)
            _squad.Add(n);

        Player!.AutoRespawn = false;
        Player.TookDown += (_, _) =>
        {
            _stats.Takedowns++;
            if (_stats.DetectedAt < 0f)
                _stats.TakedownsBeforeAlarm++;
        };
        if (Stealth)
            BuildPatrolRoutes();
        Player.Fired += _ => _stats.PlayerShots++;
        Player.Damaged += (_, amount, src) =>
        {
            _sinceHit = 0f;
            _stats.DamageToPlayer += amount;
            if (src is ActorBody { Team: 1 })
                _stats.SquadHits++;
            _bot?.OnDamaged();
        };
        Player.ShotAt += (_, _) =>
        {
            _bot?.OnShotAt();
        };
        if (Mode == ArenaMode.Experiment)
        {
            Player.MaxHealth = BotHealth;
            Player.FireCooldown = BotFireCooldown;
            _tactic = _queue[0];
        }
        else
        {
            _tactic = StartTactic;
            _baseShotDamage = Player.ShotDamage;
            _baseFireCooldown = Player.FireCooldown;
            ApplyPlayProfile();
            // кольцо перезарядки и затемнение при низком здоровье — только для живого игрока с окном
            if (DisplayServer.GetName() != "headless")
            {
                _overlay = new PlayOverlay(Player);
                AddChild(_overlay);
            }
        }
        StartRound();
    }

    private void StartRound()
    {
        foreach (var f in GetTree().GetNodesInGroup("fireball"))
            f.QueueFree();

        var rng = new Random(Seed * 1000 + _round);
        var px = (float)(rng.NextDouble() * 20 - 10);
        var sx = (float)(rng.NextDouble() * 20 - 10);
        Place(Player!, new Vector3(px, 0.05f, 16f));
        for (var i = 0; i < _npcs.Count; i++)
        {
            var start = Stealth
                ? _routes[i].Points[rng.Next(_routes[i].Points.Count)].GlobalPosition
                : new Vector3(sx + (i - (_npcs.Count - 1) / 2f) * 2f, 0f, -16f);
            Place(_npcs[i], start + Vector3.Up * 0.05f);
        }

        if (Mode == ArenaMode.Experiment)
        {
            _bot = new PlayerBot(_world, () => _npcs, Seed * 1000 + _round)
            {
                Defensive = DefensiveBot,
                Stealthy = Stealth,
                AlarmRaised = () => _brains.Any(b => b.AlarmedAt is not null),
            };
            Player!.Bot = _bot;
        }
        _squad.MaxShooters = MaxShooters;
        _squad.BraveEnabled = BraveFighters;
        BraveLayer.SortiesStarted = 0;
        RolesTactic.FlanksStarted = 0;
        RolesTactic.AmbushesSprung = 0;
        _squad.SetTactic(TacticFactory.Create(_tactic), Seed * 1000 + _round);
        if (Stealth)
        {
            _brains.Clear();
            foreach (var n in _npcs)
            {
                n.Alert = AlertState.Calm;
                var brain = new StealthBrain(_squad, n);
                _brains.Add(brain);
                n.Brain = brain;
            }
        }
        _stats = new RoundStats { Tactic = _tactic, Round = _round };
        CombatStats.Reset();
        _roundTime = 0f;
        _roundOver = false;
    }

    private static void Place(ActorBody a, Vector3 p)
    {
        a.GlobalPosition = p;
        a.Velocity = Vector3.Zero;
        a.StopMoving();
        a.ClearAim();
        a.SetCrouching(false);
        a.Revive();
    }

    /// <summary>Маршруты патруля для стелс-режима: у каждого NPC своя полоса в северной половине арены.</summary>
    private void BuildPatrolRoutes()
    {
        for (var i = 0; i < _npcs.Count; i++)
        {
            var cx = -12f + 24f * i / Mathf.Max(1, _npcs.Count - 1);
            var route = new PatrolRoute { Name = $"Route{i + 1}", Mode = PatrolMode.PingPong };
            AddChild(route);
            foreach (var p in new[] { new Vector3(cx, 0, -15f), new Vector3(cx + 2f, 0, -6f), new Vector3(cx - 2f, 0, 3f) })
            {
                var point = new PatrolPoint { WaitTime = 2f };
                route.AddChild(point);
                point.GlobalPosition = _world.SnapToNav(p);
            }
            _routes.Add(route);
            _npcs[i].Route = route;
        }
    }

    private void EndRound(string outcome)
    {
        _roundOver = true;
        _overDelay = Mode == ArenaMode.Experiment ? 0.2f : 3f;
        _stats.Outcome = outcome;
        _stats.Time = _roundTime;
        _stats.Crossfire = _squad.CrossfireTime;
        _stats.Engaged = _squad.EngagedTime;
        _stats.PlayerHpLeft = Player!.Health;
        _stats.SearchesStarted = _squad.SearchesStarted;
        _stats.SearchesSucceeded = _squad.SearchesSucceeded;
        _stats.PeekUp = CombatStats.PeekUp;
        _stats.PeekSide = CombatStats.PeekSide;
        _stats.CombatShots = CombatStats.Shots;
        _stats.SelfDefenseShots = CombatStats.SelfDefenseShots;
        _stats.Evades = CombatStats.Evades;
        _stats.Sorties = BraveLayer.SortiesStarted;
        _stats.Flanks = RolesTactic.FlanksStarted;
        _stats.Ambushes = RolesTactic.AmbushesSprung;
        _stats.CrowdTime = CombatStats.CrowdTime;
        _stats.TooCloseTime = CombatStats.TooCloseTime;
        _stats.FighterTime = CombatStats.FighterTime;
        _stats.OpenStillTime = CombatStats.OpenStillTime;
        _results.Add(_stats);
        if (outcome == "отряд")
            _score++;
        GD.Print(string.Format(CultureInfo.InvariantCulture,
            "[раунд] {0} #{1}: победа — {2}, {3:0.0} с, отряд {4}/{5} попаданий, игрок {6}/{7}, потери отряда {8}, свои по своим {9}",
            _squad.Tactic!.Name, _stats.Round + 1, outcome, _stats.Time, _stats.SquadHits, _stats.SquadShots,
            _stats.PlayerHits, _stats.PlayerShots, _stats.SquadDeaths, _stats.FriendlyHits));
    }

    private void NextRound()
    {
        if (Mode == ArenaMode.Play)
        {
            StartRound();
            return;
        }
        _round++;
        if (_round >= RoundsPerTactic)
        {
            _round = 0;
            _queue.RemoveAt(0);
            if (_queue.Count == 0)
            {
                WriteReport();
                GetTree().CallDeferred(SceneTree.MethodName.Quit, 0);
                return;
            }
            _tactic = _queue[0];
        }
        StartRound();
    }

    private string Style => (Stealth ? "stealth" : DefensiveBot ? "defensive_bot" : "aggressive_bot") + ReportSuffix;

    private void WriteReport()
    {
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("tactic,round,outcome,time,squad_shots,squad_hits,player_shots,player_hits,friendly_hits,squad_deaths,damage_to_player,damage_to_squad,crossfire,engaged,player_hp_left,detected_at,takedowns,takedowns_before_alarm,searches,searches_found,peek_up,peek_side,selfdefense_shots,evades,fighter_time,open_still_time,sorties,crowd_time,too_close_time,flanks,ambushes\n");
        foreach (var r in _results)
            csv.AppendLine(string.Join(",", r.Tactic, r.Round + 1, r.Outcome, r.Time.ToString("0.0", inv),
                r.SquadShots, r.SquadHits, r.PlayerShots, r.PlayerHits, r.FriendlyHits, r.SquadDeaths,
                r.DamageToPlayer.ToString("0", inv), r.DamageToSquad.ToString("0", inv),
                r.Crossfire.ToString("0.0", inv), r.Engaged.ToString("0.0", inv), r.PlayerHpLeft.ToString("0", inv),
                r.DetectedAt.ToString("0.0", inv), r.Takedowns, r.TakedownsBeforeAlarm, r.SearchesStarted, r.SearchesSucceeded,
                r.PeekUp, r.PeekSide, r.SelfDefenseShots, r.Evades, r.FighterTime.ToString("0.0", inv),
                r.OpenStillTime.ToString("0.0", inv), r.Sorties, r.CrowdTime.ToString("0.0", inv),
                r.TooCloseTime.ToString("0.0", inv), r.Flanks, r.Ambushes));

        var md = new StringBuilder();
        md.AppendLine($"Бот-игрок ({(Stealth ? "подкрадывается, после тревоги — напористый бой" : DefensiveBot ? "обороняется у старта" : "напористый")}): HP {BotHealth:0}, перезарядка {BotFireCooldown:0.00} с; отряд: {SquadSize} × HP 100, " +
                      $"перезарядка 0.8 с; выстрел — хитскан с моделью промаха, урон 20; {RoundsPerTactic} раундов на тактику, таймаут {RoundTimeout:0} с.");
        md.AppendLine();
        md.AppendLine("| Тактика | Победы отряда | Ничьи | Время до победы, с | Потери отряда за раунд | Урон по игроку за раунд | Точность отряда | Точность игрока | Свои по своим | Перекрёстный огонь, % боя |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var g in _results.GroupBy(r => r.Tactic))
        {
            var n = g.Count();
            var wins = g.Where(r => r.Outcome == "отряд").ToList();
            var draws = g.Count(r => r.Outcome == "ничья");
            var shots = g.Sum(r => r.SquadShots);
            var pshots = g.Sum(r => r.PlayerShots);
            var engaged = g.Sum(r => r.Engaged);
            md.AppendLine(string.Format(inv,
                "| {0} | {1}/{2} | {3} | {4} | {5:0.0} | {6:0} | {7:0}% | {8:0}% | {9} | {10:0}% |",
                TacticFactory.Create(g.Key).Name, wins.Count, n, draws,
                wins.Count > 0 ? wins.Average(r => r.Time).ToString("0", inv) : "—",
                g.Average(r => r.SquadDeaths), g.Average(r => r.DamageToPlayer),
                shots > 0 ? 100.0 * g.Sum(r => r.SquadHits) / shots : 0,
                pshots > 0 ? 100.0 * g.Sum(r => r.PlayerHits) / pshots : 0,
                g.Sum(r => r.FriendlyHits),
                engaged > 0 ? 100.0 * g.Sum(r => r.Crossfire) / engaged : 0));
        }

        md.AppendLine();
        md.AppendLine("Поведение бойцов (уровень «боец в бою», одинаков для всех тактик):");
        md.AppendLine();
        md.AppendLine("| Тактика | Выглядываний вверх за раунд | Выглядываний вбок за раунд | Выстрелов в самообороне | Уходов из-под огня за раунд | Стоит на виду без дела, % времени | Вылазок за раунд | Обходов за раунд | Засад сработало за раунд | Толпятся (ближе 1,6 м к своему), % времени | Срезают обход мимо цели, % времени |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var g in _results.GroupBy(r => r.Tactic))
        {
            var ft = g.Sum(r => r.FighterTime);
            var cs = g.Sum(r => r.CombatShots);
            md.AppendLine(string.Format(inv, "| {0} | {1:0.0} | {2:0.0} | {3:0}% | {4:0.0} | {5:0}% | {6:0.0} | {9:0.0} | {10:0.0} | {7:0.0}% | {8:0.0}% |",
                TacticFactory.Create(g.Key).Name, g.Average(r => r.PeekUp), g.Average(r => r.PeekSide),
                cs > 0 ? 100.0 * g.Sum(r => r.SelfDefenseShots) / cs : 0, g.Average(r => r.Evades),
                ft > 0 ? 100.0 * g.Sum(r => r.OpenStillTime) / ft : 0, g.Average(r => r.Sorties),
                ft > 0 ? 100.0 * g.Sum(r => r.CrowdTime) / ft : 0, ft > 0 ? 100.0 * g.Sum(r => r.TooCloseTime) / ft : 0,
                g.Average(r => r.Flanks), g.Average(r => r.Ambushes)));
        }

        if (Stealth)
        {
            md.AppendLine();
            md.AppendLine($"Стелс-фаза: ночь (фон {StealthAmbient:0.00}), отряд патрулирует и не знает об игроке.");
            md.AppendLine();
            md.AppendLine("| Тактика | Раундов с тревогой | Время до тревоги, с | Тихих устранений до тревоги за раунд | Поисков начато | Поисков с повторным обнаружением |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var g in _results.GroupBy(r => r.Tactic))
            {
                var det = g.Where(r => r.DetectedAt >= 0f).ToList();
                md.AppendLine(string.Format(inv, "| {0} | {1}/{2} | {3} | {4:0.0} | {5} | {6} |",
                    TacticFactory.Create(g.Key).Name, det.Count, g.Count(),
                    det.Count > 0 ? det.Average(r => r.DetectedAt).ToString("0", inv) : "—",
                    g.Average(r => r.TakedownsBeforeAlarm), g.Sum(r => r.SearchesStarted), g.Sum(r => r.SearchesSucceeded)));
            }
        }

        GD.Print("[итог арены]\n" + md);
        if (!SaveReport)
            return;
        var dir = ProjectSettings.GlobalizePath("res://docs/results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"arena_rounds_{Style}.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(dir, $"arena_summary_{Style}.md"), md.ToString());
    }

    private void BuildHud()
    {
        // слой выше затемнения при низком здоровье (PlayOverlay.EffectLayer)
        var layer = new CanvasLayer { Name = "ArenaHud", Layer = PlayOverlay.HudLayer };
        AddChild(layer);
        _hud = new Label { Position = new Vector2(16f, 40f) };
        _hud.AddThemeFontSizeOverride("font_size", 20);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_hud);
        if (Mode != ArenaMode.Play)
            return;
        _hint = new Label
        {
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = -220f, OffsetTop = -34f, OffsetRight = -12f, OffsetBottom = -8f,
            HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0.7f),
        };
        _hint.AddThemeFontSizeOverride("font_size", 14);
        _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hint.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hint);
    }

    private void UpdateHud()
    {
        if (_squad?.Tactic is null)
            return;
        var alive = _npcs.Count(n => !n.IsDead);
        var head = Mode == ArenaMode.Play
            ? $"Тактика: {(int)_tactic + 1}. {_squad.Tactic.Name}   (1–5 — сменить, R — заново, T — токены огня: {(MaxShooters > 0 ? $"стреляют не больше {MaxShooters}" : "выкл")})"
            : $"Тактика: {_squad.Tactic.Name}, раунд {_round + 1}/{RoundsPerTactic}";
        var p = Player!;
        var enemies = string.Join("  ", _npcs.Select((n, i) => $"{i + 1}: {(n.IsDead ? "убит" : $"{n.Health:0}")}"));
        var pressure = Mode != ArenaMode.Play ? ""
            : $"\nСмельчаки (B): {(_squad.BraveOn ? "ВКЛ, " + _squad.Brave.Status(_squad) : "выкл")}   (P — смельчак идёт на штурм сейчас)" +
              (PlayProfile
                  ? $"\nПрофиль (F1): игровой — урон {PlayShotDamage:0}, выстрел {PlayFireCooldown:0.0#} с, реген через {PlayRegenDelay:0.#} с" +
                    (p.MagazineSize > 0
                        ? p.IsReloading ? $"   Перезарядка… {p.ReloadLeft:0.0} с" : $"   Магазин {p.Ammo}/{p.MagazineSize} (F — перезарядить)"
                        : "")
                  : $"\nПрофиль (F1): замеров — урон {_baseShotDamage:0}, выстрел {_baseFireCooldown:0.0#} с, без регена") +
              $"   Наблюдатель (G): {(_observer ? "ВКЛ — урон по вам не проходит" : "выкл")}" +
              $"   Скорость ([ ]): {TimeScales[_timeScaleIndex]:0.0#}×";
        if (_hint is not null)
            _hint.Text = _showText ? "H — скрыть текст" : "H — показать текст";
        if (Mode == ArenaMode.Play && !_showText)
        {
            // HP игрока (его строка вверху) и подписи над врагами остаются; остальной текст скрыт
            _hud.Text = _roundOver ? $"Раунд окончен: победа — {_stats.Outcome}" : "";
            return;
        }
        _hud.Text = $"{head}\nВрагов: {alive}/{_npcs.Count}   время {_roundTime:0} с   побед отряда: {_score}" +
                    $"\nВаше здоровье: {p.Health:0}/{p.MaxHealth:0} ({100f * p.Health / Mathf.Max(1f, p.MaxHealth):0}%)   враги: {enemies}" +
                    pressure +
                    (_squad.Tactic is RolesTactic roles ? "\n" + roles.Status(_squad) : "") +
                    (_roundOver ? $"\nРаунд окончен: победа — {_stats.Outcome}" : "");
    }
}
