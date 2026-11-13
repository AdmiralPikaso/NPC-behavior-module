using System.Collections.Generic;
using Godot;
using Zero.Actors;
using Zero.Ai.Actions;
using Zero.Ai.Navigation;
using Zero.Ai.Stealth;

namespace Zero.Ai.Nodes;

/// <summary>Чем NPC занят, если им никто не командует.</summary>
public enum NpcBehaviour
{
	/// <summary>Стоит на месте (мишень).</summary>
	Idle,
	/// <summary>Ходит по <see cref="NpcAgent.Route"/>.</summary>
	Patrol,
	/// <summary>По кругу показывает все атомарные действия (итерация 1).</summary>
	Showcase,
}

public enum NavResult { NotReady, Moving, Arrived, Unreachable }

/// <summary>
/// NPC: тело с общим слоем действий (<see cref="ActorBody"/>) плюс навигация
/// и «мозг», который исполняет корневое действие <see cref="NpcAction"/>.
/// </summary>
[GlobalClass]
public partial class NpcAgent : ActorBody
{
	[Export] public NpcBehaviour Behaviour { get; set; } = NpcBehaviour.Patrol;
	/// <summary>Маршрут патруля; в режиме Showcase — точки показа (нужно 4).</summary>
	[Export] public PatrolRoute? Route { get; set; }
	[Export] public int StartPointIndex { get; set; }
	/// <summary>Цель для стрельбы в режиме Showcase.</summary>
	[Export] public Node3D? Target { get; set; }

	/// <summary>Корневое действие. Можно заменить из кода (позже — тактикой).</summary>
	public NpcAction? Brain
	{
		get => _brain;
		set
		{
			if (_brain is not null && _brainStarted)
				_brain.Stop(this);
			_brain = value;
			_brainStarted = false;
		}
	}

	private NavigationAgent3D _nav = null!;
	private Label3D? _label;
	private NpcAction? _brain;
	private bool _brainStarted;

	public override void _Ready()
	{
		base._Ready();
		_nav = GetNode<NavigationAgent3D>("NavigationAgent3D");
		_label = GetNodeOrNull<Label3D>("DebugLabel");
		if (_label is not null)
		{
			// мельче и растёт вверх от головы: HP — нижняя строка, подписи соседей меньше налезают друг на друга
			_label.FontSize = 28;
			_label.OutlineSize = 6;
			_label.VerticalAlignment = VerticalAlignment.Bottom;
		}
		Brain = BuildDefaultBrain();
	}

	private NpcAction? BuildDefaultBrain()
	{
		switch (Behaviour)
		{
			case NpcBehaviour.Patrol when Route is not null && Route.Points.Count > 0:
				return new PatrolAction(Route, StartPointIndex);
			case NpcBehaviour.Showcase when Route is not null && Route.Points.Count >= 4:
				return BuildShowcase(Route);
			case NpcBehaviour.Idle:
				return null;
			default:
				GD.PushWarning($"{Name}: для режима {Behaviour} не хватает маршрута — NPC будет стоять.");
				return null;
		}
	}

	/// <summary>Последовательно выполняет все атомарные действия и начинает сначала.</summary>
	private SequenceAction BuildShowcase(PatrolRoute route)
	{
		var p = route.Points;
		Vector3 At(int i) => p[i].GlobalPosition;
		Vector3 Aim() => Target switch
		{
			ActorBody actor => actor.CenterMass,
			not null => Target.GlobalPosition + Vector3.Up * 1.1f,
			_ => GlobalPosition + Facing * 10f + Vector3.Up * 1.1f,
		};

		return new SequenceAction(new List<NpcAction>
		{
			new MoveToAction(At(0), Gait.Walk, "1. Шаг"),
			new WaitAction(0.8f, "Пауза"),
			new MoveToAction(At(1), Gait.Run, "2. Бег с прыжком") { JumpAtDistance = 4f },
			new MoveToAction(At(2), Gait.Sprint, "3. Спринт"),
			new CrouchAction(true),
			new MoveToAction(At(3), Gait.Walk, "4. Присед, шаг"),
			new WaitAction(1f, "Сидит"),
			new CrouchAction(false),
			new JumpAction(),
			new FaceAction(Aim),
			new FireAction(Aim, 3),
			new FireAction(Aim, 1) { UseFireball = true },
			new StrafeAction(Aim, left: true, seconds: 2.5f),
			new StrafeAction(Aim, left: false, seconds: 2.5f),
			new WaitAction(1.5f, "Пауза"),
		}, loop: true) { LogSteps = true };
	}

