using System;
using System.Collections.Generic;
using Godot;

namespace Zero.Ai.Tactics;

/// <summary>
/// Веса факторов оценки позиции. Каждая тактика задаёт свой набор — в этом и разница
/// между «укрыться», «обойти», «окружить» (идея оценочных функций Killzone).
/// Все факторы нормированы в 0..1, итог — взвешенная сумма.
/// </summary>
public sealed record PositionWeights
{
    /// <summary>Укрыта от цели (присевшего не видно).</summary>
    public float Cover { get; init; }
    /// <summary>Стоя видно цель (можно стрелять).</summary>
    public float LineOfFire { get; init; }
    /// <summary>Расстояние до цели внутри полосы [MinRange, MaxRange].</summary>
    public float Range { get; init; } = 1f;
    public float MinRange { get; init; } = 6f;
    public float MaxRange { get; init; } = 12f;
    /// <summary>Жёсткая «линия остановки»: точки ближе этого к цели не рассматриваются вовсе.</summary>
    public float HardMinRange { get; init; }
    /// <summary>Близко к бойцу (короткая перебежка).</summary>
    public float Travel { get; init; } = 0.5f;
    /// <summary>Далеко от точек, уже занятых союзниками (разнесение).</summary>
    public float Spacing { get; init; } = 1f;
    /// <summary>Ближе к цели, чем боец сейчас (продвижение).</summary>
    public float Advance { get; init; }
    /// <summary>Угол «цель → точка» близок к <see cref="DesiredAngleDir"/> (фланг).</summary>
    public float Angle { get; init; }
    public Vector3 DesiredAngleDir { get; init; }
    /// <summary>
    /// Жёсткое условие: только точки у препятствий, где присевшего не видно от цели.
    /// Без него укрытость — лишь слагаемое, и открытая точка с хорошей дистанцией иногда побеждает
    /// (на плейтесте враги «садились в укрытие» посреди поля).
    /// </summary>
    public bool RequireCover { get; init; }
    /// <summary>Жёсткое условие: из точки есть выстрел — встав или шагнув вбок (<see cref="TacticalWorld.PeekSpot"/>).</summary>
    public bool RequireShot { get; init; }
    /// <summary>
    /// Жёсткое условие: только точки укрытий, которые закрывает от цели собственное препятствие
    /// (<see cref="TacticalWorld.IsOwnSideCover"/>), — боец садится с дальней от цели стороны.
    /// При ослаблении условий в <see cref="TacticalQuery.FindBest"/> не снимается: лучше остаться на
    /// месте, чем сесть перед стеной. По умолчанию выключено (старые тактики ведут себя как раньше).
    /// </summary>
    public bool RequireOwnSide { get; init; }
}

/// <summary>
/// Запрос «найди лучшую точку»: генерирует кандидатов (точки укрытий + кольца вокруг цели),
/// отбрасывает недопустимые и выбирает максимум взвешенной суммы.
/// По духу — Tactical Point System Crysis / EQS Unreal.
/// </summary>
public static class TacticalQuery
{
    private static readonly float[] Rings = { 5f, 8f, 11f, 14f, 17f };
    private const int RingSamples = 20;

    public static Vector3? FindBest(TacticalWorld world, Vector3 self, Vector3 target, Vector3 targetEye,
        PositionWeights w, IReadOnlyList<Vector3> claimed, float maxTravel = 30f, Func<Vector3, bool>? allow = null)
    {
        var strict = w.RequireCover || w.RequireShot;
        // 1) дешёвые факторы для всех кандидатов; 2) лучи — по убыванию дешёвой оценки, пока
        // дешёвая оценка + максимум «лучевых» слагаемых ещё может побить лучшую (отсечение)
        var cheap = new List<(Vector3 P, float S)>();
        foreach (var p in w.RequireCover ? CoverCandidates(world) : Candidates(world, target))
        {
            // область поиска задаёт вызывающий (сильная позиция: искать внутри неё, а не вокруг цели)
            if (allow is not null && !allow(p))
                continue;
            if (w.RequireOwnSide && world.CoverAt(p, 0.1f) is null)
                continue;
            var s = CheapScore(p, self, target, w, claimed, maxTravel);
            if (s > float.MinValue)
                cheap.Add((p, s));
        }
        cheap.Sort((a, b) => b.S.CompareTo(a.S));
        var rayMax = Mathf.Max(0f, w.Cover) + Mathf.Max(0f, w.LineOfFire);
        var best = float.MinValue;
        Vector3? bestPoint = null;
        foreach (var (p, c) in cheap)
        {
            if (c + rayMax <= best)
                break;
            if (w.RequireOwnSide && !world.IsOwnSideCover(p, target, targetEye))
                continue;
            var score = c + RayScore(world, p, target, targetEye, w);
            if (score > best && (!strict || PassesHard(world, p, target, targetEye, w)))
            {
                best = score;
                bestPoint = p;
            }
        }
        // ни одна точка не прошла жёсткие условия — ослабляем: сначала без выстрела, потом без укрытия
        if (bestPoint is null && w.RequireShot)
            return FindBest(world, self, target, targetEye, w with { RequireShot = false }, claimed, maxTravel, allow);
        if (bestPoint is null && w.RequireCover)
            return FindBest(world, self, target, targetEye, w with { RequireCover = false }, claimed, maxTravel, allow);
        return bestPoint;
    }

