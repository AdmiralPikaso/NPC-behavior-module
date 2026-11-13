using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Actors;
using Zero.Ai.Tactics;

namespace Zero.Player;

/// <summary>Источник решений для игрока вместо ввода (бот для автоматических прогонов).</summary>
public interface IPlayerBrain
{
    void Tick(PlayerController player, float dt);
}

/// <summary>
/// Бот-игрок для сравнения тактик. Ведёт себя как средний живой игрок в шутере:
/// выбирает позицию, откуда видно цель и которая укрыта от остальных врагов,
/// при попаданиях по себе меняет позицию, стоя на месте — уклоняется шагами в стороны,
/// стреляет в ближайшего видимого врага с разбросом. Использует те же атомарные действия ActorBody.
/// </summary>
public sealed class PlayerBot : IPlayerBrain
{
    /// <summary>Меткость бота относительно модели врагов (живой игрок обычно точнее ИИ).</summary>
    public float HumanAccuracy { get; init; } = 1.25f;
    /// <summary>Во сколько раз быстрее бот «пристреливается».</summary>
    public float HumanSettleSpeed { get; init; } = 2f;
    private ActorBody? _aimTarget;
    private float _aimTime;
    /// <summary>
    /// Оборонительный стиль: держаться в радиусе <see cref="HoldRadius"/> от точки старта
    /// и ценить укрытие от всех врагов выше, чем обзор (игрок-«кемпер»).
    /// </summary>
    public bool Defensive { get; init; }
    public float HoldRadius { get; init; } = 6f;
    private Vector3? _anchor;

    /// <summary>
    /// Скрытный стиль (стелс-режим арены): пока тревоги нет, бот подкрадывается в приседе
    /// к ближайшему врагу со спины, пережидает, когда на него смотрят, и устраняет тихо.
    /// После тревоги — обычный бой.
    /// </summary>
    public bool Stealthy { get; init; }
    /// <summary>Поднята ли тревога (задаёт арена).</summary>
    public Func<bool> AlarmRaised { get; init; } = () => true;
    private bool _wasStealth;

    private readonly TacticalWorld _world;
    private readonly Func<IEnumerable<ActorBody>> _enemies;
    private readonly Random _rng;
    private ActorBody? _target;
    private Vector3? _dest;
    private Vector3[] _path = Array.Empty<Vector3>();
    private int _pathIndex;
    private float _replan;
    private float _retarget;
    private float _strafeLeft;
    private int _strafeDir;
    private readonly Queue<float> _hits = new();
    private float _time;

    public PlayerBot(TacticalWorld world, Func<IEnumerable<ActorBody>> enemies, int seed)
    {
        _world = world;
        _enemies = enemies;
        _rng = new Random(seed);
    }

    public void OnDamaged() => _hits.Enqueue(_time);

    /// <summary>
    /// Рядом пролетел выстрел (подавление): как живой игрок, бот вздрагивает (точность ×0.7 на секунду),
    /// а после трёх близких промахов за 2 с меняет позицию.
    /// </summary>
    public void OnShotAt()
    {
        _nearMisses.Enqueue(_time);
        _lastNearMiss = _time;
    }

    private readonly Queue<float> _nearMisses = new();
    private float _lastNearMiss = -99f;