	// ───────────── навигация ─────────────

	public bool NavReady => NavigationServer3D.MapGetIterationId(_nav.GetNavigationMap()) > 0;

	/// <summary>
	/// Шаг движения к точке по навигационной сетке (вызывать каждый кадр).
	/// По прибытии останавливается.
	/// </summary>
	public NavResult NavigateTo(Vector3 point, Gait gait, float tolerance = 0.5f)
	{
		if (!NavReady)
		{
			StopMoving();
			return NavResult.NotReady;
		}

		var flat = point - GlobalPosition;
		flat.Y = 0f;
		if (flat.Length() <= tolerance)
		{
			StopMoving();
			return NavResult.Arrived;
		}

		if (_nav.TargetPosition.DistanceSquaredTo(point) > 0.04f)
			_nav.TargetPosition = point;

		if (_nav.IsNavigationFinished())
		{
			StopMoving();
			// дошли до ближайшей доступной точки, но не до цели
			return flat.Length() <= tolerance + 0.75f ? NavResult.Arrived : NavResult.Unreachable;
		}

		var toNext = _nav.GetNextPathPosition() - GlobalPosition;
		toNext.Y = 0f;
		if (toNext.LengthSquared() > 0.0001f)
			Move(toNext.Normalized(), gait);
		return NavResult.Moving;
	}

	/// <summary>Движение напрямую, без навигации (короткие манёвры: стрейф, отскок).</summary>
	public void MoveDirect(Vector3 direction, Gait gait) => Move(direction, gait);

	// ───────────── мозг ─────────────

	protected override void BeforeMove(float dt)
	{
		if (IsDead || _brain is null)
			return;
		if (!_brainStarted)
		{
			_brain.Start(this);
			_brainStarted = true;
		}
		if (_brain.Tick(this, dt) != ActionStatus.Running)
		{
			_brain.Stop(this);
			_brain = null;
		}
	}

	protected override void AfterMove(float dt)
	{
		if (_label is null)
			return;
		var hp = $"HP {Health:0}/{MaxHealth:0}";
		if (_brain is StealthBrain sb && ShowLabelDetails)
			hp = $"подозрение {sb.Awareness.Suspicion:0}   {hp}";
		_label.Text = IsDead ? "Повержен"
			: !ShowLabelDetails ? hp
			: _brain switch
			{
				SequenceAction seq => $"{seq.Name}\n{hp}",
				null => hp,
				_ => $"{_brain.Name}\n{hp}",
			};
		// цвет подписи = уровень тревоги (читаемость для игрока, как индикатор в Assassin's Creed)
		_label.Modulate = Alert switch
		{
			AlertState.Alarm => new Color(1f, 0.3f, 0.25f),
			AlertState.Searching => new Color(1f, 0.6f, 0.2f),
			AlertState.Suspicious => new Color(1f, 0.9f, 0.3f),
			_ => new Color(0.75f, 0.9f, 1f),
		};
	}

	/// <summary>
	/// Показывать над NPC роль и состояние; false — только HP (на арене переключается вместе с текстом, клавиша H).
	/// </summary>
	public static bool ShowLabelDetails { get; set; } = true;

	/// <summary>Состояние тревоги (ставит <see cref="StealthBrain"/>; без стелса — Calm).</summary>
	public AlertState Alert { get; set; } = AlertState.Calm;

	/// <summary>
	/// Тихо устранить можно того, кто не в тревоге, или любого — со спины.
	/// </summary>
	public override bool CanBeTakenDownBy(ActorBody attacker)
	{
		if (Alert != AlertState.Alarm)
			return true;
		var toAttacker = attacker.GlobalPosition - GlobalPosition;
		toAttacker.Y = 0f;
		return toAttacker.LengthSquared() < 0.01f
			|| Mathf.RadToDeg(Facing.AngleTo(toAttacker.Normalized())) > 120f;
	}
}
