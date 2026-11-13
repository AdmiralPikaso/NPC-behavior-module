using Godot;

namespace Zero.Common.Animation;

/// <summary>
/// Связывает движение персонажа с анимациями. Сам собирает AnimationTree поверх
/// AnimationPlayer из импортированной модели:
///   stand  (BlendSpace2D по локальной скорости: Idle, Walk_F/B/L/R, Run_F, Sprint_F)
///   crouch (BlendSpace2D: Idle_Crouch, Crouch_F/B/L/R)
///   crouch_amount (Blend2 stand/crouch) → air_amount (Blend2 с Fall)
///   → jump (OneShot: взлёт из Jump) → land (OneShot: Land).
/// Локальная скорость считается в системе модели: X — вправо, Y — вперёд, поэтому
/// персонаж может идти боком и спиной, глядя на цель (стрейф).
/// Анимации in-place: перемещает персонажа код, а не анимация.
/// </summary>
[GlobalClass]
public partial class LocomotionDriver : Node
{
    private static readonly string[] LoopedClips =
    {
        "Idle", "Idle_Crouch", "Walk_F", "Walk_B", "Walk_L", "Walk_R",
        "Run_F", "Sprint_F", "Crouch_F", "Crouch_B", "Crouch_L", "Crouch_R", "Fall",
    };

    /// <summary>Узел модели (инстанс PolygonCharacter.glb).</summary>
    [Export] public NodePath ModelPath { get; set; } = "../Visual/Model";

    // Скорости (м/с), при которых клип выглядит естественно — точки на BlendSpace.
    [Export] public float WalkClipSpeed { get; set; } = 1.4f;
    [Export] public float RunClipSpeed { get; set; } = 3.6f;
    [Export] public float SprintClipSpeed { get; set; } = 5.8f;
    [Export] public float CrouchClipSpeed { get; set; } = 1.2f;

    /// <summary>Насколько быстро параметры догоняют целевые значения.</summary>
    [Export] public float Smoothing { get; set; } = 10f;

    private AnimationTree? _tree;
    private Vector2 _motion;
    private Vector2 _targetMotion;
    private float _crouch;
    private float _targetCrouch;
    private float _air;
    private float _targetAir;

    public override void _Ready()
    {
        var model = GetNodeOrNull<Node>(ModelPath);
        var player = model?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
        if (model is null || player is null)
        {
            GD.PushWarning($"{Name}: не найден AnimationPlayer в '{ModelPath}', анимации отключены.");
            return;
        }

        foreach (var clip in LoopedClips)
        {
            if (player.HasAnimation(clip))
                player.GetAnimation(clip).LoopMode = Godot.Animation.LoopModeEnum.Linear;
            else
                GD.PushWarning($"{Name}: в модели нет клипа '{clip}'.");
        }

        _tree = new AnimationTree { Name = "LocomotionTree", TreeRoot = BuildGraph() };
        model.AddChild(_tree);
        _tree.AnimPlayer = _tree.GetPathTo(player);
        _tree.Active = true;
    }

    /// <summary>
    /// Сообщить движение: локальная скорость (x — вправо, y — вперёд, м/с),
    /// присед и находится ли персонаж в воздухе.
    /// </summary>
    public void SetMotion(Vector2 localVelocity, bool crouched, bool airborne = false)
    {
        _targetMotion = localVelocity;
        _targetCrouch = crouched ? 1f : 0f;
        _targetAir = airborne ? 1f : 0f;
    }

    /// <summary>Совместимость со старым вызовом: движение только вперёд.</summary>
    public void SetMotion(float horizontalSpeed, bool crouched) =>
        SetMotion(new Vector2(0f, horizontalSpeed), crouched);

    /// <summary>Проиграть взлёт (вызывается в момент прыжка).</summary>
    public void PlayJump() => FireOneShot("jump");

    /// <summary>Проиграть приземление (после заметного полёта).</summary>
    public void PlayLand() => FireOneShot("land");

    public override void _Process(double delta)
    {
        if (_tree is null)
            return;
        var t = 1f - Mathf.Exp(-Smoothing * (float)delta);
        _motion = _motion.Lerp(_targetMotion, t);
        _crouch = Mathf.Lerp(_crouch, _targetCrouch, t);
        // в воздух переходим быстрее, чтобы падение не «запаздывало»
        _air = Mathf.Lerp(_air, _targetAir, 1f - Mathf.Exp(-Smoothing * 1.5f * (float)delta));
        _tree.Set("parameters/stand/blend_position", _motion);
        _tree.Set("parameters/crouch/blend_position", _motion);
        _tree.Set("parameters/crouch_amount/blend_amount", _crouch);
        _tree.Set("parameters/air_amount/blend_amount", _air);
    }

