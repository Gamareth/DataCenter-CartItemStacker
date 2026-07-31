using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class TrolleyItemInteraction
{
    private const float PlacementFinalizeDelay =
        LayoutConstants.NativePlacementDelay;
    private static readonly Dictionary<System.IntPtr, int> PlacementTokens = new();

    internal static void Reset()
    {
        PlacementTokens.Clear();
    }

    internal static void SchedulePlacementFinalize(UsableObject item, string reason)
    {
        if (item is null)
            return;

        var pointer = item.Pointer;
        var token = PlacementTokens.TryGetValue(pointer, out var previousToken)
            ? previousToken + 1
            : 1;
        PlacementTokens[pointer] = token;
        MelonCoroutines.Start(FinalizePlacementAfterNativeDelay(
            item, pointer, token, reason));
    }

    private static IEnumerator FinalizePlacementAfterNativeDelay(
        UsableObject item,
        System.IntPtr pointer,
        int token,
        string reason)
    {
        if (ModSettings.DebugLogging)
        {
            // Capture one point during the native LeanTween path only when
            // detailed diagnostics are explicitly enabled.
            yield return new WaitForSeconds(0.25f);

            if (!PlacementTokens.TryGetValue(pointer, out var midpointToken) ||
                midpointToken != token)
                yield break;

            if (item is not null)
                PlacementPoseDiagnostics.LogItem(
                    "native-flight t=0.25s",
                    TrolleyContext.Current,
                    item);

            yield return new WaitForSeconds(PlacementFinalizeDelay - 0.25f);
        }
        else
        {
            // TrolleyLoadingBay enables the root collider in its own one-shot
            // coroutine after 0.5s. Finalize just after the native delay.
            yield return new WaitForSeconds(PlacementFinalizeDelay);
        }

        if (!PlacementTokens.TryGetValue(pointer, out var currentToken) ||
            currentToken != token)
            yield break;

        if (item is null || item.objectInHands || !item.isOnTrolley)
        {
            PlacementTokens.Remove(pointer);
            yield break;
        }

        try
        {
            var rootCollider = item.GetComponent<Collider>();
            var rootWasEnabled = rootCollider is not null && rootCollider.enabled;
            if (rootCollider is not null && !rootCollider.enabled)
                rootCollider.enabled = true;

            var previousTag = item.gameObject.tag;
            var previousLayer = item.gameObject.layer;
            Restore(item, reason);
            PlacementPoseDiagnostics.LogItem(
                "finalize pre-correction t=0.65s",
                TrolleyContext.Current,
                item);
            if (PatchPanelLayerLayout.IsPatchPanel(item))
                PatchPanelLayerLayout.NormalizeStoredPose(
                    TrolleyContext.Current, item);
            Physics.SyncTransforms();
            PlacementPoseDiagnostics.LogItem(
                "finalize post-correction t=0.65s",
                TrolleyContext.Current,
                item);
            var reasserted = TrolleyPhysicsIsolation.Reassert(item);

            var colliderCount = 0;
            var enabledCount = 0;
            foreach (var collider in item.GetComponentsInChildren<Collider>(true))
            {
                if (collider is null)
                    continue;
                colliderCount++;
                if (collider.enabled)
                    enabledCount++;
            }

            ModSettings.Debug(
                $"Finalized stored '{item.name}' after native delay ({reason}): " +
                $"tag '{previousTag}'->'{item.gameObject.tag}', layer " +
                $"{previousLayer}->{item.gameObject.layer}, root collider " +
                $"{rootWasEnabled}->{(rootCollider is not null && rootCollider.enabled)}, " +
                $"enabled colliders {enabledCount}/{colliderCount}, " +
                $"reasserted {reasserted} trolley pair(s).");

        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not finalize stored item after native delay: {exception.Message}");
        }
        finally
        {
            if (PlacementTokens.TryGetValue(pointer, out var finalToken) &&
                finalToken == token)
                PlacementTokens.Remove(pointer);
        }
    }

    internal static void Restore(UsableObject item, string reason)
    {
        var gameObject = item?.gameObject;
        if (gameObject is null)
            return;

        var previousTag = gameObject.tag;
        var previousLayer = gameObject.layer;
        gameObject.tag = "Interact";
        gameObject.layer = 0;

        if (previousTag != "Interact" || previousLayer != 0)
        {
            ModSettings.Debug(
                $"Restored interaction root for '{item.name}' after {reason}: " +
                $"tag '{previousTag}'->'Interact', layer {previousLayer}->0.");
        }
    }
}

