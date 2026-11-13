using Godot;

namespace Zero.Ai.Tactics;

/// <summary>
/// Разметка сильной позиции в редакторе: узел ставится в сцену уровня, размер области и числа задаются
/// в инспекторе. Арена собирает такие узлы (группа <c>hard_point</c>); если их нет, берёт разметку из кода.
/// </summary>
[GlobalClass]
public partial class HardPointMarker : Node3D
{
    public const string Group = "hard_point";

    /// <summary>Размер области по X и Z, м.</summary>
    [Export] public Vector2 Size { get; set; } = new(8f, 8f);
    [Export] public int MinFighters { get; set; }
    [Export] public int MaxFighters { get; set; } = 1;
    [Export] public bool Enabled { get; set; } = true;

    public override void _Ready() => AddToGroup(Group);

    public HardPoint ToHardPoint() => new()
    {
        Name = Name.ToString(),
        Center = new Vector3(GlobalPosition.X, 0f, GlobalPosition.Z),
        HalfSize = Size / 2f,
        Min = MinFighters,
        Max = MaxFighters,
        Enabled = Enabled,
    };
}