    public void Tick(PlayerController p, float dt)
    {
        _time += dt;
        while (_hits.Count > 0 && _time - _hits.Peek() > 2f)
            _hits.Dequeue();
        while (_nearMisses.Count > 0 && _time - _nearMisses.Peek() > 2f)
            _nearMisses.Dequeue();

        var enemies = _enemies().Where(e => !e.IsDead).ToList();
        if (enemies.Count == 0)
        {
            p.StopMoving();
            p.ClearAim();
            return;
        }

        if (Stealthy && !AlarmRaised())
        {
            _wasStealth = true;
            StealthTick(p, enemies, dt);
            return;
        }
        if (_wasStealth)
        {
            _wasStealth = false;
            p.SetCrouching(false);
            _dest = null; // после тревоги — выбрать боевую позицию заново
        }

        _retarget -= dt;
        if (_target is null || _target.IsDead || _retarget <= 0f)
        {
            _retarget = 0.3f;
            var visible = enemies.Where(e => CanHit(p, e)).ToList();
            _target = (visible.Count > 0 ? visible : enemies)
                .OrderBy(e => Squad.FlatDistance(e.GlobalPosition, p.GlobalPosition)).First();
        }

        _replan -= dt;
        if (_dest is null || _replan <= 0f || _hits.Count >= 2 || _nearMisses.Count >= 3)
        {
            _hits.Clear();
            _nearMisses.Clear();
            _replan = 4f;
            ChoosePosition(p, enemies);
        }

        var moving = FollowPath(p);
        if (!moving)
            Dodge(p, dt);

        var canHit = CanHit(p, _target);
        if (_target != _aimTarget)
        {
            _aimTarget = _target;
            _aimTime = 0f;
        }
        if (canHit)
        {
            _aimTime += dt;
            p.AimAt(_target.CenterMass);
            if (p.CanFire && p.IsFacing(_target.GlobalPosition, 12f))
            {
                // та же модель промаха, что у врагов (без предупредительного выстрела): бот
                // изображает живого игрока, который под огнём попадает далеко не всегда
                var odds = AccuracyModel.Evaluate(_world, p, _target, _aimTime * HumanSettleSpeed, moving);
                var hit = _rng.NextDouble() < Mathf.Min(0.95f, odds.Chance * HumanAccuracy * (_time - _lastNearMiss < 1f ? 0.7f : 1f));
                p.Fire(AccuracyModel.AimPoint(_rng, p, _target, hit, _world.VisiblePart(p.MuzzlePosition, _target, p.GetRid())));
            }
        }
        else
        {
            p.ClearAim();
        }
    }

    private bool CanHit(PlayerController p, ActorBody e) =>
        _world.VisiblePart(p.MuzzlePosition, e, p.GetRid()) is not null; // как у врагов: любая видимая часть тела

    private void ChoosePosition(PlayerController p, List<ActorBody> enemies)
    {
        var target = _target!;
        _anchor ??= p.GlobalPosition;
        var others = enemies.Where(e => e != target).ToList();
        var best = float.MinValue;
        Vector3? bestPoint = null;
        foreach (var c in TacticalQuery.Candidates(_world, p.GlobalPosition))
        {
            var travel = Squad.FlatDistance(c, p.GlobalPosition);
            if (travel > 10f || (Defensive && Squad.FlatDistance(c, _anchor.Value) > HoldRadius))
                continue;
            var dist = Squad.FlatDistance(c, target.GlobalPosition);
            var s = 0f;
            s += 1.5f * (1f - travel / 10f);
            s += dist is >= 6f and <= 13f ? 1f : 0f;
            s += 3f * (_world.CanSeeFrom(c, target.CenterMass) ? 1f : 0f);
            if (others.Count > 0)
                s += (Defensive ? 3f : 1.5f) * others.Count(e => _world.IsCoveredFrom(c, e.EyePosition)) / others.Count;
            s += (float)_rng.NextDouble() * 0.3f; // немного непредсказуемости
            if (s > best)
            {
                best = s;
                bestPoint = c;
            }
        }
        _dest = bestPoint ?? p.GlobalPosition;
        var map = p.GetWorld3D().NavigationMap;
        _path = NavigationServer3D.MapGetPath(map, p.GlobalPosition + Vector3.Up * 0.5f, _dest.Value + Vector3.Up * 0.5f, true);
        _pathIndex = 0;
    }

