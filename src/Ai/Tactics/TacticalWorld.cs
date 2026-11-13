using System.Collections.Generic;
using Godot;

namespace Zero.Ai.Tactics;

/// <summary>Точка укрытия у края препятствия.</summary>
public readonly record struct CoverPoint(Vector3 Position, Vector3 Normal, float ObstacleHeight);

/// <summary>
/// Пространственные знания о мире для тактик: точки укрытий (генерируются автоматически
/// вокруг статических препятствий), проверки видимости лучами и привязка к навигационной сетке.
/// Аналог «сенсора укрытий» F.E.A.R. и мировых данных Tactical Point System Crysis.
/// </summary>
public sealed class TacticalWorld
{
    /// <summary>Слой статической геометрии (препятствия). Персонажи — на слое 2.</summary>
    public const uint WorldMask = 1;
    public const uint ActorMask = 2;

    public IReadOnlyList<CoverPoint> CoverPoints => _cover;

    private readonly List<CoverPoint> _cover = new();
    private readonly Node3D _owner;

    public TacticalWorld(Node3D owner) => _owner = owner;

    private PhysicsDirectSpaceState3D Space => _owner.GetWorld3D().DirectSpaceState;
    private Rid NavMap => _owner.GetWorld3D().NavigationMap;

    /// <summary>
    /// Собрать точки укрытий: вдоль каждой грани каждого статического бокса,
    /// на расстоянии <paramref name="offset"/> от грани, с шагом <paramref name="spacing"/>.
    /// </summary>
    public void BuildCoverPoints(Node root, float offset = 0.75f, float spacing = 1.2f, float minHeight = 0.9f)
    {
        _cover.Clear();
        foreach (var node in root.FindChildren("*", "CollisionShape3D", true, false))
        {
            var shapeNode = (CollisionShape3D)node;
            if (shapeNode.GetParent() is not StaticBody3D || shapeNode.Shape is not BoxShape3D box)
                continue;
            var xf = shapeNode.GlobalTransform;
            var half = box.Size / 2f;
            var height = half.Y * 2f * xf.Basis.Scale.Y;
            var top = (xf * new Vector3(0, half.Y, 0)).Y;
            if (height < minHeight || top < minHeight || half.X > 15f || half.Z > 15f)
                continue; // пол и слишком низкие предметы — не укрытие
            // четыре вертикальные грани
            foreach (var (axis, other) in new[] { (0, 2), (2, 0) })
            {
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var faceHalf = axis == 0 ? half.Z : half.X;
                    var steps = Mathf.Max(1, Mathf.FloorToInt(faceHalf * 2f / spacing));
                    for (var i = 0; i <= steps; i++)
                    {
                        var t = -faceHalf + faceHalf * 2f * i / steps;
                        var local = Vector3.Zero;
                        local[axis] = sign * (axis == 0 ? half.X : half.Z);
                        local[other] = t;
                        local.Y = -half.Y;
                        var onFace = xf * local;
                        var normal = (xf.Basis * (axis == 0 ? Vector3.Right : Vector3.Back) * sign).Normalized();
                        normal.Y = 0f;
                        var p = onFace + normal.Normalized() * offset;
                        p.Y = 0f;
                        if (IsOnNavmesh(p, 0.35f))
                            _cover.Add(new CoverPoint(p, normal.Normalized(), height));
                    }
                }
            }
        }
    }

    /// <summary>Есть ли прямая видимость между точками (только статические препятствия).</summary>
    public bool ClearLine(Vector3 from, Vector3 to)
    {
        using var q = PhysicsRayQueryParameters3D.Create(from, to, WorldMask);
        return Space.IntersectRay(q).Count == 0;
    }

    /// <summary>
    /// Можно ли выстрелить из <paramref name="from"/> в <paramref name="target"/>: луч по миру и персонажам
    /// первым должен упереться в цель (союзник на линии огня — нельзя).
    /// </summary>
    public bool CanHit(Vector3 from, CollisionObject3D target, Vector3 targetPoint, Rid self)
    {
        using var q = PhysicsRayQueryParameters3D.Create(from, targetPoint, WorldMask | ActorMask);
        q.Exclude = new() { self };
        var hit = Space.IntersectRay(q);
        return hit.Count > 0 && hit["collider"].AsGodotObject() == target;
    }

    /// <summary>Ближайшая точка навигационной сетки к <paramref name="p"/> (на уровне пола).</summary>
    public Vector3 SnapToNav(Vector3 p)
    {
        var s = NavigationServer3D.MapGetClosestPoint(NavMap, p + Vector3.Up * 0.5f);
        return new Vector3(s.X, p.Y, s.Z);
    }

    public bool IsOnNavmesh(Vector3 p, float tolerance = 0.3f)
    {
        var s = SnapToNav(p);
        return new Vector2(s.X - p.X, s.Z - p.Z).Length() <= tolerance;
    }

    /// <summary>
    /// Укрыта ли точка от наблюдателя: из глаз наблюдателя не видно присевшего в точке —
    /// ни корпуса (0.5 м), ни головы (1.0 м). Раньше проверялся только центр тела, и точка
    /// «за углом», где торчит голова, считалась укрытием.
    /// </summary>
    public bool IsCoveredFrom(Vector3 point, Vector3 observerEye) =>
        !ClearLine(observerEye, point + Vector3.Up * 0.5f) && !ClearLine(observerEye, point + Vector3.Up * 1.0f);

    /// <summary>
    /// Точку укрытия закрывает от наблюдателя её собственное препятствие: нормаль грани смотрит от
    /// наблюдателя, и линия от его глаз к присевшему в точке перекрыта препятствием не дальше
    /// <paramref name="maxGap"/> м от точки. <see cref="IsCoveredFrom"/> этого не различает: точку
    /// на лицевой грани стены «укрывало» любое дальнее препятствие у наблюдателя.
    /// </summary>
    public bool IsOwnSideCover(Vector3 point, Vector3 observerFeet, Vector3 observerEye, float maxGap = 2f)
    {
        if (CoverAt(point, 0.1f) is not { } c)
            return false;
        var to = observerFeet - c.Position;
        to.Y = 0f;
        if (to.LengthSquared() < 0.01f || c.Normal.Dot(to.Normalized()) > -0.3f)
            return false;
        // луч от присевшего к глазам наблюдателя: первое препятствие на нём — ближайшее к точке
        using var q = PhysicsRayQueryParameters3D.Create(c.Position + Vector3.Up * 0.5f, observerEye, WorldMask);
        var hit = Space.IntersectRay(q);
        if (hit.Count == 0)
            return false;
        var at = (Vector3)hit["position"];
        if (new Vector2(at.X - c.Position.X, at.Z - c.Position.Z).Length() > maxGap)
            return false;
        // голова и плечи присевшего тоже закрыты: у края препятствия плечо торчит из-за угла,
        // а бот-игрок и точная стрельба бьют по любой видимой части тела (плейтест: стоят у края стены)
        // ±0,55 м: плечи (0,27 м) плюс запас на то, что боец стоит не точно на точке (до 0,3–0,5 м)
        var side = to.Normalized().Cross(Vector3.Up) * 0.55f;
        return !ClearLine(observerEye, c.Position + Vector3.Up * 1.05f)
               && !ClearLine(observerEye, c.Position + Vector3.Up * 0.7f + side)
               && !ClearLine(observerEye, c.Position + Vector3.Up * 0.7f - side);
    }

    /// <summary>Видна ли цель стоя из точки хоть какой-то частью тела (голова, грудь, ноги, плечи).</summary>
    public bool SeesAnyPart(Vector3 eye, Vector3 targetFeet)
    {
        foreach (var part in BodyParts(eye, targetFeet, crouching: false))
            if (ClearLine(eye, part))
                return true;
        return false;
    }

    /// <summary>
    /// Точки на теле цели, в которые можно целиться: грудь, голова, ноги и оба плеча
    /// (плечо торчит из-за угла раньше центра). Порядок — от самой «выгодной» к краевым.
    /// </summary>
    public static IEnumerable<Vector3> BodyParts(Vector3 from, Vector3 feet, bool crouching)
    {
        var chest = feet + Vector3.Up * (crouching ? 0.7f : 1.1f);
        yield return chest;
        yield return feet + Vector3.Up * (crouching ? 1.05f : 1.6f);
        yield return feet + Vector3.Up * 0.35f;
        var to = chest - from;
        to.Y = 0f;
        if (to.LengthSquared() < 0.0001f)
            yield break;
        var side = to.Normalized().Cross(Vector3.Up) * 0.27f;
        yield return chest + side;
        yield return chest - side;
    }

    /// <summary>
    /// Видимая часть тела цели, в которую можно выстрелить из <paramref name="from"/> (луч упирается
    /// в саму цель, а не в препятствие или союзника), или null.
    /// </summary>
    public Vector3? VisiblePart(Vector3 from, Zero.Actors.ActorBody target, Rid self)
    {
        foreach (var part in BodyParts(from, target.GlobalPosition, target.IsCrouching))
            if (CanHit(from, target, part, self))
                return part;
        return null;
    }

    /// <summary>Ближайшая точка укрытия не дальше <paramref name="maxDist"/>.</summary>
    public CoverPoint? CoverAt(Vector3 p, float maxDist = 0.7f)
    {
        CoverPoint? best = null;
        var bestD = maxDist;
        foreach (var c in _cover)
        {
            var d = new Vector2(c.Position.X - p.X, c.Position.Z - p.Z).Length();
            if (d <= bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Высота, выше которой встать из-за укрытия бесполезно — выглядывать надо вбок.</summary>
    public const float HighCover = 1.5f;

    /// <summary>
    /// Откуда выстрелить, укрываясь в <paramref name="spot"/>: из самой точки стоя (низкое укрытие)
    /// или шаг вбок из-за края препятствия на 0.9–1.7 м (высокое укрытие, угол). null — выстрела нет.
    /// </summary>
    public Vector3? PeekSpot(Vector3 spot, Vector3 targetFeet)
    {
        var cover = CoverAt(spot);
        var low = cover is null || cover.Value.ObstacleHeight < HighCover;
        if (low && SeesAnyPart(spot + Vector3.Up * 1.6f, targetFeet))
            return spot;
        Vector3 tangent;
        if (cover is { } c)
            tangent = c.Normal.Cross(Vector3.Up).Normalized();
        else
        {
            var to = targetFeet - spot;
            to.Y = 0f;
            if (to.LengthSquared() < 0.0001f)
                return null;
            tangent = to.Normalized().Cross(Vector3.Up);
        }
        foreach (var off in SideOffsets)
        {
            var p = spot + tangent * off;
            p.Y = spot.Y;
            if (IsOnNavmesh(p, 0.3f) && ClearLine(spot + Vector3.Up * 1.0f, p + Vector3.Up * 1.0f)
                && SeesAnyPart(p + Vector3.Up * 1.6f, targetFeet))
                return p;
        }
        return null;
    }

    private static readonly float[] SideOffsets = { 0.9f, -0.9f, 1.3f, -1.3f, 1.7f, -1.7f };

    /// <summary>Видна ли цель стоя из точки (глаза на высоте 1.6 м).</summary>
    public bool CanSeeFrom(Vector3 point, Vector3 targetCenter) =>
        ClearLine(point + Vector3.Up * 1.6f, targetCenter);
}
