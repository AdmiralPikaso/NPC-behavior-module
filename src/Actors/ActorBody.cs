using System;
using Godot;
using Zero.Ai.Stealth;
using Zero.Combat;
using Zero.Common.Animation;

namespace Zero.Actors;

/// <summary>Темп передвижения.</summary>
public enum Gait { Walk, Run, Sprint }

/// <summary>
/// Общий слой атомарных действий персонажа. Им пользуются и NPC, и игрок:
/// источник решений (ИИ или ввод) только выставляет намерения, а тело их исполняет
/// по одним и тем же правилам (скорости, присед, прыжок, поворот, стрельба, здоровье).
///
/// Атомарные действия:
///   <see cref="Move"/> / <see cref="StopMoving"/> — идти в мировом направлении шагом, бегом или спринтом;
///   <see cref="SetCrouching"/> — присесть / встать (капсула уменьшается);
///   <see cref="Jump"/> — прыжок;
///   <see cref="AimAt"/> / <see cref="ClearAim"/> — смотреть на точку (идти тогда можно боком и спиной);
///   <see cref="Fire"/> — выпустить огненный шар;
///   <see cref="TakeDamage"/> — получить урон (смерть при нуле здоровья).
///
/// Тело (CharacterBody3D) не вращается, поворачивается только узел Visual с моделью.
/// </summary>
[GlobalClass]
public partial class ActorBody : CharacterBody3D, IDamageable
{
    [ExportGroup("Скорость, м/с")]
    [Export] public float WalkSpeed { get; set; } = 1.6f;
    [Export] public float RunSpeed { get; set; } = 3.8f;
    [Export] public float SprintSpeed { get; set; } = 5.8f;
    [Export] public float CrouchSpeed { get; set; } = 1.1f;
    /// <summary>Предел скорости боком и спиной, когда персонаж целится.</summary>
    [Export] public float StrafeSpeed { get; set; } = 2.2f;
    [Export] public float Acceleration { get; set; } = 12f;
    [Export] public float AirControl { get; set; } = 0.3f;
    [Export] public float TurnSpeed { get; set; } = 10f;
    [Export] public float JumpVelocity { get; set; } = 4.6f;

    [ExportGroup("Присед")]
    [Export] public float StandHeight { get; set; } = 1.8f;
    [Export] public float CrouchHeight { get; set; } = 1.2f;

    [ExportGroup("Бой")]
    [Export] public int Team { get; set; }
    [Export] public float MaxHealth { get; set; } = 100f;
    [Export] public float FireCooldown { get; set; } = 0.8f;
    /// <summary>Урон и дальность основного выстрела (хитскан).</summary>
    [Export] public float ShotDamage { get; set; } = 20f;
    [Export] public float ShotRange { get; set; } = 60f;
    /// <summary>Перезарядка броска огненного шара (отдельное действие).</summary>
    [Export] public float FireballCooldown { get; set; } = 2.5f;
    [Export] public float FireballSpeed { get; set; } = 16f;
    [Export] public float FireballDamage { get; set; } = 20f;
    [Export] public float ShotNoiseRadius { get; set; } = 14f;
    /// <summary>Радиус попадания шара (0 — точный луч). Переопределяет игрок.</summary>
    protected virtual float FireballHitRadius => 0f;

    /// <summary>Выстрел (хитскан или огненный шар).</summary>
    public event Action<ActorBody>? Fired;
    /// <summary>Получен урон: сколько и от кого.</summary>
    public event Action<ActorBody, float, Node?>? Damaged;
    public event Action<ActorBody>? Died;
    /// <summary>Рядом пролетел выстрел врага (подавление).</summary>
    public event Action<ActorBody, ActorBody>? ShotAt;
    public void NotifyShotAt(ActorBody shooter) => ShotAt?.Invoke(this, shooter);

