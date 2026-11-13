using Godot;

namespace Zero.Arena;

/// <summary>
/// Визуальный слой арены поверх серых коробок-коллайдеров (ассеты Synty POLYGON Dark Fantasy).
/// Декор ничего не меняет в геймплее: при запуске у всех его потомков сразу удаляются
/// физические тела (префабы Synty приходят со своими StaticBody3D), поэтому модели не влияют
/// на движение, лучи зрения, точки укрытий и навмеш. Физика арены — по-прежнему серые коробки
/// в <c>NavigationRegion3D</c>; декор только подобран так, чтобы совпадать с ними по габаритам.
/// </summary>
public partial class ArenaDecor : Node3D
{
    /// <summary>Скрыть меши серых коробок (сами тела и формы остаются).</summary>
    [Export] public bool HideGreybox { get; set; } = true;

    /// <summary>Узел с серыми коробками; по умолчанию соседний <c>NavigationRegion3D</c>.</summary>
    [Export] public NodePath GreyboxRoot { get; set; } = new("../NavigationRegion3D");

    public override void _Ready()
    {
        // Наш _Ready срабатывает раньше _Ready корня арены (дети готовы первыми), поэтому
        // к моменту запекания навмеша и сбора точек укрытий тел декора уже нет.
        var removed = StripCollisions(this);
        var hidden = HideGreybox ? HideGreyboxMeshes() : 0;
        GD.Print($"[декор] удалено тел: {removed}, скрыто серых мешей: {hidden}");
    }

    public override void _ExitTree()
    {
        // При выходе из игры сотни узлов декора освобождаются дольше, и финализатор .NET успевал
        // добраться до обёрток уже отработавших Tween/материалов, когда движок был наполовину
        // разобран (FATAL "!rc_owner" после длинных прогонов). Дочищаем их, пока движок жив.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
    }

    /// <summary>Немедленно удалить все CollisionObject3D среди потомков (не QueueFree: их не должен увидеть никто).</summary>
    private static int StripCollisions(Node node)
    {
        var count = 0;
        foreach (var child in node.GetChildren())
        {
            if (child is CollisionObject3D body)
            {
                body.CollisionLayer = 0;
                body.CollisionMask = 0;
                node.RemoveChild(body);
                body.Free();
                count++;
                continue;
            }
            count += StripCollisions(child);
        }
        return count;
    }

    /// <summary>Скрыть узлы «Mesh» у статических тел серой арены.</summary>
    private int HideGreyboxMeshes()
    {
        if (GetNodeOrNull(GreyboxRoot) is not { } root)
            return 0;
        var count = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is StaticBody3D body && body.GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh)
            {
                mesh.Visible = false;
                count++;
            }
        }
        return count;
    }
}
