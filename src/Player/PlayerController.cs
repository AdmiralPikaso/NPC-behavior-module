using Godot;
using Zero.Actors;

namespace Zero.Player;

/// <summary>
/// Игрок: источник намерений для общего слоя действий <see cref="ActorBody"/>.
/// Сам ничего не двигает — переводит ввод в те же атомарные действия, что и ИИ NPC:
///   WASD — <see cref="ActorBody.Move"/> относительно камеры (шаг; Shift — бег; Alt — спринт);
///   C / Ctrl — присед; Space — прыжок;
///   ПКМ (держать) — прицел: персонаж смотрит туда же, куда камера, и ходит боком;
///   ЛКМ — огненный шар в точку под прицелом.
/// </summary>
[GlobalClass]
public partial class PlayerController : ActorBody
{
	[ExportGroup("Камера")]
	[Export] public float StandCameraHeight { get; set; } = 1.55f;
	[Export] public float CrouchCameraHeight { get; set; } = 1.05f;
	/// <summary>Сколько секунд персонаж ещё смотрит в точку выстрела после стрельбы без прицела.</summary>
	[Export] public float FireFacingTime { get; set; } = 0.5f;
	[Export] public float RespawnDelay { get; set; } = 3f;
	/// <summary>Возрождаться ли после смерти (на арене раунды перезапускает матч).</summary>
	[Export] public bool AutoRespawn { get; set; } = true;

	/// <summary>Если задан, игроком управляет бот, а не ввод.</summary>
	public IPlayerBrain? Bot { get; set; }

	private ThirdPersonCamera _camera = null!;
	private Camera3D? _view;
	private Label? _hud;
	private float _faceAfterFire;
	private bool _fireQueued;
	private bool _aiming;
	private bool _throwQueued;
	private bool _aimOnEnemy;
	private Control? _crosshair;
	private float _respawnLeft;
	private Transform3D _spawn;

	public override void _Ready()
	{
		base._Ready();
		_camera = GetNode<ThirdPersonCamera>("CameraRig");
		_view = _camera.GetNodeOrNull<Camera3D>("SpringArm3D/Camera3D");
		_spawn = GlobalTransform;
		BuildHud();
		Died += _ =>
		{
			_respawnLeft = RespawnDelay;
			if (AutoRespawn)
				GD.Print($"[бой] игрок повержен, возрождение через {RespawnDelay:0} с");
		};
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (IsDead)
			return;
		if (Bot is not null)
			return;
		if (@event.IsActionPressed("crouch"))
			SetCrouching(!IsCrouching);
		if (@event.IsActionPressed("jump"))
			Jump();
		if (@event.IsActionPressed("takedown"))
			TryTakedown();
		// короткий щелчок может уложиться между двумя физическими кадрами — запоминаем его
		if (@event.IsActionPressed("fire"))
			_fireQueued = true;
		if (@event.IsActionPressed("alt_fire") && Input.MouseMode == Input.MouseModeEnum.Captured)
			_throwQueued = true;
	}

	protected override void BeforeMove(float dt)
	{
		if (IsDead)
		{
			if (!AutoRespawn)
				return;
			_respawnLeft -= dt;
			if (_respawnLeft <= 0f)
			{
				GlobalTransform = _spawn;
				Velocity = Vector3.Zero;
				Revive();
			}
			return;
		}

		if (Bot is not null)
		{
			Bot.Tick(this, dt);
			return;
		}

		var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
		var direction = _camera.YawBasis * new Vector3(input.X, 0f, input.Y);
		var gait = Input.IsActionPressed("sprint") ? Gait.Sprint
			: Input.IsActionPressed("run") ? Gait.Run
			: Gait.Walk;
		Move(direction, gait);

		var aimPoint = CameraAimPoint();
		var aiming = Input.IsActionPressed("aim");
		_aiming = aiming;
		var wantsFire = _fireQueued || Input.IsActionPressed("fire");
		if (wantsFire && Input.MouseMode == Input.MouseModeEnum.Captured && Fire(aimPoint))
		{
			_faceAfterFire = FireFacingTime;
			_fireQueued = false;
		}

		if (_throwQueued)
		{
			_throwQueued = false;
			if (ThrowFireball(aimPoint) is not null)
				_faceAfterFire = FireFacingTime;
		}

		_faceAfterFire = Mathf.Max(0f, _faceAfterFire - dt);
		if (aiming || _faceAfterFire > 0f)
			AimAt(aimPoint);
		else
			ClearAim();
	}