    private void FireOneShot(string node) =>
        _tree?.Set($"parameters/{node}/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);

    private AnimationNodeBlendTree BuildGraph()
    {
        var w = WalkClipSpeed;
        var stand = new AnimationNodeBlendSpace2D
        {
            MinSpace = new Vector2(-w * 1.5f, -w * 1.5f),
            MaxSpace = new Vector2(w * 1.5f, SprintClipSpeed),
        };
        stand.AddBlendPoint(Clip("Idle"), Vector2.Zero, -1, "Idle");
        stand.AddBlendPoint(Clip("Walk_F"), new Vector2(0f, w), -1, "Walk_F");
        stand.AddBlendPoint(Clip("Walk_B"), new Vector2(0f, -w), -1, "Walk_B");
        stand.AddBlendPoint(Clip("Walk_L"), new Vector2(-w, 0f), -1, "Walk_L");
        stand.AddBlendPoint(Clip("Walk_R"), new Vector2(w, 0f), -1, "Walk_R");
        stand.AddBlendPoint(Clip("Run_F"), new Vector2(0f, RunClipSpeed), -1, "Run_F");
        stand.AddBlendPoint(Clip("Sprint_F"), new Vector2(0f, SprintClipSpeed), -1, "Sprint_F");

        var c = CrouchClipSpeed;
        var crouch = new AnimationNodeBlendSpace2D
        {
            MinSpace = new Vector2(-c * 2f, -c * 2f),
            MaxSpace = new Vector2(c * 2f, c * 2f),
        };
        crouch.AddBlendPoint(Clip("Idle_Crouch"), Vector2.Zero, -1, "Idle_Crouch");
        crouch.AddBlendPoint(Clip("Crouch_F"), new Vector2(0f, c), -1, "Crouch_F");
        crouch.AddBlendPoint(Clip("Crouch_B"), new Vector2(0f, -c), -1, "Crouch_B");
        crouch.AddBlendPoint(Clip("Crouch_L"), new Vector2(-c, 0f), -1, "Crouch_L");
        crouch.AddBlendPoint(Clip("Crouch_R"), new Vector2(c, 0f), -1, "Crouch_R");

        // Из Jump_Idle берём только сам взлёт (кадры ~19–34 при 30 к/с),
        // подготовительный присед пропускаем, чтобы прыжок был мгновенным.
        var jumpUp = Clip("Jump");
        jumpUp.UseCustomTimeline = true;
        jumpUp.StartOffset = 0.55;
        jumpUp.TimelineLength = 0.5;
        jumpUp.StretchTimeScale = false;
        jumpUp.LoopMode = Godot.Animation.LoopModeEnum.None;

        var land = Clip("Land");
        land.UseCustomTimeline = true;
        land.StartOffset = 0.12;
        land.TimelineLength = 0.45;
        land.StretchTimeScale = false;
        land.LoopMode = Godot.Animation.LoopModeEnum.None;

        var jumpShot = new AnimationNodeOneShot { FadeInTime = 0.05, FadeOutTime = 0.2 };
        var landShot = new AnimationNodeOneShot { FadeInTime = 0.05, FadeOutTime = 0.2 };

        var graph = new AnimationNodeBlendTree();
        graph.AddNode("stand", stand, new Vector2(0, 0));
        graph.AddNode("crouch", crouch, new Vector2(0, 200));
        graph.AddNode("crouch_amount", new AnimationNodeBlend2(), new Vector2(300, 100));
        graph.AddNode("fall", Clip("Fall"), new Vector2(300, 300));
        graph.AddNode("air_amount", new AnimationNodeBlend2(), new Vector2(500, 150));
        graph.AddNode("jump_clip", jumpUp, new Vector2(500, 350));
        graph.AddNode("jump", jumpShot, new Vector2(700, 200));
        graph.AddNode("land_clip", land, new Vector2(700, 400));
        graph.AddNode("land", landShot, new Vector2(900, 250));

        graph.ConnectNode("crouch_amount", 0, "stand");
        graph.ConnectNode("crouch_amount", 1, "crouch");
        graph.ConnectNode("air_amount", 0, "crouch_amount");
        graph.ConnectNode("air_amount", 1, "fall");
        graph.ConnectNode("jump", 0, "air_amount");
        graph.ConnectNode("jump", 1, "jump_clip");
        graph.ConnectNode("land", 0, "jump");
        graph.ConnectNode("land", 1, "land_clip");
        graph.ConnectNode("output", 0, "land");
        return graph;
    }

    private static AnimationNodeAnimation Clip(string name) => new() { Animation = name };
}