    public float Health { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsCrouching { get; private set; }
    public Gait CurrentGait { get; private set; } = Gait.Walk;
    /// <summary>Желаемое направление движения в мире (длина 0..1).</summary>
    public Vector3 MoveIntent { get; private set; }
    /// <summary>Точка, на которую персонаж смотрит, если задана.</summary>
    public Vector3? AimPoint { get; private set; }
    public bool IsAirborne => !IsOnFloor();
    public float HorizontalSpeed => new Vector2(Velocity.X, Velocity.Z).Length();
    public float FireCooldownLeft => _fireCooldownLeft;
    public bool CanFire => !IsDead && _fireCooldownLeft <= 0f && !IsReloading && (MagazineSize <= 0 || Ammo > 0);

    /// <summary>Магазин основного выстрела; 0 — бесконечный (по умолчанию, так у NPC и бота).</summary>
    public int MagazineSize
    {
        get => _magazineSize;
        set
        {
            _magazineSize = Mathf.Max(0, value);
            Ammo = _magazineSize;
            _reloadLeft = 0f;
        }
    }
    public float ReloadTime { get; set; } = 1.6f;
    public int Ammo { get; private set; }
    public bool IsReloading => _reloadLeft > 0f;
    public float ReloadLeft => Mathf.Max(0f, _reloadLeft);
    private int _magazineSize;
    private float _reloadLeft;

    /// <summary>Сменить размер магазина, сохранив патроны (сколько было, но не больше нового размера).</summary>
    public void ResizeMagazine(int size, int ammo)
    {
        _magazineSize = Mathf.Max(0, size);
        Ammo = Mathf.Clamp(ammo, 0, _magazineSize);
        _reloadLeft = 0f;
        if (_magazineSize > 0 && Ammo == 0)
            Reload();
    }

    /// <summary>Начать перезарядку (если есть магазин и он не полон).</summary>
    public bool Reload()
    {
        if (IsDead || MagazineSize <= 0 || IsReloading || Ammo >= MagazineSize)
            return false;
        _reloadLeft = ReloadTime;
        return true;
    }

    /// <summary>Куда смотрит модель (горизонтально, единичный вектор).</summary>
    public Vector3 Facing
    {
        get
        {
            var z = _visual.GlobalBasis.Z;
            z.Y = 0f;
            return z.LengthSquared() > 0.0001f ? z.Normalized() : Vector3.Back;
        }
    }

    /// <summary>Высота глаз — отсюда проверяется видимость.</summary>
    public Vector3 EyePosition => GlobalPosition + Vector3.Up * (IsCrouching ? 1.05f : 1.6f);
    /// <summary>Откуда вылетает огненный шар (у правого плеча, чуть впереди).</summary>
    public Vector3 MuzzlePosition =>
        GlobalPosition + Vector3.Up * (IsCrouching ? 0.95f : 1.4f) + Facing * 0.55f + Facing.Cross(Vector3.Up) * 0.2f;

    /// <summary>Куда целиться в этого персонажа.</summary>
    public Vector3 CenterMass => GlobalPosition + Vector3.Up * (IsCrouching ? 0.7f : 1.1f);

    protected Node3D Visual => _visual;
    protected LocomotionDriver? Locomotion => _locomotion;

    private Node3D _visual = null!;
    private LocomotionDriver? _locomotion;
    private CollisionShape3D _collider = null!;
    private CapsuleShape3D _capsule = null!;
    private float _fireCooldownLeft;
    private float _airTime;
    private bool _wasOnFloor = true;

    public override void _Ready()
    {
        _visual = GetNode<Node3D>("Visual");
        _locomotion = GetNodeOrNull<LocomotionDriver>("LocomotionDriver");
        _collider = GetNode<CollisionShape3D>("CollisionShape3D");
        // своя копия формы, чтобы присед не менял капсулу у других экземпляров сцены
        _capsule = (CapsuleShape3D)_collider.Shape.Duplicate();
        _collider.Shape = _capsule;
        ApplyHeight(StandHeight);
        Health = MaxHealth;
    }

    // ───────────── атомарные действия ─────────────

    /// <summary>Идти в мировом направлении (вертикаль отбрасывается).</summary>
    public void Move(Vector3 worldDirection, Gait gait = Gait.Walk)
    {
        worldDirection.Y = 0f;
        MoveIntent = worldDirection.LengthSquared() > 1f ? worldDirection.Normalized() : worldDirection;
        CurrentGait = gait;
    }

    public void StopMoving() => MoveIntent = Vector3.Zero;

    /// <summary>Присесть или встать. Встать нельзя, если над головой препятствие.</summary>
    public bool SetCrouching(bool crouch)
    {
        if (IsDead || crouch == IsCrouching)
            return crouch == IsCrouching;
        if (!crouch && !CanStandUp())
            return false;
        IsCrouching = crouch;
        ApplyHeight(crouch ? CrouchHeight : StandHeight);
        return true;
    }

    /// <summary>Прыгнуть, если стоит на земле. Из приседа сначала встаёт.</summary>
    public bool Jump()
    {
        if (IsDead || !IsOnFloor())
            return false;
        if (IsCrouching && !SetCrouching(false))
            return false;
        var v = Velocity;
        v.Y = JumpVelocity;
        Velocity = v;
        _locomotion?.PlayJump();
        return true;
    }

    public void AimAt(Vector3 point) => AimPoint = point;
    public void ClearAim() => AimPoint = null;

    /// <summary>Смотрит ли модель на точку с точностью до угла (градусы).</summary>
    public bool IsFacing(Vector3 point, float toleranceDegrees = 10f)
    {
        var to = point - GlobalPosition;
        to.Y = 0f;
        if (to.LengthSquared() < 0.0001f)
            return true;
        return Mathf.RadToDeg(Facing.AngleTo(to.Normalized())) <= toleranceDegrees;
    }

    /// <summary>
    /// Основной выстрел — хитскан: мгновенный луч из ствола в точку. false, если оружие перезаряжается.
    /// Поворот к цели — забота вызывающего (см. <see cref="AimAt"/>).
    /// </summary>
    public bool Fire(Vector3 targetPoint)
    {
        if (!CanFire)
            return false;
        var muzzle = MuzzlePosition;
        var dir = targetPoint - muzzle;
        if (dir.LengthSquared() < 0.01f)
            dir = Facing;
        _fireCooldownLeft = FireCooldown;
        NoiseSystem.Emit(new NoiseEvent(NoiseKind.Shot, muzzle, ShotNoiseRadius, this, muzzle));
        Hitscan.Shoot(this, muzzle, dir, ShotRange, ShotDamage);
        if (MagazineSize > 0 && --Ammo <= 0)
            Reload(); // магазин пуст — перезарядка сама
        Fired?.Invoke(this);
        return true;
    }

    public bool CanThrowFireball => !IsDead && _fireballCooldownLeft <= 0f;
    private float _fireballCooldownLeft;

    /// <summary>
    /// Бросить огненный шар (медленный снаряд, громкий взрыв — удобно отвлечь охрану).
    /// </summary>
    public Fireball? ThrowFireball(Vector3 targetPoint)
    {
        if (!CanThrowFireball)
            return null;
        var muzzle = MuzzlePosition;
        var dir = targetPoint - muzzle;
        if (dir.LengthSquared() < 0.01f)
            dir = Facing;
        var ball = Fireball.Spawn(GetTree().CurrentScene ?? GetParent(), muzzle, dir.Normalized(),
            FireballSpeed, FireballDamage, this);
        ball.HitRadius = FireballHitRadius;
        _fireballCooldownLeft = FireballCooldown;
        Fired?.Invoke(this);
        return ball;
    }

    /// <summary>
    /// Неуязвимость (режим наблюдателя на арене): попадания засчитываются и событие <see cref="Damaged"/>
    /// приходит, но здоровье не уменьшается. По умолчанию выключено.
    /// </summary>
    public bool Invulnerable { get; set; }

    /// <summary>Восстановить здоровье (не выше максимума). Мёртвого не воскрешает.</summary>
    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f)
            return;
        Health = Mathf.Min(MaxHealth, Health + amount);
    }

    public void TakeDamage(float amount, Node? source)
    {
        if (IsDead || amount <= 0f)
            return;
        if (!Invulnerable)
            Health = Mathf.Max(0f, Health - amount);
        Damaged?.Invoke(this, amount, source);
        if (Health <= 0f)
            Die();
    }

    /// <summary>Вернуть к жизни с полным здоровьем (для повторных прогонов).</summary>
    public void Revive()
    {
        IsDead = false;
        Health = MaxHealth;
        Ammo = MagazineSize;
        _reloadLeft = 0f;
        _visual.Rotation = new Vector3(0f, _visual.Rotation.Y, 0f);
        _visual.Position = Vector3.Zero;
        _collider.Disabled = false;
    }

    // ───────────── исполнение ─────────────

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        BeforeMove(dt);

        _fireCooldownLeft = Mathf.Max(0f, _fireCooldownLeft - dt);
        if (_reloadLeft > 0f)
        {
            _reloadLeft -= dt;
            if (_reloadLeft <= 0f)
                Ammo = MagazineSize;
        }
        _fireballCooldownLeft = Mathf.Max(0f, _fireballCooldownLeft - dt);
        var velocity = Velocity;
        if (!IsOnFloor())
            velocity += GetGravity() * dt;

        var target = IsDead ? Vector3.Zero : MoveIntent * MaxSpeedFor(MoveIntent);
        var horizontal = new Vector3(velocity.X, 0f, velocity.Z);
        var accel = Acceleration * (IsOnFloor() ? 1f : AirControl);
        horizontal = horizontal.MoveToward(target, accel * dt);
        velocity.X = horizontal.X;
        velocity.Z = horizontal.Z;
        Velocity = velocity;
        MoveAndSlide();

        UpdateFacing(dt);
        UpdateAirState(dt);

        var local = Vector2.Zero;
        if (!IsDead)
        {
            var f = Facing;
            var right = f.Cross(Vector3.Up);
            var v = new Vector3(Velocity.X, 0f, Velocity.Z);
            local = new Vector2(v.Dot(right), v.Dot(f));
        }
        _locomotion?.SetMotion(local, IsCrouching, airborne: _airTime > 0.15f);
        EmitFootsteps(dt);

        AfterMove(dt);
    }

    // ───────────── шум и тихое устранение (стелс) ─────────────

    /// <summary>Радиус слышимости шагов, м: присед / шаг / бег / спринт.</summary>
    public float FootstepRadius => !IsOnFloor() || HorizontalSpeed < 0.3f ? 0f
        : IsCrouching ? 1.5f
        : HorizontalSpeed <= WalkSpeed + 0.2f ? 4f
        : HorizontalSpeed <= RunSpeed + 0.2f ? 9f
        : 14f;

    private float _stepTimer;

    private void EmitFootsteps(float dt)
    {
        var r = IsDead ? 0f : FootstepRadius;
        if (r <= 0f)
            return;
        _stepTimer -= dt;
        if (_stepTimer > 0f)
            return;
        _stepTimer = 0.45f;
        NoiseSystem.Emit(new NoiseEvent(NoiseKind.Footstep, GlobalPosition, r, this, GlobalPosition));
    }

    /// <summary>Можно ли тихо устранить этого персонажа (переопределяет NPC: только неготового или со спины).</summary>
    public virtual bool CanBeTakenDownBy(ActorBody attacker) => false;

    public event Action<ActorBody, ActorBody>? TookDown;

    /// <summary>
    /// Атомарное действие стелса: тихое устранение врага вплотную (до 1.7 м, перед собой).
    /// Почти бесшумно: звук удара слышно в радиусе 3 м.
    /// </summary>
    public ActorBody? TryTakedown()
    {
        if (IsDead)
            return null;
        ActorBody? best = null;
        var bestD = 1.7f;
        foreach (var node in GetTree().GetNodesInGroup("npc"))
        {
            if (node is not ActorBody t || t == this || t.IsDead || t.Team == Team)
                continue;
            var to = t.GlobalPosition - GlobalPosition;
            to.Y = 0f;
            var d = to.Length();
            if (d > bestD || (d > 0.2f && Mathf.RadToDeg(Facing.AngleTo(to / d)) > 70f) || !t.CanBeTakenDownBy(this))
                continue;
            best = t;
            bestD = d;
        }
        if (best is null)
            return null;
        best.TakeDamage(best.MaxHealth * 10f, this);
        NoiseSystem.Emit(new NoiseEvent(NoiseKind.Impact, best.GlobalPosition, 3f, this, best.GlobalPosition));
        TookDown?.Invoke(this, best);
        return best;
    }

    /// <summary>Хук для наследников: принять решения до движения.</summary>
    protected virtual void BeforeMove(float dt) { }

    /// <summary>Хук для наследников: после движения (камера, подписи).</summary>
    protected virtual void AfterMove(float dt) { }

    private float MaxSpeedFor(Vector3 intent)
    {
        var speed = IsCrouching ? CrouchSpeed : CurrentGait switch
        {
            Gait.Run => RunSpeed,
            Gait.Sprint => SprintSpeed,
            _ => WalkSpeed,
        };
        // целясь, быстро можно бежать только вперёд; боком и спиной — не быстрее StrafeSpeed
        if (AimPoint is not null && intent.LengthSquared() > 0.0001f)
        {
            var forwardness = Facing.Dot(intent.Normalized());
            if (forwardness < 0.7f)
                speed = Mathf.Min(speed, IsCrouching ? CrouchSpeed : StrafeSpeed);
        }
        return speed;
    }

    private void UpdateFacing(float dt)
    {
        if (IsDead)
            return;
        Vector3 dir;
        if (AimPoint is { } aim)
            dir = aim - GlobalPosition;
        else
            dir = MoveIntent;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f)
            return;
        // модель Synty смотрит вдоль +Z, поэтому угол считается от оси Z
        var targetYaw = Mathf.Atan2(dir.X, dir.Z);
        var rotation = _visual.GlobalRotation;
        rotation.Y = Mathf.LerpAngle(rotation.Y, targetYaw, 1f - Mathf.Exp(-TurnSpeed * dt));
        _visual.GlobalRotation = rotation;
    }

    private void UpdateAirState(float dt)
    {
        var onFloor = IsOnFloor();
        if (onFloor)
        {
            if (!_wasOnFloor && _airTime > 0.3f)
                _locomotion?.PlayLand();
            _airTime = 0f;
        }
        else
        {
            _airTime += dt;
        }
        _wasOnFloor = onFloor;
    }

    private void Die()
    {
        IsDead = true;
        MoveIntent = Vector3.Zero;
        AimPoint = null;
        // анимации смерти в паке нет — модель просто заваливается на бок
        _visual.Rotation = new Vector3(0f, _visual.Rotation.Y, Mathf.Pi / 2f);
        _visual.Position = new Vector3(0f, 0.3f, 0f);
        _collider.Disabled = true;
        Died?.Invoke(this);
    }

    private void ApplyHeight(float height)
    {
        _capsule.Height = height;
        _collider.Position = new Vector3(0f, height / 2f, 0f);
    }

    /// <summary>Есть ли над головой место, чтобы встать.</summary>
    private bool CanStandUp()
    {
        var probe = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = _capsule.Radius * 0.9f, Height = StandHeight },
            Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * (StandHeight / 2f + 0.05f)),
            CollisionMask = CollisionMask,
            Exclude = new() { GetRid() },
        };
        return GetWorld3D().DirectSpaceState.IntersectShape(probe, 1).Count == 0;
    }
}
