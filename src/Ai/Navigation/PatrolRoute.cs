using System.Collections.Generic;
using System.Linq;
using Godot;
using Zero.Ai.Core;

namespace Zero.Ai.Navigation;

/// <summary>Маршрут патруля: точки — это дочерние узлы <see cref="PatrolPoint"/> по порядку.</summary>
[GlobalClass]
public partial class PatrolRoute : Node3D
{
    [Export]
    public PatrolMode Mode { get; set; } = PatrolMode.Loop;

    public IReadOnlyList<PatrolPoint> Points => GetChildren().OfType<PatrolPoint>().ToList();
}
