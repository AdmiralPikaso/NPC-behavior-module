using System;

namespace Zero.Ai.Core;

/// <summary>Порядок обхода точек патруля.</summary>
public enum PatrolMode
{
    /// <summary>0 → 1 → 2 → 0 → …</summary>
    Loop,
    /// <summary>0 → 1 → 2 → 1 → 0 → …</summary>
    PingPong,
    /// <summary>Случайная точка, не совпадающая с текущей.</summary>
    Random,
}

/// <summary>
/// Чистая логика выбора следующей точки патруля. Не зависит от Godot,
/// поэтому её можно проверять обычными модульными тестами.
/// </summary>
public sealed class PatrolCursor
{
    private readonly Random _rng;
    private int _direction = 1;

    public PatrolCursor(int pointCount, PatrolMode mode, int startIndex = 0, int? seed = null)
    {
        if (pointCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointCount), "В маршруте должна быть хотя бы одна точка.");
        Count = pointCount;
        Mode = mode;
        Current = Math.Clamp(startIndex, 0, pointCount - 1);
        _rng = seed is null ? new Random() : new Random(seed.Value);
    }

    public int Count { get; }
    public PatrolMode Mode { get; }
    public int Current { get; private set; }

    /// <summary>Переходит к следующей точке и возвращает её индекс.</summary>
    public int Advance()
    {
        if (Count == 1)
            return Current;

        switch (Mode)
        {
            case PatrolMode.Loop:
                Current = (Current + 1) % Count;
                break;
            case PatrolMode.PingPong:
                var target = Current + _direction;
                if (target < 0 || target >= Count)
                    _direction = -_direction;
                Current += _direction;
                break;
            case PatrolMode.Random:
                var next = _rng.Next(Count - 1);
                Current = next >= Current ? next + 1 : next;
                break;
        }
        return Current;
    }
}
