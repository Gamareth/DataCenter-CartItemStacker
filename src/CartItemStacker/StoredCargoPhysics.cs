using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

/// <summary>
/// Establishes the single supported physics state for cargo stored on the
/// trolley: an item-owned kinematic Rigidbody with no stale momentum.
/// </summary>
internal static class StoredCargoPhysics
{
    internal static bool EnsureOwnKinematicBody(
        UsableObject item,
        string reason)
    {
        if (item?.gameObject is null)
            return false;

        var body = item.GetComponent<Rigidbody>();
        var restored = false;
        if (body is null)
        {
            item.RestoreRigidbody();
            body = item.GetComponent<Rigidbody>();
            restored = body is not null;
        }

        if (body is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not give stored item '{item.name}' its own Rigidbody " +
                $"during {reason}; its colliders will remain suppressed.");
            return false;
        }

        var changedToKinematic = !body.isKinematic;
        if (changedToKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }

        if (restored || changedToKinematic)
        {
            ModSettings.Debug(
                $"Normalized stored cargo physics for '{item.name}' during " +
                $"{reason}: restoredBody={restored}, " +
                $"changedToKinematic={changedToKinematic}.");
        }
        return true;
    }
}
