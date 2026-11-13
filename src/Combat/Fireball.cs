using Godot;

namespace Zero.Combat;

/// <summary>
/// Огненный шар — «формальное» оружие. Летит по прямой с постоянной скоростью,
/// попадание проверяется лучом между положениями соседних кадров (не пролетает сквозь
/// тонкие стены на большой скорости). При попадании в <see cref="IDamageable"/> наносит урон.
/// Весь вид собирается в коде, отдельной сцены не нужно.
/// </summary>
public partial class Fireball : Node3D
{
    public Vector3 Direction { get; private set; }
    public float Speed { get; private set; }
    public float Damage { get; private set; }
    public Node3D? Shooter { get; private set; }
    public float Lifetime { get; set; } = 3f;
    /// <summary>Радиус «попадания» (0 — тонкий луч). Игроку-человеку даётся помощь в прицеливании.</summary>
    public float HitRadius { get; set; }

    private Rid _exclude;
    private OmniLight3D _light = null!;

    public static Fireball Spawn(Node parent, Vector3 from, Vector3 direction, float speed, float damage,
        CollisionObject3D? shooter)
    {
        var ball = new Fireball
        {
            Name = "Fireball",
            Direction = direction.Normalized(),
            Speed = speed,
            Damage = damage,
            Shooter = shooter,
            _exclude = shooter?.GetRid() ?? default,
        };
        parent.AddChild(ball);
        ball.GlobalPosition = from;
        return ball;
    }

    public override void _Ready()
    {
        AddToGroup("fireball");
        var core = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.75f, 0.3f),
        };
        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.14f, Height = 0.28f, Material = core },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        var trailMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            VertexColorUseAsAlbedo = true,
        };
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 0.6f, 0.15f, 0.9f));
        ramp.SetColor(1, new Color(0.6f, 0.1f, 0.02f, 0f));
        AddChild(new CpuParticles3D
        {
            Amount = 40,
            Lifetime = 0.35,
            LocalCoords = false,
            Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f, Material = trailMat, RadialSegments = 8, Rings = 4 },
            Gravity = new Vector3(0f, 0.6f, 0f),
            InitialVelocityMin = 0.1f,
            InitialVelocityMax = 0.4f,
            Spread = 180f,
            ScaleAmountMin = 0.6f,
            ScaleAmountMax = 1.2f,
            ColorRamp = ramp,
        });

        _light = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 2.5f, OmniRange = 4f };
        AddChild(_light);
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        Lifetime -= dt;
        if (Lifetime <= 0f)
        {
            QueueFree();
            return;
        }

        var from = GlobalPosition;
        var to = from + Direction * Speed * dt;
        if (HitRadius > 0f && SphereHit(from, to) is { } sphereHit)
        {
            if (sphereHit.Collider is IDamageable t)
                t.TakeDamage(Damage, Shooter);
            Explode(sphereHit.Point);
            return;
        }
        using var query = PhysicsRayQueryParameters3D.Create(from, to);
        if (_exclude.IsValid)
            query.Exclude = new() { _exclude };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0)
        {
            var point = (Vector3)hit["position"];
            var collider = hit["collider"].AsGodotObject();
            if (collider is IDamageable target)
                target.TakeDamage(Damage, Shooter);
            Explode(point);
            return;
        }
        GlobalPosition = to;
    }

    /// <summary>Сфера радиуса HitRadius, протянутая от from до to: первое касание персонажа.</summary>
    private (Vector3 Point, GodotObject Collider)? SphereHit(Vector3 from, Vector3 to)
    {
        var space = GetWorld3D().DirectSpaceState;
        using var shape = new SphereShape3D { Radius = HitRadius };
        using var q = new PhysicsShapeQueryParameters3D
        {
            Shape = shape,
            Transform = new Transform3D(Basis.Identity, from),
            Motion = to - from,
            CollisionMask = 2, // только персонажи; стены — тонким лучом, чтобы шар не цеплял углы
        };
        if (_exclude.IsValid)
            q.Exclude = new() { _exclude };
        var cast = space.CastMotion(q);
        if (cast.Length < 2 || cast[1] >= 1f)
            return null;
        var at = from + (to - from) * cast[1];
        q.Transform = new Transform3D(Basis.Identity, at);
        q.Motion = Vector3.Zero;
        var info = space.GetRestInfo(q);
        if (info.Count == 0)
            return null;
        var id = (ulong)info["collider_id"];
        var obj = GodotObject.InstanceFromId(id);
        return obj is null ? null : (at, obj);
    }

    private void Explode(Vector3 point)
    {
        // взрыв слышно: стелс-NPC идут проверить место
        Zero.Ai.Stealth.NoiseSystem.Emit(new Zero.Ai.Stealth.NoiseEvent(Zero.Ai.Stealth.NoiseKind.Impact, point, 10f,
            Shooter as Zero.Actors.ActorBody, point));
        var flash = new Node3D { Name = "FireballFlash" };
        GetParent().AddChild(flash);
        flash.GlobalPosition = point;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(1f, 0.55f, 0.15f, 0.9f),
        };
        var sphere = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, Material = mat },
            Scale = Vector3.One * 0.3f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        var light = new OmniLight3D { LightColor = new Color(1f, 0.5f, 0.2f), LightEnergy = 5f, OmniRange = 5f };
        flash.AddChild(sphere);
        flash.AddChild(light);

        var tween = flash.CreateTween().SetParallel();
        tween.TweenProperty(sphere, "scale", Vector3.One * 1.4f, 0.25);
        tween.TweenProperty(mat, "albedo_color:a", 0f, 0.25);
        tween.TweenProperty(light, "light_energy", 0f, 0.3);
        tween.Chain().TweenCallback(Callable.From(flash.QueueFree));

        QueueFree();
    }
}