internal static class PlacementPoseDiagnostics
{
    private static bool IsRelevant(UsableObject item) =>
        PatchPanelLayerLayout.IsPatchPanel(item) ||
        BoxLayerLayout.IsBox(item);

    internal static void LogItem(
        string stage,
        TrolleyLoadingBay bay,
        UsableObject item)
    {
        if (!ModSettings.DebugLogging ||
            bay?.transform is null ||
            item?.transform is null ||
            !IsRelevant(item))
            return;

        var rootPosition =
            bay.transform.InverseTransformPoint(item.transform.position);
        var rootRotation =
            (Quaternion.Inverse(bay.transform.rotation) *
                item.transform.rotation).eulerAngles;
        var target = GetAssignedTarget(bay, item);
        var targetSummary = target is null
            ? "<none>"
            : DescribeTransform(bay, target);

        var boundsSummary = "<none>";
        if (PatchPanelLayerLayout.TryGetWorldBounds(item, out var bounds))
        {
            var boundsCenter =
                bay.transform.InverseTransformPoint(bounds.center);
            boundsSummary =
                $"center=({boundsCenter.x:0.000},{boundsCenter.y:0.000}," +
                $"{boundsCenter.z:0.000}) size=({bounds.size.x:0.000}," +
                $"{bounds.size.y:0.000},{bounds.size.z:0.000})";
        }

        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"POSE {stage} '{item.name}': root local " +
            $"({rootPosition.x:0.000},{rootPosition.y:0.000}," +
            $"{rootPosition.z:0.000}) rot " +
            $"({rootRotation.x:0.0},{rootRotation.y:0.0}," +
            $"{rootRotation.z:0.0}); bounds {boundsSummary}; " +
            $"metadata slot={item.trolleySlotIndex}, stored={item.storedPosition}, " +
            $"inHands={item.objectInHands}, onTrolley={item.isOnTrolley}; " +
            $"assigned target {targetSummary}.");
    }

    internal static void LogMoveTarget(
        string stage,
        TrolleyLoadingBay bay,
        UsableObject item,
        Transform target,
        int targetIndex)
    {
        if (!ModSettings.DebugLogging ||
            bay?.transform is null ||
            item?.transform is null ||
            !IsRelevant(item))
            return;

        var itemPosition =
            bay.transform.InverseTransformPoint(item.transform.position);
        var targetPosition = target is null
            ? Vector3.zero
            : bay.transform.InverseTransformPoint(target.position);
        var delta = target is null
            ? Vector3.zero
            : targetPosition - itemPosition;
        var temporary = bay.temporaryTransformToStoreInCorrectSpot;

        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"POSE {stage} '{item.name}': MoveToStorage index={targetIndex}, " +
            $"root local ({itemPosition.x:0.000},{itemPosition.y:0.000}," +
            $"{itemPosition.z:0.000}); target " +
            $"{(target is null ? "<none>" : DescribeTransform(bay, target))}; " +
            $"root-to-target delta ({delta.x:0.000},{delta.y:0.000}," +
            $"{delta.z:0.000}); temporary target " +
            $"{(temporary is null ? "<none>" : DescribeTransform(bay, temporary))}.");
    }

    private static Transform GetAssignedTarget(
        TrolleyLoadingBay bay,
        UsableObject item)
    {
        var targets = bay?.positionsOnTrolley;
        var index = item?.trolleySlotIndex ?? -1;
        if (targets is null || index < 0 || index >= targets.Length)
            return null;
        return targets[index];
    }

    private static string DescribeTransform(
        TrolleyLoadingBay bay,
        Transform target)
    {
        var localPosition =
            bay.transform.InverseTransformPoint(target.position);
        var localRotation =
            (Quaternion.Inverse(bay.transform.rotation) *
                target.rotation).eulerAngles;
        return
            $"ptr=0x{target.Pointer.ToInt64():X} local " +
            $"({localPosition.x:0.000},{localPosition.y:0.000}," +
            $"{localPosition.z:0.000}) rot " +
            $"({localRotation.x:0.0},{localRotation.y:0.0}," +
            $"{localRotation.z:0.0})";
    }
}

[HarmonyPatch(typeof(UsableObject), nameof(UsableObject.MoveToStorage))]
internal static class TrolleyStackBoundaryPatch
{
    private const int SlotsPerStack = CartLayout.SlotsPerStack;

