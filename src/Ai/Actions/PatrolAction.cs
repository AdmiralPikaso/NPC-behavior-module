using Zero.Actors;
using Zero.Ai.Core;
using Zero.Ai.Navigation;
using Zero.Ai.Nodes;

namespace Zero.Ai.Actions;

/// <summary>
/// Бесконечный патруль по <see cref="PatrolRoute"/>: дойти до точки, постоять,
/// при необходимости повернуться по направлению точки, перейти к следующей.
/// </summary>
public sealed class PatrolAction : NpcAction
{
    private readonly PatrolRoute _route;
    private readonly PatrolCursor _cursor;
    private bool _waiting;
    private float _waitLeft;

    public PatrolAction(PatrolRoute route, int startIndex)
    {
        _route = route;
        _cursor = new PatrolCursor(route.Points.Count, route.Mode, startIndex);
    }

    public override string Name => _waiting
        ? $"Стоит в точке {_cursor.Current} ({_waitLeft:0.0} с)"
        : $"Патруль → точка {_cursor.Current}";

    public override void Start(NpcAgent npc) => _waiting = false;

    public override ActionStatus Tick(NpcAgent npc, float dt)
    {
        var point = _route.Points[_cursor.Current];
        if (!_waiting)
        {
            if (npc.NavigateTo(point.GlobalPosition, Gait.Walk, 0.5f) == NavResult.Arrived)
            {
                _waiting = true;
                _waitLeft = point.WaitTime;
            }
            return ActionStatus.Running;
        }

        _waitLeft -= dt;
        if (point.FaceDirection)
            npc.AimAt(npc.GlobalPosition + point.FacingDirection * 5f);
        if (_waitLeft <= 0f)
        {
            npc.ClearAim();
            _cursor.Advance();
            _waiting = false;
        }
        return ActionStatus.Running;
    }

    public override void Stop(NpcAgent npc)
    {
        npc.StopMoving();
        npc.ClearAim();
    }
}
