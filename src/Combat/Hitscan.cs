using Godot;
using Zero.Actors;
using Zero.Ai.Stealth;

namespace Zero.Combat;

/// <summary>
/// Мгновенный выстрел (хитскан): луч от ствола, первое препятствие или персонаж получает урон.
/// Видимый след — короткая вспышка-трассер, в точке попадания — искра. Попадание слышно
/// рядом (как удар), выстрел — дальше (шум выстрела издаёт сам стрелок).
/// </summary>
public static class Hitscan
{
    public static (Vector3 Point, GodotObject? Collider) Shoot(ActorBody shooter, Vector3 from, Vector3 direction,
        float range, float damage)
    {
        var to = from + direction.Normalized() * range;
        var space = shooter.GetWorld3D().DirectSpaceState;
        using var q = PhysicsRayQueryParameters3D.Create(from, to);
        q.Exclude = new() { shooter.GetRid() };
        var hit = space.IntersectRay(q);

        var end = to;
        GodotObject? collider = null;
        if (hit.Count > 0)
        {
            end = (Vector3)hit["position"];
            collider = hit["collider"].AsGodotObject();
            if (collider is IDamageable target)
                target.TakeDamage(damage, shooter);
            NoiseSystem.Emit(new NoiseEvent(NoiseKind.Impact, end, 6f, shooter, end));
        }

        NotifyNearMisses(shooter, from, end, collider);

        var parent = shooter.GetTree().CurrentScene ?? shooter.GetParent();
        SpawnTracer(parent, from, end);
        if (hit.Count > 0)
            SpawnSpark(parent, end);
        return (end, collider);
    }

    /// <summary>Пули, пролетевшие в 1.5 м от врага стрелка, — «подавление»: цель знает, что по ней стреляют.</summary>
    private static void NotifyNearMisses(ActorBody shooter, Vector3 from, Vector3 to, GodotObject? hit)
    {
        foreach (var group in new[] { "player", "npc" })
            foreach (var node in shooter.GetTree().GetNodesInGroup(group))
            {
                if (node is not ActorBody a || a == hit || a.IsDead || a.Team == shooter.Team)
                    continue;
                var p = a.CenterMass;
                var seg = to - from;
                var t = Mathf.Clamp((p - from).Dot(seg) / Mathf.Max(seg.LengthSquared(), 0.0001f), 0f, 1f);
                if ((from + seg * t).DistanceTo(p) < 1.5f)
                    a.NotifyShotAt(shooter);
            }
    }

    private static void SpawnTracer(Node parent, Vector3 from, Vector3 to)
    {
        var len = from.DistanceTo(to);
        if (len < 0.05f)
            return;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(1f, 0.85f, 0.5f, 0.8f),
        };
        var tracer = new MeshInstance3D
        {
            Name = "Tracer",
            Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.025f, len), Material = mat },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(tracer);
        var mid = (from + to) / 2f;
        tracer.GlobalPosition = mid;
        if (!(to - from).Normalized().Cross(Vector3.Up).IsZeroApprox())
            tracer.LookAt(to, Vector3.Up);
        var tween = tracer.CreateTween();
        tween.TweenProperty(mat, "albedo_color:a", 0f, 0.09);
        tween.TweenCallback(Callable.From(tracer.QueueFree));
    }

    private static void SpawnSpark(Node parent, Vector3 at)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(1f, 0.7f, 0.3f, 1f),
        };
        var spark = new MeshInstance3D
        {
            Name = "Spark",
            Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f, Material = mat, RadialSegments = 8, Rings = 4 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(spark);
        spark.GlobalPosition = at;
        var tween = spark.CreateTween().SetParallel();
        tween.TweenProperty(spark, "scale", Vector3.One * 2.5f, 0.12);
        tween.TweenProperty(mat, "albedo_color:a", 0f, 0.12);
        tween.Chain().TweenCallback(Callable.From(spark.QueueFree));
    }
}
