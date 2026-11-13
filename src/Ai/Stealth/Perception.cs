using System;
using System.Collections.Generic;
using Godot;
using Zero.Actors;

namespace Zero.Ai.Stealth;

/// <summary>Состояния тревоги NPC (автомат из документа по архитектуре).</summary>
public enum AlertState { Calm, Suspicious, Searching, Alarm }

public enum NoiseKind
{
    /// <summary>Шаги: громкость зависит от темпа и приседа.</summary>
    Footstep,
    /// <summary>Удар, взрыв огненного шара, падение тела.</summary>
    Impact,
    /// <summary>Выстрел огненным шаром (слышно, откуда стреляли).</summary>
    Shot,
    /// <summary>Крик союзника о тревоге — несёт позицию цели.</summary>
    Shout,
}

/// <summary>Звуковое событие: где, насколько далеко слышно, кто издал.</summary>
public readonly record struct NoiseEvent(NoiseKind Kind, Vector3 Position, float Radius, ActorBody? Source, Vector3 Info);

/// <summary>
/// Шина звуков. Любой может «издать звук», все подписанные слушатели получают его, если
/// расстояние по навигационной сетке (а не по прямой) меньше радиуса — как распространение
/// звука через проходы в Splinter Cell: Blacklist.
/// </summary>
public static class NoiseSystem
{
    private static readonly List<Action<NoiseEvent>> Listeners = new();

    public static void Subscribe(Action<NoiseEvent> listener) => Listeners.Add(listener);
    public static void Unsubscribe(Action<NoiseEvent> listener) => Listeners.Remove(listener);

    public static void Emit(NoiseEvent noise)
    {
        foreach (var l in Listeners.ToArray())
            l(noise);
    }

    /// <summary>Длина пути по навигационной сетке между точками (или прямая, если сетки нет).</summary>
    public static float PathLength(Rid map, Vector3 from, Vector3 to)
    {
        var path = NavigationServer3D.MapGetPath(map, from + Vector3.Up * 0.5f, to + Vector3.Up * 0.5f, true);
        if (path.Length < 2)
            return from.DistanceTo(to);
        var len = 0f;
        for (var i = 1; i < path.Length; i++)
            len += path[i - 1].DistanceTo(path[i]);
        // путь мог закончиться в ближайшей доступной точке — добавим остаток по прямой
        return len + path[^1].DistanceTo(to + Vector3.Up * 0.5f);
    }
}

/// <summary>
/// Освещённость точки для зрения NPC: общий фон плюс вклад источников света из группы
/// «light_source» (OmniLight3D), если источник видит точку. 0 — темно, 1 — светло.
/// </summary>
public static class LightField
{
    /// <summary>Фоновая освещённость (ночь — 0.2, день — 1).</summary>
    public static float Ambient { get; set; } = 1f;

    public static float LevelAt(Node3D context, Vector3 point)
    {
        var level = Ambient;
        if (level >= 1f)
            return 1f;
        var space = context.GetWorld3D().DirectSpaceState;
        foreach (var node in context.GetTree().GetNodesInGroup("light_source"))
        {
            if (node is not OmniLight3D light || !light.Visible)
                continue;
            var d = light.GlobalPosition.DistanceTo(point);
            if (d >= light.OmniRange)
                continue;
            using var q = PhysicsRayQueryParameters3D.Create(light.GlobalPosition, point, 1);
            if (space.IntersectRay(q).Count > 0)
                continue; // источник закрыт препятствием — тень
            var k = 1f - d / light.OmniRange;
            level += light.LightEnergy * 0.5f * k * k;
        }
        return Mathf.Clamp(level, 0f, 1f);
    }
}
