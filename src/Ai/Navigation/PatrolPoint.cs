using Godot;

namespace Zero.Ai.Navigation;

/// <summary>
/// Точка патруля. Ставится на сцену дочерним узлом <see cref="PatrolRoute"/>
/// и двигается мышью в редакторе. Синяя ось (−Z) показывает, куда смотреть на стоянке.
/// </summary>
[GlobalClass]
public partial class PatrolPoint : Marker3D
{
    /// <summary>Сколько секунд NPC стоит в этой точке.</summary>
    [Export(PropertyHint.Range, "0,30,0.1,suffix:s")]
    public float WaitTime { get; set; } = 1.5f;

    /// <summary>Повернуться ли на стоянке в направлении точки (её −Z).</summary>
    [Export]
    public bool FaceDirection { get; set; }

    public Vector3 FacingDirection => -GlobalBasis.Z;
}