	protected override void AfterMove(float dt)
	{
		_camera.UpdateZoom(Bot is null && _aiming && !IsDead, dt);
		UpdateCrosshair();
		var camPos = _camera.Position;
		camPos.Y = Mathf.Lerp(camPos.Y, IsCrouching ? CrouchCameraHeight : StandCameraHeight, 1f - Mathf.Exp(-10f * dt));
		_camera.Position = camPos;

		if (_hud is not null)
			_hud.Text = IsDead
				? $"Повержен — возрождение через {_respawnLeft:0.0} с"
				: $"HP {Health:0}/{MaxHealth:0}   {(IsReloading ? $"перезарядка магазина {ReloadLeft:0.0} с" : CanFire ? "огонь готов" : $"перезарядка {FireCooldownLeft:0.0} с")}";
	}

	private void UpdateCrosshair()
	{
		if (_crosshair is null)
			return;
		_crosshair.Visible = Bot is null && !IsDead;
		_crosshair.Modulate = _aimOnEnemy ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 0.9f, 0.6f, 0.85f);
		_crosshair.Scale = Vector2.One * (_aiming ? 0.8f : 1f);
	}

	/// <summary>
	/// Помощь в прицеливании для живого игрока: шар задевает цель в радиусе 0.35 м от линии полёта.
	/// Боту (прогоны сравнения) помощь не даётся.
	/// </summary>
	protected override float FireballHitRadius => Bot is null ? 0.35f : 0f;

	/// <summary>Точка, куда смотрит центр экрана (луч из камеры), или точка в 60 м впереди.</summary>
	private Vector3 CameraAimPoint()
	{
		if (_view is null)
			return GlobalPosition + Facing * 30f;
		var from = _view.GlobalPosition;
		var to = from - _view.GlobalBasis.Z * 60f;
		using var query = PhysicsRayQueryParameters3D.Create(from, to);
		query.Exclude = new() { GetRid() };
		var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
		_aimOnEnemy = hit.Count > 0 && hit["collider"].AsGodotObject() is ActorBody { IsDead: false } a && a.Team != Team;
		return hit.Count > 0 ? (Vector3)hit["position"] : to;
	}

	private void BuildHud()
	{
		// слой 5: выше затемнения экрана при низком здоровье на арене
		var layer = new CanvasLayer { Name = "Hud", Layer = 5 };
		AddChild(layer);

		// прицел: четыре штриха вокруг центра и точка; краснеет, когда под ним враг
		_crosshair = new Control
		{
			AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		foreach (var (x, y, w, h) in new[] { (-2f, -2f, 4f, 4f), (-14f, -1f, 8f, 2f), (6f, -1f, 8f, 2f), (-1f, -14f, 2f, 8f), (-1f, 6f, 2f, 8f) })
			_crosshair.AddChild(new ColorRect
			{
				Position = new Vector2(x, y), Size = new Vector2(w, h),
				Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore,
			});
		layer.AddChild(_crosshair);

		_hud = new Label
		{
			Position = new Vector2(16f, 12f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_hud.AddThemeFontSizeOverride("font_size", 20);
		_hud.AddThemeColorOverride("font_outline_color", Colors.Black);
		_hud.AddThemeConstantOverride("outline_size", 6);
		layer.AddChild(_hud);
	}
}
