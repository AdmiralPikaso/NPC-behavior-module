using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Actors;

namespace Zero.Common;

/// <summary>
/// Корневой скрипт тестовой сцены: при запуске запекает навигационную сетку,
/// чтобы не нужно было перепекать её вручную после перестановки препятствий,
/// пишет в Output журнал боя (попадания и смерти) и итог по выстрелам при выходе.
/// </summary>
[GlobalClass]
public partial class TestLevel : Node3D
{
    [Export] public NavigationRegion3D? NavigationRegion { get; set; }
    [Export] public bool BakeOnReady { get; set; } = true;
    [Export] public bool LogCombat { get; set; } = true;

    private sealed class Stats
    {
        public int Shots, Hits, Deaths;
        public float DamageDealt, DamageTaken;
    }

    private readonly Dictionary<string, Stats> _stats = new();

    public override void _Ready()
    {
        if (BakeOnReady && NavigationRegion is not null)
            NavigationRegion.BakeNavigationMesh(onThread: false);
        if (LogCombat)
            CallDeferred(MethodName.SubscribeCombatLog);
    }

    private Stats For(Node? node) =>
        _stats.TryGetValue(node?.Name ?? "?", out var s) ? s : _stats[node?.Name ?? "?"] = new Stats();

    private void SubscribeCombatLog()
    {
        foreach (var node in FindChildren("*", "CharacterBody3D", true, false))
        {
            if (node is not ActorBody actor)
                continue;
            For(actor);
            actor.Fired += a => For(a).Shots++;
            actor.Damaged += (a, amount, source) =>
            {
                For(a).DamageTaken += amount;
                var src = For(source);
                src.Hits++;
                src.DamageDealt += amount;
                GD.Print($"[бой] {source?.Name ?? "?"} → {a.Name}: −{amount:0} HP (осталось {a.Health:0})");
            };
            actor.Died += a =>
            {
                For(a).Deaths++;
                GD.Print($"[бой] {a.Name} повержен");
            };
        }
    }

    public override void _ExitTree()
    {
        if (!LogCombat || _stats.Count == 0)
            return;
        GD.Print("[итог] имя: выстрелы / попадания / урон нанесён / урон получен / смерти");
        foreach (var (name, s) in _stats.OrderBy(p => p.Key))
            GD.Print($"[итог] {name}: {s.Shots} / {s.Hits} / {s.DamageDealt:0} / {s.DamageTaken:0} / {s.Deaths}");
    }
}
