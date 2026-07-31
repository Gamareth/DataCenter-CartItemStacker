using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class TrolleyContext
{
    internal static TrolleyLoadingBay Current;
    internal static readonly List<UsableObject> Items = new();
    internal static bool SaveItemsDiscovered;
    internal static bool LayoutEnabled;
    internal static bool PendingDisable;

    internal static void Register(UsableObject item)
    {
        if (item is null)
            return;

        for (var i = 0; i < Items.Count; i++)
        {
            var existing = Items[i];
            if (existing is not null && existing.Pointer == item.Pointer)
            {
                // Generic IL2CPP GetComponent<UsableObject>() wrappers hide
                // SFPBox fields on three of the four tray prefabs. Prefer the
                // derived wrapper when both represent the same native component.
                if (item is SFPBox && existing is not SFPBox)
                    Items[i] = item;
                return;
            }
        }

        Items.Add(item);
    }

    internal static void Unregister(UsableObject item)
    {
        if (item is null)
            return;

        Items.RemoveAll(existing => existing is null || existing.Pointer == item.Pointer);
    }

    internal static bool HasCargo()
    {
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            var item = Items[index];
            if (item is null)
            {
                Items.RemoveAt(index);
                continue;
            }

            try
            {
                if (item.isOnTrolley && !item.objectInHands)
                    return true;
            }
            catch (System.Exception)
            {
                // A stale IL2CPP wrapper cannot prove that the trolley is empty.
                // Fail safe and keep the active layout until a later cart event.
                return true;
            }
        }

        return false;
    }

    internal static void DiscoverSaveItemsOnce(int trolleyStorageUid)
    {
        if (SaveItemsDiscovered || Current is null || Current.positionsOnTrolley is null)
            return;

        SaveItemsDiscovered = true;
        var targets = Current.positionsOnTrolley;
        foreach (var item in UnityEngine.Object.FindObjectsOfType<UsableObject>())
        {
            if (item is null || item.transform is null)
                continue;

            var slot = item.trolleySlotIndex;
            if (slot < 0 || slot >= targets.Length || targets[slot] is null)
                continue;

            if (item.currentRackPosition is null &&
                item.storageUID == trolleyStorageUid)
                Register(item);
        }

        ModSettings.Debug(
            $"Recovered {Items.Count} trolley item(s) for savegame compaction.");
    }
}

internal static class TrolleyPhysicsIsolation
{
    private readonly struct CollisionPair
    {
        internal readonly Collider Item;
        internal readonly Collider Trolley;
        internal readonly bool OwnedByMod;

        internal CollisionPair(Collider item, Collider trolley, bool ownedByMod)
        {
            Item = item;
            Trolley = trolley;
            OwnedByMod = ownedByMod;
        }
    }

    private static TrolleyLoadingBay _bay;
    private static Collider[] _trolleyColliders = System.Array.Empty<Collider>();
    // Track native/pre-existing ignored pairs as well as pairs changed by this
    // mod. Unity forgets IgnoreCollision when a collider is disabled, which the
    // compactor temporarily does. Only mod-owned pairs may be undone when the
    // item is extracted, but every tracked pair must be reasserted afterwards.
    private static readonly Dictionary<System.IntPtr, List<CollisionPair>> TrackedPairs = new();

    internal static void Reset()
    {
        TrackedPairs.Clear();
        _trolleyColliders = System.Array.Empty<Collider>();
        _bay = null;
    }

    internal static void Attach(TrolleyLoadingBay bay)
    {
        _bay = bay;
        TrackedPairs.Clear();
        var trolleyColliders = new List<Collider>();
        var seen = new HashSet<System.IntPtr>();
        var candidates = bay?.transform?.root is null
            ? System.Array.Empty<Collider>()
            : bay.transform.root.GetComponentsInChildren<Collider>(true);
        foreach (var collider in candidates)
        {
            if (collider is null || collider.isTrigger ||
                IsRegisteredCargoCollider(collider) ||
                !seen.Add(collider.Pointer))
                continue;
            trolleyColliders.Add(collider);
        }
        _trolleyColliders = trolleyColliders.ToArray();

        ModSettings.Debug(
            $"Cached {_trolleyColliders.Length} trolley collider(s) for event-driven cargo isolation.");
    }