    private bool FollowPath(PlayerController p, Gait gait = Gait.Run)
    {
        while (_pathIndex < _path.Length && Squad.FlatDistance(_path[_pathIndex], p.GlobalPosition) < 0.5f)
            _pathIndex++;
        if (_pathIndex >= _path.Length)
            return false;
        var dir = _path[_pathIndex] - p.GlobalPosition;
        dir.Y = 0f;
        p.Move(dir.Normalized(), gait);
        return true;
    }

    private void Dodge(PlayerController p, float dt)
    {
        _strafeLeft -= dt;
        if (_strafeLeft <= 0f)
        {
            _strafeLeft = 0.5f + (float)_rng.NextDouble() * 0.8f;
            _strafeDir = _rng.Next(-1, 2);
        }
        if (_strafeDir == 0 || _target is null)
        {
            p.StopMoving();
            return;
        }
        var to = _target.GlobalPosition - p.GlobalPosition;
        to.Y = 0f;
        var side = to.Normalized().Cross(Vector3.Up) * _strafeDir;
        // не уходить далеко от выбранной точки
        if (_dest is { } d && Squad.FlatDistance(p.GlobalPosition + side, d) > 2f)
            side = -side;
        p.Move(side, Gait.Run);
    }

    // ───────────── скрытный подход ─────────────

    private float _stealthReplan;

    private void StealthTick(PlayerController p, List<ActorBody> enemies, float dt)
    {
        p.SetCrouching(true);
        var target = enemies.OrderBy(e => Squad.FlatDistance(e.GlobalPosition, p.GlobalPosition)).First();
        _target = target;
        var dist = Squad.FlatDistance(target.GlobalPosition, p.GlobalPosition);

        // вплотную — устранить
        if (dist < 1.6f)
        {
            p.StopMoving();
            p.AimAt(target.CenterMass);
            if (p.IsFacing(target.GlobalPosition, 40f))
                p.TryTakedown();
            return;
        }
        p.ClearAim();

        // кто-то смотрит в мою сторону и видит меня — замереть (а если светло и близко — отойти в укрытие)
        var watched = enemies.Any(e => Watches(e, p));
        _stealthReplan -= dt;
        if (watched)
        {
            if (_stealthReplan <= 0f)
            {
                _stealthReplan = 1f;
                var hide = TacticalQuery.Candidates(_world, p.GlobalPosition)
                    .Where(c => Squad.FlatDistance(c, p.GlobalPosition) < 6f)
                    .Where(c => enemies.All(e => _world.IsCoveredFrom(c, e.EyePosition)))
                    .OrderBy(c => Squad.FlatDistance(c, p.GlobalPosition))
                    .Cast<Vector3?>().FirstOrDefault();
                SetPath(p, hide ?? p.GlobalPosition);
            }
            if (!FollowPath(p, Gait.Walk))
                p.StopMoving();
            return;
        }

        // подходить к точке за спиной цели
        if (_stealthReplan <= 0f)
        {
            _stealthReplan = 0.5f;
            var behind = target.GlobalPosition - target.Facing * 1.1f;
            SetPath(p, _world.SnapToNav(behind));
        }
        if (!FollowPath(p, Gait.Walk))
            p.StopMoving();
    }

    /// <summary>Враг смотрит на бота и видит его (грубая оценка самим ботом — как игрок «чувствует» взгляд).</summary>
    private bool Watches(ActorBody e, PlayerController p)
    {
        var to = p.GlobalPosition - e.GlobalPosition;
        to.Y = 0f;
        var d = to.Length();
        if (d > 16f || d < 0.1f)
            return false;
        if (Mathf.RadToDeg(e.Facing.AngleTo(to / d)) > 70f)
            return false;
        return _world.ClearLine(e.EyePosition, p.CenterMass);
    }

    private void SetPath(PlayerController p, Vector3 dest)
    {
        _dest = dest;
        var map = p.GetWorld3D().NavigationMap;
        _path = NavigationServer3D.MapGetPath(map, p.GlobalPosition + Vector3.Up * 0.5f, dest + Vector3.Up * 0.5f, true);
        _pathIndex = 0;
    }
}
