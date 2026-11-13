using Zero.Ai.Nodes;

namespace Zero.Ai.Actions;

public enum ActionStatus { Running, Succeeded, Failed }

/// <summary>
/// Атомарное действие NPC, растянутое во времени: «дойти до точки», «присесть»,
/// «выстрелить в цель». Действие только командует телом NPC (<see cref="NpcAgent"/>
/// наследует общий слой действий ActorBody), само ничего не двигает.
/// Из таких действий собираются последовательности, а позже — тактики
/// (их же будут вызывать листья дерева поведения или шаги плана GOAP).
/// </summary>
public abstract class NpcAction
{
    /// <summary>Подпись для отладки (над головой NPC).</summary>
    public abstract string Name { get; }

    public virtual void Start(NpcAgent npc) { }

    public abstract ActionStatus Tick(NpcAgent npc, float dt);

    /// <summary>Вызывается при завершении или прерывании — вернуть тело в нейтральное состояние.</summary>
    public virtual void Stop(NpcAgent npc) { }

    public override string ToString() => Name;
}
