using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Zero.Common.Debug;

/// <summary>
/// Проигрывает заранее записанный «ввод игрока» — для автоматической проверки
/// управления без человека (вместе с --write-movie). Шаги — строки вида:
///   "1.5 press move_forward", "3 release run", "4 tap jump",
///   "7 mouse 87 -10" (сдвиг мыши в пикселях).
/// Время — секунды от запуска сцены.
/// </summary>
[GlobalClass]
public partial class InputScript : Node
{
    [Export] public string[] Steps { get; set; } = System.Array.Empty<string>();

    private readonly List<(float Time, string[] Args)> _steps = new();
    private readonly List<string> _tapped = new();
    private float _time;
    private int _next;

    public override void _Ready()
    {
        foreach (var line in Steps)
        {
            var parts = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                _steps.Add((t, parts[1..]));
        }
        _steps.Sort((a, b) => a.Time.CompareTo(b.Time));
        // стрельба игрока разрешена только при захваченном курсоре
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _PhysicsProcess(double delta)
    {
        foreach (var action in _tapped)
            Release(action);
        _tapped.Clear();

        _time += (float)delta;
        while (_next < _steps.Count && _steps[_next].Time <= _time)
        {
            var args = _steps[_next++].Args;
            GD.Print($"[ввод {_time:0.0} с] {string.Join(' ', args)}");
            if (args[1] == "fire")
                GD.Print($"  курсор: {Input.MouseMode}");
            switch (args[0])
            {
                case "press": Press(args[1]); break;
                case "release": Release(args[1]); break;
                case "tap": Press(args[1]); _tapped.Add(args[1]); break;
                case "mouse":
                    Input.ParseInputEvent(new InputEventMouseMotion
                    {
                        Relative = new Vector2(
                            float.Parse(args[1], CultureInfo.InvariantCulture),
                            float.Parse(args[2], CultureInfo.InvariantCulture)),
                    });
                    break;
            }
        }
    }

    // Через события, чтобы срабатывали и _UnhandledInput (присед, прыжок), и Input.IsActionPressed.
    private static void Press(string action) =>
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });

    private static void Release(string action) =>
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
}