    internal static void IgnoreStoredItem(UsableObject item)
    {
        if (item is null || item.transform is null || _bay is null)
            return;

        if (TrackedPairs.ContainsKey(item.Pointer))
        {
            Reassert(item);
            return;
        }

        var itemColliders = item.GetComponentsInChildren<Collider>(true);
        var tracked = new List<CollisionPair>();
        var changed = 0;
        var preExisting = 0;
        foreach (var itemCollider in itemColliders)
        {
            if (itemCollider is null || itemCollider.isTrigger)
                continue;

            foreach (var trolleyCollider in _trolleyColliders)
            {
                if (trolleyCollider is null || trolleyCollider.isTrigger ||
                    trolleyCollider.Pointer == itemCollider.Pointer ||
                    trolleyCollider.transform is null ||
                    trolleyCollider.transform.IsChildOf(item.transform))
                    continue;

                try
                {
                    if (Physics.GetIgnoreCollision(itemCollider, trolleyCollider))
                    {
                        preExisting++;
                        tracked.Add(new CollisionPair(
                            itemCollider, trolleyCollider, false));
                        continue;
                    }
                    Physics.IgnoreCollision(itemCollider, trolleyCollider, true);
                    tracked.Add(new CollisionPair(
                        itemCollider, trolleyCollider, true));
                    changed++;
                }
                catch (System.Exception exception)
                {
                    Melon<CartItemStacker.Mod>.Logger.Warning(
                        $"Could not isolate one collider pair for '{item.name}': " +
                        exception.Message);
                }
            }
        }

        TrackedPairs[item.Pointer] = tracked;
        Physics.SyncTransforms();
        ModSettings.Debug(
            $"Tracked {tracked.Count} item-trolley collision pair(s) for " +
            $"'{item.name}' ({changed} changed, {preExisting} pre-existing).");
    }

    internal static void RestoreItem(UsableObject item)
    {
        if (item is null || !TrackedPairs.TryGetValue(item.Pointer, out var pairs))
            return;

        var restored = 0;
        foreach (var pair in pairs)
        {
            try
            {
                if (pair.Item is null || pair.Trolley is null)
                    continue;
                if (!pair.OwnedByMod)
                    continue;
                Physics.IgnoreCollision(pair.Item, pair.Trolley, false);
                restored++;
            }
            catch (System.Exception exception)
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Could not restore one collider pair for '{item.name}': " +
                    exception.Message);
            }
        }
        TrackedPairs.Remove(item.Pointer);
        Physics.SyncTransforms();
        ModSettings.Debug(
            $"Restored {restored} item-trolley collision pair(s) for '{item.name}'.");
    }

    internal static int Reassert(UsableObject item)
    {
        if (item is null || !TrackedPairs.TryGetValue(item.Pointer, out var pairs))
            return 0;

        var reasserted = 0;
        foreach (var pair in pairs)
        {
            try
            {
                if (pair.Item is not null && pair.Trolley is not null)
                {
                    Physics.IgnoreCollision(pair.Item, pair.Trolley, true);
                    reasserted++;
                }
            }
            catch (System.Exception)
            {
                // A destroyed collider will be discarded with its item record.
            }
        }
        return reasserted;
    }

    private static bool IsRegisteredCargoCollider(Collider collider)
    {
        foreach (var cargo in TrolleyContext.Items)
        {
            if (cargo is null || cargo.transform is null)
                continue;
            // A trolley-level UsableObject is an ancestor, not stored cargo.
            if (_bay?.transform is not null &&
                _bay.transform.IsChildOf(cargo.transform))
                continue;
            if (collider.transform.IsChildOf(cargo.transform))
                return true;
        }
        return false;
    }
}

[HarmonyPatch(
    typeof(TrolleyLoadingBay._ParentTheObjectWithDelay_d__10),
    nameof(TrolleyLoadingBay._ParentTheObjectWithDelay_d__10.MoveNext))]
internal static class TrolleyNativeColliderEnablePatch
{
    private static void Postfix(
        TrolleyLoadingBay._ParentTheObjectWithDelay_d__10 __instance,
        bool __result)
    {
        // StartCoroutine runs MoveNext once immediately, while the root
        // collider is still disabled. The second successful MoveNext call
        // parents the item, enables that collider and marks it on-trolley.
        // Reassert in this same call so no FixedUpdate can observe the
        // re-enabled collider with a forgotten IgnoreCollision pair.
        if (!TrolleyContext.LayoutEnabled ||
            __instance is null ||
            !__result ||
            __instance.__1__state != 2)
            return;

        try
        {
            var item = __instance.uo;
            if (item is null || item.objectInHands || !item.isOnTrolley)
                return;

            var rootCollider = item.GetComponent<Collider>();
            if (rootCollider is null || !rootCollider.enabled)
                return;

            var reasserted = TrolleyPhysicsIsolation.Reassert(item);
            ModSettings.Debug(
                $"Native collider-enable hook reasserted {reasserted} trolley " +
                $"pair(s) for '{item.name}' before the next physics step.");
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Native collider-enable hook failed safely: {exception.Message}");
        }
    }
}
