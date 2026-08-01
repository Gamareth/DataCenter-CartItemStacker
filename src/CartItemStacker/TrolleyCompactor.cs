using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class TrolleyCompactor
{
    private readonly struct ColliderState
    {
        internal readonly Collider Collider;
        internal readonly bool Enabled;

        internal ColliderState(Collider collider, bool enabled)
        {
            Collider = collider;
            Enabled = enabled;
        }
    }

    private const float ColliderRestoreDelay =
        LayoutConstants.GravityAnimationDuration;
    private static readonly Dictionary<System.IntPtr, GameObject> ItemAnchors = new();
    private static readonly List<ColliderState> CompactionColliders = new();
    private static readonly List<UsableObject> CompactionItems = new();
    private static int _movementToken;
    private static int _restoreToken;

    internal static void ResetAnchors()
    {
        RestoreCompactionColliders();
        foreach (var anchor in ItemAnchors.Values)
            DestroyIfEmpty(anchor);
        ItemAnchors.Clear();
    }

    internal static void ReleaseAnchor(UsableObject item)
    {
        if (item is null || !ItemAnchors.TryGetValue(item.Pointer, out var anchor))
            return;

        ItemAnchors.Remove(item.Pointer);
        DestroyIfEmpty(anchor);
    }

    private static void DestroyIfEmpty(GameObject anchor)
    {
        // Never destroy an anchor that still owns an item. Native extraction
        // normally reparents synchronously, but keeping a harmless orphan is
        // safer than deleting the held/stored item if that ever changes.
        if (anchor is not null && anchor.transform.childCount == 0)
            UnityEngine.Object.Destroy(anchor);
    }

    private static void SuppressCompactionColliders(UsableObject item)
    {
        if (item is null)
            return;

        var tracked = false;
        foreach (var existing in CompactionItems)
        {
            if (existing is not null && existing.Pointer == item.Pointer)
            {
                tracked = true;
                break;
            }
        }
        if (!tracked)
            CompactionItems.Add(item);

        foreach (var collider in item.GetComponentsInChildren<Collider>(true))
        {
            if (collider is null)
                continue;
            CompactionColliders.Add(new ColliderState(collider, collider.enabled));
            collider.enabled = false;
        }
    }

    internal static void RestoreCompactionColliders()
    {
        _restoreToken++;
        _movementToken++;
        RestoreCompactionCollidersNow("interrupted by a newer cart operation");
    }

    internal static void ScheduleCompactionColliderRestore()
    {
        if (CompactionColliders.Count == 0 && CompactionItems.Count == 0)
            return;

        var token = ++_restoreToken;
        MelonCoroutines.Start(RestoreCompactionCollidersAfterMove(token));
    }

    private static IEnumerator RestoreCompactionCollidersAfterMove(int token)
    {
        yield return new WaitForSeconds(
            ModSettings.GetAnimationDuration(ColliderRestoreDelay));
        if (token != _restoreToken)
            yield break;

        _restoreToken++;
        _movementToken++;
        RestoreCompactionCollidersNow("animation completed");
    }

    private static void RestoreCompactionCollidersNow(string reason)
    {
        if (CompactionColliders.Count == 0 && CompactionItems.Count == 0)
            return;

        // Commit every compacted item to its authoritative absolute target
        // before restoring collisions. This also makes interruption safe: a
        // newer cart operation completes the previous move first instead of
        // preserving an arbitrary in-flight world pose.
        var parented = 0;
        var normalizedBodies = 0;
        foreach (var item in CompactionItems)
        {
            if (item is null || item.transform is null ||
                !ItemAnchors.TryGetValue(item.Pointer, out var anchor) ||
                anchor is null || anchor.transform is null)
                continue;

            var positionError = Vector3.Distance(
                item.transform.position, anchor.transform.position);
            var rotationError = Quaternion.Angle(
                item.transform.rotation, anchor.transform.rotation);
            StoredCargoMotion.SnapAbsolute(
                item, anchor.transform.position, anchor.transform.rotation);
            item.transform.SetParent(anchor.transform, true);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.storedPosition = item.trolleySlotIndex;
            item.isOnTrolley = true;
            if (StoredCargoPhysics.EnsureOwnKinematicBody(
                    item, "compaction collider restore"))
                normalizedBodies++;
            ModSettings.Debug(
                $"Converged compacted '{item.name}' at slot " +
                $"{item.trolleySlotIndex}: corrected " +
                $"{positionError:0.0000}m and {rotationError:0.00}deg " +
                $"({reason}).");
            parented++;
        }

        var restored = 0;
        foreach (var state in CompactionColliders)
        {
            if (state.Collider is null)
                continue;
            state.Collider.enabled = state.Enabled;
            restored++;
        }
        CompactionColliders.Clear();
        Physics.SyncTransforms();
        foreach (var item in CompactionItems)
        {
            TrolleyItemInteraction.Restore(item, "compaction");
            TrolleyPhysicsIsolation.Reassert(item);
        }
        CompactionItems.Clear();
        ModSettings.Debug(
            $"Converged and bound {parented} compacted item(s) to the trolley and restored " +
            $"{restored} collider(s); normalized {normalizedBodies} " +
            "item-owned kinematic Rigidbody state(s).");
    }

    internal static void MoveUpperItemsBeforeExtraction(
        UsableObject removed,
        int start,
        int removedSize)
    {
        // Recover defensively if a prior native interaction was interrupted.
        RestoreCompactionColliders();

        var bay = TrolleyContext.Current;
        var targets = bay?.positionsOnTrolley;
        var used = bay?.usedPositions;
        if (bay is null || targets is null || used is null ||
            start < 0 || start >= CartLayout.ServerSlots)
            return;

        var split = DynamicTargetAllocator.Split;
        var stackStart = start < split ? 0 : split;
        var stackEnd = System.Math.Min(
            start < split ? split : CartLayout.ServerSlots,
            used.Length);
        var moving = new List<UsableObject>();

        foreach (var item in TrolleyContext.Items)
        {
            if (item is null || !item.isOnTrolley ||
                (removed is not null && item.Pointer == removed.Pointer))
                continue;
            if (BoxLayerLayout.IsBox(item) ||
                ModuleTrayLayout.IsTray(item) ||
                !ServerSectionCatalog.IsAllowed(item, out _))
                continue;

            var slot = item.trolleySlotIndex;
            if (slot < stackStart || slot >= stackEnd)
                continue;

            if (slot >= start + removedSize)
                moving.Add(item);
        }

        // Move the highest item first, then work downward toward the extraction.
        moving.Sort((a, b) => b.trolleySlotIndex.CompareTo(a.trolleySlotIndex));
        var movementToken = ++_movementToken;

        foreach (var item in moving)
        {
            var newSlot = item.trolleySlotIndex - removedSize;
            if (newSlot < stackStart || newSlot >= targets.Length)
                continue;

            // The destination slot is still occupied until the original click
            // extracts the lower item. Use a private bay-owned copy of the
            // authoritative destination pose and animate directly to it. The
            // item is already stored, so invoking native MoveToStorage again
            // would add a second independent tween and a second interpretation
            // of the same storage target.
            var anchorObject = new GameObject(
                $"CartStackerCompactionAnchor_{item.Pointer.ToInt64():X}_{newSlot}");
            anchorObject.transform.SetParent(bay.transform, false);
            DynamicTargetAllocator.ApplyServerTargetPose(
                bay, anchorObject.transform, item, newSlot, false);

            // The destination volume is still occupied until the original
            // InteractOnClick continues and extracts the lower item. Hiding the
            // moving colliders for this synchronous window prevents Unity from
            // resolving that intentional overlap through the trolley body.
            SuppressCompactionColliders(item);
            if (!StoredCargoPhysics.EnsureOwnKinematicBody(
                    item, "pre-extraction compaction"))
                continue;

            // If this item was compacted before, detach it from the old private
            // anchor while preserving world pose so that anchor can be cleaned
            // up before the next deterministic move.
            if (ItemAnchors.TryGetValue(item.Pointer, out var previousAnchor) &&
                previousAnchor is not null)
            {
                item.transform.SetParent(bay.transform, true);
                DestroyIfEmpty(previousAnchor);
            }

            item.trolleySlotIndex = newSlot;
            item.storedPosition = newSlot;
            item.isOnTrolley = true;

            ItemAnchors[item.Pointer] = anchorObject;
            MelonCoroutines.Start(StoredCargoMotion.AnimateAbsolute(
                item,
                anchorObject.transform.position,
                anchorObject.transform.rotation,
                ModSettings.GetAnimationDuration(ColliderRestoreDelay),
                StoredCargoMotion.PositionEasing.SmoothStep,
                StoredCargoMotion.RotationMotion.Interpolate,
                () => movementToken == _movementToken));

            ModSettings.Debug(
                $"Compacted '{item.name}' from slot {newSlot + removedSize} " +
                $"to {newSlot} through deterministic absolute-pose movement " +
                $"(-{removedSize}U).");
        }

        ModSettings.Debug(
            $"Pre-extraction compaction moved {moving.Count} item(s) before removing {removedSize}U at slot {start}.");
    }

    internal static void RebuildReservationsAfterExtraction(UsableObject removed, int start)
    {
        var bay = TrolleyContext.Current;
        var used = bay?.usedPositions;
        if (bay is null || used is null ||
            start < 0 || start >= CartLayout.ServerSlots)
            return;

        var split = DynamicTargetAllocator.Split;
        var stackStart = start < split ? 0 : split;
        var stackEnd = System.Math.Min(
            start < split ? split : CartLayout.ServerSlots,
            used.Length);
        for (var i = stackStart; i < stackEnd; i++)
            used[i] = 0;

        foreach (var item in TrolleyContext.Items)
        {
            if (item is null || !item.isOnTrolley ||
                (removed is not null && item.Pointer == removed.Pointer))
                continue;
            if (BoxLayerLayout.IsBox(item) ||
                ModuleTrayLayout.IsTray(item) ||
                !ServerSectionCatalog.IsAllowed(item, out _))
                continue;

            var slot = item.trolleySlotIndex;
            if (slot < stackStart || slot >= stackEnd)
                continue;

            var size = System.Math.Max(1, item.sizeInU);
            for (var i = slot; i < slot + size && i < stackEnd; i++)
                used[i] = 1;
        }
    }
}