    private static IEnumerable<Vector3> CoverCandidates(TacticalWorld world)
    {
        foreach (var c in world.CoverPoints)
            yield return c.Position;
    }

    private static bool PassesHard(TacticalWorld world, Vector3 p, Vector3 target, Vector3 targetEye, PositionWeights w)
    {
        if (w.RequireCover && !world.IsCoveredFrom(p, targetEye))
            return false;
        if (w.RequireShot && world.PeekSpot(p, target) is null)
            return false;
        return true;
    }

    public static IEnumerable<Vector3> Candidates(TacticalWorld world, Vector3 target)
    {
        foreach (var c in world.CoverPoints)
            yield return c.Position;
        foreach (var r in Rings)
        {
            for (var i = 0; i < RingSamples; i++)
            {
                var a = Mathf.Tau * i / RingSamples;
                var p = target + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r;
                p.Y = 0f;
                if (world.IsOnNavmesh(p))
                    yield return p;
            }
        }
    }

    public static float Score(TacticalWorld world, Vector3 p, Vector3 self, Vector3 target, Vector3 targetEye,
        PositionWeights w, IReadOnlyList<Vector3> claimed, float maxTravel)
    {
        var s = CheapScore(p, self, target, w, claimed, maxTravel);
        return s == float.MinValue ? s : s + RayScore(world, p, target, targetEye, w);
    }

    /// <summary>Слагаемые с лучами — самые дорогие.</summary>
    private static float RayScore(TacticalWorld world, Vector3 p, Vector3 target, Vector3 targetEye, PositionWeights w)
    {
        float s = 0f;
        if (w.Cover != 0f)
            s += w.Cover * (world.IsCoveredFrom(p, targetEye) ? 1f : 0f);
        if (w.LineOfFire != 0f)
            s += w.LineOfFire * (world.SeesAnyPart(p + Vector3.Up * 1.6f, target) ? 1f : 0f);
        return s;
    }

    private static float CheapScore(Vector3 p, Vector3 self, Vector3 target, PositionWeights w,
        IReadOnlyList<Vector3> claimed, float maxTravel)
    {
        var flatTo = new Vector2(p.X - target.X, p.Z - target.Z);
        var dist = flatTo.Length();
        if (dist < Mathf.Max(2.5f, w.HardMinRange))
            return float.MinValue;
        var travel = new Vector2(p.X - self.X, p.Z - self.Z).Length();
        if (travel > maxTravel)
            return float.MinValue;

        float s = 0f;
        // дешёвые факторы сначала
        var range = dist < w.MinRange ? 1f - (w.MinRange - dist) / 4f
            : dist > w.MaxRange ? 1f - (dist - w.MaxRange) / 6f
            : 1f;
        s += w.Range * Mathf.Clamp(range, -1f, 1f);
        s += w.Travel * (1f - Mathf.Clamp(travel / maxTravel, 0f, 1f));

        if (w.Spacing != 0f)
        {
            var minD = 10f;
            foreach (var c in claimed)
                minD = Mathf.Min(minD, new Vector2(p.X - c.X, p.Z - c.Z).Length());
            if (minD < 2.5f) // бронь укрытия целиком: соседние точки одного блока и место для шага вбок
                return float.MinValue; // точка уже занята
            s += w.Spacing * Mathf.Clamp(minD / 6f, 0f, 1f);
        }

        if (w.Advance != 0f)
        {
            var selfDist = new Vector2(self.X - target.X, self.Z - target.Z).Length();
            s += w.Advance * Mathf.Clamp((selfDist - dist) / 5f, -1f, 1f);
        }

        if (w.Angle != 0f && w.DesiredAngleDir.LengthSquared() > 0.01f)
        {
            var dir = new Vector3(flatTo.X, 0f, flatTo.Y).Normalized();
            var angle = Mathf.RadToDeg(dir.AngleTo(w.DesiredAngleDir));
            s += w.Angle * (1f - angle / 60f); // в пределах 60° — плюс, дальше — минус
        }

        return s;
    }

    /// <summary>Повернуть горизонтальный вектор на угол (градусы, против часовой стрелки сверху).</summary>
    public static Vector3 Rotate(Vector3 v, float degrees) => v.Rotated(Vector3.Up, Mathf.DegToRad(degrees));

    /// <summary>Детерминированный разброс прицела: точка в конусе <paramref name="spreadDegrees"/>.</summary>
    public static Vector3 Spread(Random rng, Vector3 from, Vector3 to, float spreadDegrees)
    {
        var dist = from.DistanceTo(to);
        var r = dist * Mathf.Tan(Mathf.DegToRad(spreadDegrees));
        var offset = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f,
            (float)rng.NextDouble() * 2f - 1f) * r;
        return to + offset;
    }
}
