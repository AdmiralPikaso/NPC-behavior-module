using System;
using System.Collections.Generic;
using Godot;
using Zero.Actors;
using Zero.Ai.Nodes;

namespace Zero.Ai.Actions;

/// <summary>Дойти до точки по навигационной сетке выбранным темпом.</summary>
public sealed class MoveToAction : NpcAction
{
    private readonly Func<Vector3> _target;
    private readonly Gait _gait;
    private readonly string _label;
    private bool _jumped;

    /// <summary>Прыгнуть по пути, когда до цели останется столько метров (0 — не прыгать).</summary>
    public float JumpAtDistance { get; init; }
    public float Tolerance { get; init; } = 0.5f;

    public MoveToAction(Vector3 target, Gait gait, string? label = null)
        : this(() => target, gait, label) { }

    public MoveToAction(Func<Vector3> target, Gait gait, string? label = null)
    {
        _target = target;
        _gait = gait;
        _label = label ?? gait switch
        {
            Gait.Run => "Бег",
            Gait.Sprint => "Спринт",
            _ => "Шаг",
        };
    }

    public override string Name => _label;

    public override void Start(NpcAgent npc) => _jumped = false;

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        var target = _target();
        if (!_jumped && JumpAtDistance > 0f && npc.GlobalPosition.DistanceTo(target) <= JumpAtDistance)
            _jumped = npc.Jump();
        return npc.NavigateTo(target, _gait, Tolerance) switch
        {
            NavResult.Arrived => npc.IsAirborne ? ActionStatus.Running : ActionStatus.Succeeded,
            NavResult.Unreachable => ActionStatus.Failed,
            _ => ActionStatus.Running,
        };
    }

    public override void Stop(NpcAgent npc) => npc.StopMoving();
}

/// <summary>Присесть или встать.</summary>
public sealed class CrouchAction : NpcAction
{
    private readonly bool _crouch;
    public CrouchAction(bool crouch) => _crouch = crouch;
    public override string Name => _crouch ? "Присесть" : "Встать";

    public override ActionStatus Tick(NpcAgent npc, float dt) =>
        npc.SetCrouching(_crouch) ? ActionStatus.Succeeded : ActionStatus.Failed;
}

/// <summary>Прыжок; завершается после приземления.</summary>
public sealed class JumpAction : NpcAction
{
    private bool _started;
    private float _time;
    public override string Name => "Прыжок";

    public override void Start(NpcAgent npc)
    {
        _time = 0f;
        _started = npc.Jump();
    }

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        if (!_started)
            return ActionStatus.Failed;
        _time += dt;
        return _time > 0.1f && !npc.IsAirborne ? ActionStatus.Succeeded : ActionStatus.Running;
    }
}

/// <summary>Стоять на месте заданное время.</summary>
public sealed class WaitAction : NpcAction
{
    private readonly float _duration;
    private readonly string _label;
    private float _left;

    public WaitAction(float seconds, string label = "Ждать")
    {
        _duration = seconds;
        _label = label;
    }

    public override string Name => $"{_label} ({_left:0.0} с)";
    public override void Start(NpcAgent npc)
    {
        _left = _duration;
        npc.StopMoving();
    }

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        _left -= dt;
        return _left <= 0f ? ActionStatus.Succeeded : ActionStatus.Running;
    }
}

/// <summary>Повернуться к точке.</summary>
public sealed class FaceAction : NpcAction
{
    private readonly Func<Vector3> _point;
    public FaceAction(Func<Vector3> point) => _point = point;
    public override string Name => "Повернуться к цели";

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        var p = _point();
        npc.AimAt(p);
        return npc.IsFacing(p, 5f) ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    public override void Stop(NpcAgent npc) => npc.ClearAim();
}

/// <summary>
/// Выпустить несколько огненных шаров в цель: повернуться, дождаться перезарядки, выстрелить.
/// </summary>
public sealed class FireAction : NpcAction
{
    private readonly Func<Vector3> _target;
    private readonly int _shots;
    private int _fired;

