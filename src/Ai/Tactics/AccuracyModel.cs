using System;
using Godot;
using Zero.Actors;
using Zero.Ai.Stealth;

namespace Zero.Ai.Tactics;

/// <summary>
/// Механика промаха врагов при хитскан-стрельбе. Вместо случайного разброса каждый выстрел —
/// бросок вероятности попадания, а промах направляется мимо цели, но рядом (трассер проходит
/// в метре от игрока — игрок видит, что по нему стреляют, и успевает среагировать).
///
/// Вероятность = база × дистанция × скорость цели × присед × открытость тела × свет ×
/// движение стрелка × «пристрелка». Пристрелка: сразу после того, как боец увидел цель,
/// точность низкая и растёт за ~2 с; первый выстрел после обнаружения — всегда
/// предупредительный промах. Так игрок наказывается за то, что долго стоит на виду,
/// а не за то, что просто попался на глаза.
/// </summary>
public static class AccuracyModel
{
    public static float Base { get; set; } = 0.7f;
    /// <summary>За сколько секунд прицеливания точность выходит на максимум.</summary>
    public static float SettleTime { get; set; } = 2f;
    /// <summary>Множитель сложности (1 — норма; 0.7 — легко; 1.3 — тяжело).</summary>
    public static float Difficulty { get; set; } = 1f;
    public static float MinChance { get; set; } = 0.05f;
    public static float MaxChance { get; set; } = 0.9f;

    /// <summary>Разбор вероятности по множителям — для отладки и отчёта.</summary>
    public readonly record struct Breakdown(float Chance, float Distance, float Motion, float Stance,
        float Exposure, float Light, float ShooterMoving, float Settle);

    public static Breakdown Evaluate(TacticalWorld world, ActorBody shooter, ActorBody target,
        float aimTime, bool shooterMoving)
    {
        var from = shooter.MuzzlePosition;
        var d = from.DistanceTo(target.CenterMass);
        var distance = Mathf.Clamp(1.1f - d / 35f, 0.35f, 1f);

        var speed = target.HorizontalSpeed;
        var motion = speed > 4f ? 0.55f : speed > 2f ? 0.75f : speed > 0.3f ? 0.9f : 1f;
        var stance = target.IsCrouching ? 0.85f : 1f;

        var visible = 0;
        var head = target.GlobalPosition + Vector3.Up * (target.IsCrouching ? 1.05f : 1.65f);
        if (world.ClearLine(from, head)) visible++;
        if (world.ClearLine(from, target.CenterMass)) visible++;
        if (world.ClearLine(from, target.GlobalPosition + Vector3.Up * 0.35f)) visible++;
        var exposure = 0.4f + 0.6f * visible / 3f;

        var light = 0.5f + 0.5f * LightField.LevelAt(shooter, target.CenterMass);
        var moving = shooterMoving ? 0.6f : 1f;
        var settle = Mathf.Min(1f, 0.35f + 0.65f * aimTime / SettleTime);

        var chance = Base * distance * motion * stance * exposure * light * moving * settle * Difficulty;
        chance = Mathf.Clamp(chance, MinChance, MaxChance);
        return new Breakdown(chance, distance, motion, stance, exposure, light, moving, settle);
    }

    /// <summary>Точка прицеливания: при попадании — в видимую часть тела (или в центр) с малым разбросом, при промахе — рядом мимо.</summary>
    public static Vector3 AimPoint(Random rng, ActorBody shooter, ActorBody target, bool hit, Vector3? part = null)
    {
        var center = target.CenterMass;
        if (hit)
            return part is { } pt
                ? pt + new Vector3(Rand(rng), Rand(rng), Rand(rng)) * 0.05f // видна только часть тела — бьёт точно в неё
                : center + new Vector3(Rand(rng), Rand(rng) * 1.5f, Rand(rng)) * 0.12f;
        var dir = center - shooter.MuzzlePosition;
        dir.Y = 0f;
        var side = dir.LengthSquared() > 0.001f ? dir.Normalized().Cross(Vector3.Up) : Vector3.Right;
        var sign = rng.Next(2) == 0 ? -1f : 1f;
        // мимо сбоку на 0.7–1.3 м или над головой — достаточно, чтобы не задеть капсулу (радиус 0.35)
        var lateral = 0.7f + 0.6f * (float)rng.NextDouble();
        var up = -0.2f + 0.9f * (float)rng.NextDouble();
        var from = shooter.MuzzlePosition;
        // промах не должен лететь в своих за спиной цели: пробуем сторону, другую сторону, над головой
        foreach (var p in new[]
                 {
                     center + side * sign * lateral + Vector3.Up * up,
                     center - side * sign * lateral + Vector3.Up * up,
                     center + Vector3.Up * 1.2f,
                 })
        {
            if (!HitsAlly(shooter, from, p))
                return p;
        }
        return center + Vector3.Up * 1.2f;
    }

    /// <summary>Заденет ли луч выстрела (до дальности оружия) союзника стрелка.</summary>
    private static bool HitsAlly(ActorBody shooter, Vector3 from, Vector3 through)
    {
        var to = from + (through - from).Normalized() * shooter.ShotRange;
        using var q = PhysicsRayQueryParameters3D.Create(from, to);
        q.Exclude = new() { shooter.GetRid() };
        var hit = shooter.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 && hit["collider"].AsGodotObject() is ActorBody a && a.Team == shooter.Team && !a.IsDead;
    }

    private static float Rand(Random rng) => (float)rng.NextDouble() * 2f - 1f;
}