    private static void Prefix(
        UsableObject __instance,
        ref Transform _pos,
        ref int _positionIndex,
        int _storageUid)
    {
        var bay = TrolleyContext.Current;
        if (!TrolleyContext.LayoutEnabled ||
            __instance is null ||
            bay is null ||
            _pos is null)
            return;

        var size = System.Math.Max(1, __instance.sizeInU);
        var targets = bay.positionsOnTrolley;
        if (targets is null)
            return;

        PlacementPoseDiagnostics.LogMoveTarget(
            "MoveToStorage input",
            bay,
            __instance,
            _pos,
            _positionIndex);

        if (PatchPanelLayerLayout.TryGetPendingNativeTarget(
            bay,
            __instance,
            out var patchSlot,
            out var patchTarget))
        {
            // TrolleyLoadingBay restores an already-used slot transform to its
            // native pose after our click Prefix. The first panel therefore
            // received the prepared patch pose, while panels 2+ animated to
            // the default trolley slot and were only corrected at finalize.
            // Reapply the authoritative pose at the final MoveToStorage
            // boundary so every panel's LeanTween starts with the right target.
            PatchPanelLayerLayout.ApplyPose(
                bay,
                patchTarget,
                __instance);
            _positionIndex = patchSlot;
            _pos = patchTarget;
            __instance.trolleySlotIndex = patchSlot;
            bay.temporaryTransformToStoreInCorrectSpot = patchTarget;
            ModSettings.Debug(
                $"Forced patch-panel native animation to logical slot " +
                $"{patchSlot}.");
        }
        else if (CableWheelLayout.TryGetPendingNativeTarget(
            bay,
            __instance,
            out var cableSlot,
            out var cableTarget))
        {
            // TrolleyLoadingBay can restore the selected slot to its native
            // pose after our click Prefix. Reapply the cable pose here so the
            // actual MoveToStorage animation receives the flat-wheel target.
            CableWheelLayout.ApplyTargetPose(
                bay,
                cableTarget,
                cableSlot);
            _positionIndex = cableSlot;
            _pos = cableTarget;
            __instance.trolleySlotIndex = cableSlot;
            bay.temporaryTransformToStoreInCorrectSpot = cableTarget;
            ModSettings.Debug(
                $"Forced cable-wheel native animation to logical slot " +
                $"{cableSlot}.");
        }

        PlacementPoseDiagnostics.LogMoveTarget(
            "MoveToStorage effective",
            bay,
            __instance,
            _pos,
            _positionIndex);

        var targetSlot = FindTargetIndex(targets, _pos);
        if (targetSlot < 0)
            return;

        ModSettings.Debug(
            $"Trolley storage candidate: target slot {targetSlot}, size {size}U.");

        // Native allocation treats all slots as one row. Redirect an item
        // whose reservation straddles the boundary between our two stacks.
        if (targetSlot >= SlotsPerStack || targetSlot + size <= SlotsPerStack)
            return;

        var used = bay.usedPositions;
        if (used is null || targetSlot + size > used.Length)
            return;

        var newStart = FindFreeRange(
            used, SlotsPerStack, CartLayout.ServerSlots, size);
        if (newStart < 0 || newStart >= targets.Length)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not redirect {size}U item across stack boundary.");
            return;
        }

        for (var i = targetSlot; i < targetSlot + size; i++)
            used[i] = 0;
        for (var i = newStart; i < newStart + size; i++)
            used[i] = 1;

        _positionIndex = newStart;
        _pos = targets[newStart];
        __instance.trolleySlotIndex = newStart;
        bay.temporaryTransformToStoreInCorrectSpot = targets[newStart];

        ModSettings.Debug(
            $"Redirected {size}U item from slot {targetSlot} to slot {newStart}.");
    }

    private static int FindTargetIndex(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform> targets,
        Transform selected)
    {
        if (selected is null)
            return -1;

        for (var i = 0; i < targets.Length; i++)
        {
            var candidate = targets[i];
            if (candidate is not null && candidate.Pointer == selected.Pointer)
                return i;
        }

        return -1;
    }

    private static int FindFreeRange(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int first,
        int endExclusive,
        int size)
    {
        var end = System.Math.Min(endExclusive, used.Length);
        for (var start = first; start + size <= end; start++)
        {
            var free = true;
            for (var i = start; i < start + size; i++)
            {
                if (used[i] != 0)
                {
                    free = false;
                    break;
                }
            }

            if (free)
                return start;
        }

        return -1;
    }
}
