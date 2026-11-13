using Godot;
using Zero.Actors;
using Zero.Ai.Nodes;
using Zero.Ai.Tactics;

namespace Zero.Ai.Stealth;

/// <summary>
/// Восприятие и подозрение одного NPC.
///
/// Зрение (по Splinter Cell: Blacklist и The Last of Us): два конуса — ближний широкий и дальний
/// узкий. Если цель в конусе, лучи из глаз проверяют три точки её тела (голова, грудь, ноги);
/// доля видимых точек, освещённость цели, присед и скорость задают, как быстро растёт подозрение.
/// Обнаружение не мгновенное: подозрение копится (в TLOU — 1–2 секунды в конусе).
///
/// Слух: звуки из <see cref="NoiseSystem"/> добавляют подозрение и «точку интереса».
/// Подозрение 0…100 с порогами из документа по архитектуре: 30 — подозревает, 70 — тревога,
/// ниже 15 — успокаивается.
/// </summary>
public sealed class Awareness
{
    public const float SuspiciousThreshold = 30f;
    public const float AlarmThreshold = 70f;
    public const float CalmThreshold = 15f;

    // параметры зрения
    public float NearDistance { get; set; } = 7f;
    public float NearHalfAngle { get; set; } = 60f;
    public float FarDistance { get; set; } = 20f;
    public float FarHalfAngle { get; set; } = 28f;
    public float NearRate { get; set; } = 110f;
    public float FarRate { get; set; } = 40f;
    public float DecayRate { get; set; } = 6f;

    public float Suspicion { get; private set; }
    /// <summary>Однажды поднятая тревога не забывается полностью (Blacklist, «среднее состояние» TLOU 2).</summary>
    public bool Heightened { get; set; }
    /// <summary>Видит ли цель в этом кадре (в конусе и хотя бы одна точка тела не закрыта).</summary>
    public bool SeesTarget { get; private set; }
    /// <summary>Последнее, что вызвало подозрение (место увиденного или услышанного).</summary>
    public Vector3? PointOfInterest { get; private set; }
    /// <summary>Пришёл крик союзника о тревоге.</summary>
    public bool AlarmRaisedByOther { get; private set; }

    private readonly NpcAgent _npc;
    private readonly TacticalWorld _world;
    private ActorBody? _target;

    public Awareness(NpcAgent npc, TacticalWorld world)
    {
        _npc = npc;
        _world = world;
    }

    public void SetTarget(ActorBody target) => _target = target;

    public void Reset()
    {
        Suspicion = 0f;
        Heightened = false;
        SeesTarget = false;
        PointOfInterest = null;
        AlarmRaisedByOther = false;
    }

    public void ConsumeAlarmFlag() => AlarmRaisedByOther = false;

    public void Raise(float amount, Vector3? poi)
    {
        Suspicion = Mathf.Clamp(Suspicion + amount, 0f, 100f);
        if (poi is not null)
            PointOfInterest = poi;
    }

    public void ClearPointOfInterest() => PointOfInterest = null;

    /// <summary>Обновить зрение; <paramref name="combat"/> — в бою конусы шире и реакция быстрее.</summary>
    public void Update(float dt, bool combat)
    {
        SeesTarget = false;
        var gain = 0f;
        if (_target is { IsDead: false } t)
        {
            var eye = _npc.EyePosition;
            var to = t.CenterMass - eye;
            var dist = new Vector2(to.X, to.Z).Length();
            var flat = new Vector3(to.X, 0f, to.Z);
            var angle = flat.LengthSquared() > 0.001f ? Mathf.RadToDeg(_npc.Facing.AngleTo(flat.Normalized())) : 0f;
            var wide = combat ? 1.5f : 1f;
            float rate = 0f;
            if (dist <= NearDistance * wide && angle <= Mathf.Min(NearHalfAngle * wide, 110f))
                rate = NearRate;
            else if (dist <= FarDistance * wide && angle <= FarHalfAngle * wide)
                rate = FarRate * (1f - 0.6f * (dist - NearDistance) / (FarDistance - NearDistance));
            if (rate > 0f)
            {
                var visible = 0;
                var head = t.GlobalPosition + Vector3.Up * (t.IsCrouching ? 1.05f : 1.65f);
                if (_world.ClearLine(eye, head)) visible++;
                if (_world.ClearLine(eye, t.CenterMass)) visible++;
                if (_world.ClearLine(eye, t.GlobalPosition + Vector3.Up * 0.35f)) visible++;
                if (visible > 0)
                {
                    SeesTarget = true;
                    var light = LightField.LevelAt(_npc, t.CenterMass);
                    var stance = t.IsCrouching ? 0.55f : 1f;
                    var motion = t.HorizontalSpeed > 3f ? 1.4f : t.HorizontalSpeed > 0.3f ? 1f : 0.75f;
                    gain = rate * (visible / 3f) * (0.2f + 0.8f * light) * stance * motion;
                    if (combat || Heightened)
                        gain *= combat ? 3f : 1.5f;
                    PointOfInterest = t.GlobalPosition;
                }
            }
        }

        if (gain > 0f)
            Suspicion = Mathf.Min(100f, Suspicion + gain * dt);
        else
            Suspicion = Mathf.Max(Heightened ? 10f : 0f, Suspicion - DecayRate * dt);
    }

    /// <summary>Реакция на звук. Возвращает true, если звук услышан.</summary>
    public bool Hear(NoiseEvent noise)
    {
        if (_npc.IsDead || noise.Source == _npc)
            return false;
        var straight = _npc.GlobalPosition.DistanceTo(noise.Position);
        if (straight > noise.Radius)
            return false;
        var path = NoiseSystem.PathLength(_npc.GetWorld3D().NavigationMap, noise.Position, _npc.GlobalPosition);
        if (path > noise.Radius)
            return false;
        var k = 1f - path / noise.Radius; // 1 — рядом, 0 — на пределе слышимости
        switch (noise.Kind)
        {
            case NoiseKind.Shout:
                AlarmRaisedByOther = true;
                PointOfInterest = noise.Info;
                Suspicion = Mathf.Max(Suspicion, AlarmThreshold);
                break;
            case NoiseKind.Footstep:
                if (noise.Source is NpcAgent)
                    return false; // шаги своих не интересны
                Raise(14f * k + 4f, noise.Position);
                break;
            case NoiseKind.Shot:
                Raise(55f * k + 25f, noise.Position);
                break;
            case NoiseKind.Impact:
                Raise(35f * k + 15f, noise.Position);
                break;
        }
        return true;
    }
}