    public FireAction(Func<Vector3> target, int shots = 1)
    {
        _target = target;
        _shots = shots;
    }

    /// <summary>Бросать огненный шар вместо выстрела.</summary>
    public bool UseFireball { get; init; }

    public override string Name => $"{(UseFireball ? "Огненный шар" : "Выстрел")} {_fired}/{_shots}";
    public override void Start(NpcAgent npc) => _fired = 0;

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        var target = _target();
        npc.AimAt(target);
        if (npc.IsFacing(target, 8f) && (UseFireball ? npc.ThrowFireball(target) is not null : npc.Fire(target)))
            _fired++;
        return _fired >= _shots ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    public override void Stop(NpcAgent npc) => npc.ClearAim();
}

/// <summary>
/// Стрейф: идти боком (влево или вправо относительно цели), не сводя с неё глаз,
/// и стрелять, когда оружие готово.
/// </summary>
public sealed class StrafeAction : NpcAction
{
    private readonly Func<Vector3> _target;
    private readonly float _duration;
    private readonly bool _left;
    private readonly bool _shoot;
    private float _time;

    public StrafeAction(Func<Vector3> target, bool left, float seconds, bool shoot = true)
    {
        _target = target;
        _left = left;
        _duration = seconds;
        _shoot = shoot;
    }

    public override string Name => _left ? "Стрейф влево с огнём" : "Стрейф вправо с огнём";
    public override void Start(NpcAgent npc) => _time = 0f;

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        _time += dt;
        var target = _target();
        var to = target - npc.GlobalPosition;
        to.Y = 0f;
        var side = to.Normalized().Cross(Vector3.Up); // вправо от направления на цель
        if (_left)
            side = -side;
        npc.AimAt(target);
        npc.MoveDirect(side, Gait.Run);
        if (_shoot && npc.IsFacing(target, 10f))
            npc.Fire(target);
        return _time >= _duration ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    public override void Stop(NpcAgent npc)
    {
        npc.StopMoving();
        npc.ClearAim();
    }
}

/// <summary>Последовательность действий; при <c>loop</c> повторяется бесконечно.</summary>
public sealed class SequenceAction : NpcAction
{
    private readonly List<NpcAction> _steps;
    private readonly bool _loop;
    private int _index;
    private bool _started;

    public SequenceAction(IEnumerable<NpcAction> steps, bool loop = false)
    {
        _steps = new List<NpcAction>(steps);
        _loop = loop;
    }

    /// <summary>Печатать начало каждого шага в Output (отладка).</summary>
    public bool LogSteps { get; init; }

    public NpcAction? Current => _index < _steps.Count ? _steps[_index] : null;
    public override string Name => Current?.Name ?? "—";
    /// <summary>Номер текущего шага (с 1) и общее количество — для подписи.</summary>
    public string Progress => $"{_index + 1}/{_steps.Count}";

    public override void Start(NpcAgent npc)
    {
        _index = 0;
        _started = false;
    }

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        while (_index < _steps.Count)
        {
            var step = _steps[_index];
            if (!_started)
            {
                step.Start(npc);
                if (LogSteps)
                    GD.Print($"[{Engine.GetPhysicsFrames() / (float)Engine.PhysicsTicksPerSecond:0.0} с] {npc.Name}: {step.Name}");
                _started = true;
            }
            var status = step.Tick(npc, dt);
            if (status == ActionStatus.Running)
                return ActionStatus.Running;
            step.Stop(npc);
            _started = false;
            if (status == ActionStatus.Failed)
                GD.Print($"{npc.Name}: шаг «{step.Name}» не удался, идём дальше");
            _index++;
            if (_index >= _steps.Count && _loop)
            {
                _index = 0;
                return ActionStatus.Running; // следующий круг — со следующего кадра
            }
        }
        return ActionStatus.Succeeded;
    }

    public override void Stop(NpcAgent npc)
    {
        if (_started)
            Current?.Stop(npc);
        _started = false;
    }
}
