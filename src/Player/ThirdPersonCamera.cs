using Godot;

namespace Zero.Player;

/// <summary>
/// Камера от третьего лица: узел-шарнир с SpringArm3D и Camera3D внутри.
/// Мышь вращает шарнир, SpringArm не даёт камере уйти в стену.
/// Esc освобождает курсор, щелчок по окну снова захватывает.
/// </summary>
[GlobalClass]
public partial class ThirdPersonCamera : Node3D
{
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export(PropertyHint.Range, "-89,0")] public float MinPitchDegrees { get; set; } = -60f;
    [Export(PropertyHint.Range, "0,89")] public float MaxPitchDegrees { get; set; } = 35f;

    [ExportGroup("Прицел")]
    [Export] public float ArmLength { get; set; } = 2.4f;
    [Export] public float AimArmLength { get; set; } = 1.7f;
    [Export] public float Fov { get; set; } = 62f;
    [Export] public float AimFov { get; set; } = 42f;
    [Export] public float Shoulder { get; set; } = 0.5f;
    [Export] public float AimShoulder { get; set; } = 0.8f;
    /// <summary>Чувствительность мыши в прицеле (доля от обычной).</summary>
    [Export] public float AimSensitivityScale { get; set; } = 0.6f;

    public bool Aiming { get; private set; }
    private SpringArm3D? _arm;
    private Camera3D? _cam;

    private float _yaw;
    private float _pitch = Mathf.DegToRad(-15f);

    public override void _Ready()
    {
        _yaw = Rotation.Y;
        _arm = GetNodeOrNull<SpringArm3D>("SpringArm3D");
        _cam = _arm?.GetNodeOrNull<Camera3D>("Camera3D");
        if (_arm is not null && GetParent() is CollisionObject3D body)
            _arm.AddExcludedObject(body.GetRid());
        if (_arm is not null)
            _arm.SpringLength = ArmLength;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Apply();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                var s = MouseSensitivity * (Aiming ? AimSensitivityScale : 1f);
                _yaw -= motion.Relative.X * s;
                _pitch -= motion.Relative.Y * s;
                _pitch = Mathf.Clamp(_pitch, Mathf.DegToRad(MinPitchDegrees), Mathf.DegToRad(MaxPitchDegrees));
                Apply();
                break;
            case InputEventMouseButton { Pressed: true } when Input.MouseMode != Input.MouseModeEnum.Captured:
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;
        }
    }

    /// <summary>Плавно приблизить камеру к плечу и сузить обзор, пока игрок целится (ПКМ).</summary>
    public void UpdateZoom(bool aiming, float dt)
    {
        Aiming = aiming;
        var t = 1f - Mathf.Exp(-12f * dt);
        if (_arm is not null)
        {
            _arm.SpringLength = Mathf.Lerp(_arm.SpringLength, aiming ? AimArmLength : ArmLength, t);
            var p = _arm.Position;
            p.X = Mathf.Lerp(p.X, aiming ? AimShoulder : Shoulder, t);
            _arm.Position = p;
        }
        if (_cam is not null)
            _cam.Fov = Mathf.Lerp(_cam.Fov, aiming ? AimFov : Fov, t);
    }

    /// <summary>Горизонтальный поворот камеры — от него считается направление ходьбы игрока.</summary>
    public Basis YawBasis => new(Vector3.Up, _yaw);

    private void Apply() => Rotation = new Vector3(_pitch, _yaw, 0f);
}
