using Godot;

namespace Zero.Combat;

/// <summary>То, что может получать урон от снарядов.</summary>
public interface IDamageable
{
    void TakeDamage(float amount, Node? source);
}
