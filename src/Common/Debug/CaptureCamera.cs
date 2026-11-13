using Godot;

namespace Zero.Common.Debug;

/// <summary>
/// Камера для записи демонстраций (--write-movie): при запуске становится текущей.
/// Если задан <see cref="Follow"/>, следует за узлом с отступом <see cref="Offset"/>,
/// иначе неподвижно смотрит на <see cref="LookTarget"/>.
/// </summary>
[GlobalClass]
public partial class CaptureCamera : Camera3D
{
    [Export] public Vector3 LookTarget { get; set; }
    [Export] public Node3D? Follow { get; set; }
    [Export] public Vector3 Offset { get; set; } = new(-4f, 3.5f, 5f);
    [Export] public float Smoothing { get; set; } = 3f;

    public override void _Ready() => CallDeferred(MethodName.Activate);

    private void Activate()
    {
        if (Follow is not null)
            GlobalPosition = Follow.GlobalPosition + Offset;
        LookAt(Follow?.GlobalPosition + Vector3.Up ?? LookTarget, Vector3.Up);
        MakeCurrent();
    }

    public override void _Process(double delta)
    {
        if (Follow is null)
            return;
        var t = 1f - Mathf.Exp(-Smoothing * (float)delta);
        GlobalPosition = GlobalPosition.Lerp(Follow.GlobalPosition + Offset, t);
        LookAt(Follow.GlobalPosition + Vector3.Up, Vector3.Up);
    }
}
