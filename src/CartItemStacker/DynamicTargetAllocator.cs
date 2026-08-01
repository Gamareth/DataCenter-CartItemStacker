using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class DynamicTargetAllocator
{
    private const float BaseY = LayoutConstants.EquipmentBaseY;
    private const float U = LayoutConstants.UnitHeight;
    // BaseY was calibrated with the placement anchor of a 3U server resting on
    // the trolley. Data Center's horizontal server pose is top-anchored: the
    // target for an item starting at `level` must therefore represent
    // `level + size`. Relative to the calibrated 3U item this requires the full
    // size difference, not half of it.
    private const float CalibratedItemSizeU = 3f;
    private const int NominalBoundary = CartLayout.SlotsPerStack;
    private static int _split = NominalBoundary;
    private static int _pendingRackStack = -1;
    private static int _pendingRackSize;
    private static int _pendingRackStart = -1;
    private static UsableObject _pendingRackItem;
    private static bool _pendingFirstServerClearance;

    internal static int Split => _split;

    internal static bool PrepareForClick(TrolleyLoadingBay bay)
    {
        if (bay is null || bay.positionsOnTrolley is null || bay.usedPositions is null)
            return true;

        var held = GetHeldObject();
        if (held is null)
            return true;

        CancelPendingPlacement(bay, "new trolley click");

        if (ModuleTrayLayout.TryGetTray(held, out var moduleTray))
            return ModuleTrayLayout.PreparePlacement(bay, moduleTray);

        if (PatchPanelLayerLayout.IsPatchPanel(held))
            return PatchPanelLayerLayout.PreparePlacement(bay, held);

        if (CableWheelLayout.TryGetCableWheel(held, out var cableWheel))
            return CableWheelLayout.PreparePlacement(bay, cableWheel);

        if (!ServerSectionCatalog.IsAllowed(held, out var record))
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Rejected non-server-section item '{held.name}' (record='{record}').");
            return false;
        }

        var wasEmpty = IsTrolleyEmpty(bay, held);
        TrolleyContext.Register(held);

        var boxedRack = BoxLayerLayout.IsBox(held);
        if (boxedRack && !BoxLayerLayout.PrepareNativeReservation(bay, held))
        {
            held.sizeInU = CartLayout.NativeBoxSizeU;
            TrolleyContext.Unregister(held);
            return false;
        }

        var size = boxedRack ? 0 : System.Math.Max(1, held.sizeInU);
        var plannedStart = -1;
        if (!boxedRack)
        {
            if (!BoxLayerLayout.PrepareRackInsertion(
                bay, size, out _pendingRackStack, out plannedStart))
            {
                var stack1 = BoxLayerLayout.GetOccupiedHeightU(0);
                var stack2 = BoxLayerLayout.GetOccupiedHeightU(1);
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"No contiguous {size}U space: stack 1 is " +
                    $"{stack1}/{ModSettings.EquipmentStackMaxUnits}U and stack 2 is " +
                    $"{stack2}/{ModSettings.EquipmentStackMaxUnits}U.");
                TrolleyContext.Unregister(held);
                ClearPendingRack();
                return false;
            }
            _pendingRackSize = size;
            _pendingRackStart = plannedStart;
            _pendingRackItem = held;
        }

        var start = boxedRack
            ? FindFirstFree(bay.usedPositions, size)
            : plannedStart;
        if (!boxedRack && !RangeIsFree(
            bay.usedPositions, plannedStart, size, _pendingRackStack))
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Planned {size}U range at slot {plannedStart} on stack " +
                $"{_pendingRackStack + 1} is not free; placement cancelled.");
            BoxLayerLayout.RebuildLayerReservations(bay);
            TrolleyContext.Unregister(held);
            ClearPendingRack();
            return false;
        }
        if (start >= 0 && start < bay.positionsOnTrolley.Length)
        {
            var applyFirstServerClearance =
                wasEmpty &&
                !boxedRack &&
                size == 3 &&
                plannedStart == 0 &&
                record.StartsWith(
                    "ShopItemSO_Server_",
                    System.StringComparison.OrdinalIgnoreCase);
            CartPoseResolver.ApplyLayoutAnchorPose(
                bay,
                held,
                bay.positionsOnTrolley[start]);
            if (applyFirstServerClearance)
            {
                _pendingFirstServerClearance = true;
                bay.positionsOnTrolley[start].position +=
                    bay.transform.TransformVector(new Vector3(0f, 0.005f, 0f));
                ModSettings.Debug(
                    $"Applied the calibrated 0.005m first-server clearance to " +
                    $"'{held.name}'.");
            }
        }

        if (!boxedRack)
            BlockEarlierStacksForNativeScan(
                bay.usedPositions, _pendingRackStack);
        return true;
    }

    private static bool IsTrolleyEmpty(
        TrolleyLoadingBay bay,
        UsableObject held)
    {
        if (bay?.usedPositions is null)
            return false;

        for (var i = 0; i < bay.usedPositions.Length; i++)
            if (bay.usedPositions[i] != 0)
                return false;

        foreach (var item in TrolleyContext.Items)
        {
            if (item is null ||
                (held is not null && item.Pointer == held.Pointer) ||
                item.objectInHands)
                continue;
            if (item.isOnTrolley)
                return false;
        }

        return true;
    }

    internal static void CompleteClick(TrolleyLoadingBay bay)
    {
        if (ModuleTrayLayout.HasPendingPlacement)
        {
            ModuleTrayLayout.CompletePlacement(bay);
            return;
        }

        if (PatchPanelLayerLayout.HasPendingPlacement)
        {
            PatchPanelLayerLayout.CompletePlacement(bay);
            return;
        }

        if (CableWheelLayout.HasPendingPlacement)
        {
            CableWheelLayout.CompletePlacement(bay);
            return;
        }

        if (BoxLayerLayout.HasPendingPlacement)
        {
            BoxLayerLayout.CompletePlacement(bay);
            return;
        }

        if (_pendingRackStack < 0)
            return;

        var succeeded = GetHeldObject() is null;
        if (succeeded)
        {
            // Native TrolleyLoadingBay scans all U positions as one linear
            // range. Normalize the item to the stack-aware start that we
            // planned, even if native metadata selected a crossing slot.
            if (_pendingRackItem is not null && _pendingRackStart >= 0)
            {
                _pendingRackItem.trolleySlotIndex = _pendingRackStart;
                _pendingRackItem.storedPosition = _pendingRackStart;
                _pendingRackItem.isOnTrolley = true;
                ModSettings.Debug(
                    $"Normalized '{_pendingRackItem.name}' to planned slot " +
                    $"{_pendingRackStart} on stack {_pendingRackStack + 1}.");
            }

            BoxLayerLayout.ShiftBoxesVertically(
                bay, _pendingRackStack, _pendingRackSize);
        }
        else if (_pendingRackItem is not null)
        {
            PatchPanelLayerLayout.ShiftPanelsVertically(
                bay, _pendingRackStack, -_pendingRackSize);
            TrolleyContext.Unregister(_pendingRackItem);
        }
        BoxLayerLayout.RebuildLayerReservations(bay);
        ModSettings.Debug(
            succeeded
                ? $"Mixed rack placement completed on stack {_pendingRackStack + 1}."
                : "Mixed rack placement failed; restored box reservations.");
        ClearPendingRack();
    }

    internal static void Reset()
    {
        _split = NominalBoundary;
        ClearPendingRack();
    }

    internal static void CancelPendingPlacement(
        TrolleyLoadingBay bay,
        string reason)
    {
        var cancelled = false;
        cancelled |= ModuleTrayLayout.CancelPendingPlacement(bay);
        cancelled |= PatchPanelLayerLayout.CancelPendingPlacement(bay);
        cancelled |= CableWheelLayout.CancelPendingPlacement(bay);
        cancelled |= BoxLayerLayout.CancelPendingPlacement(bay);

        if (_pendingRackStack >= 0)
        {
            if (_pendingRackItem is not null)
            {
                PatchPanelLayerLayout.ShiftPanelsVertically(
                    bay,
                    _pendingRackStack,
                    -_pendingRackSize);
                TrolleyContext.Unregister(_pendingRackItem);
            }
            ClearPendingRack();
            BoxLayerLayout.RebuildLayerReservations(bay);
            cancelled = true;
        }

        if (cancelled)
            ModSettings.Debug(
                $"Cancelled pending trolley placement state ({reason}).");
    }

    private static void ClearPendingRack()
    {
        _pendingRackStack = -1;
        _pendingRackSize = 0;
        _pendingRackStart = -1;
        _pendingRackItem = null;
        _pendingFirstServerClearance = false;
    }

    internal static bool TryGetPendingEquipmentPose(
        UsableObject item,
        out int slot,
        out float extraLocalY)
    {
        slot = -1;
        extraLocalY = 0f;
        if (item is null ||
            _pendingRackItem is null ||
            item.Pointer != _pendingRackItem.Pointer ||
            _pendingRackStart < 0)
            return false;

        slot = _pendingRackStart;
        extraLocalY = _pendingFirstServerClearance ? 0.005f : 0f;
        return true;
    }

    private static void BlockEarlierStacksForNativeScan(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int plannedStack)
    {
        if (used is null || plannedStack <= 0)
            return;

        var blocked = 0;
        var end = System.Math.Min(plannedStack * _split, used.Length);
        for (var i = 0; i < end; i++)
        {
            if (used[i] != 0)
                continue;
            used[i] = 1;
            blocked++;
        }

        if (blocked > 0)
            ModSettings.Debug(
                $"Temporarily blocked {blocked} tail slot(s) so native " +
                $"allocation starts on stack {plannedStack + 1}.");
    }

    private static bool RangeIsFree(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int start,
        int size,
        int stack)
    {
        if (used is null || stack < 0 || start < 0 || size < 0)
            return false;
        var stackStart = stack * _split;
        var stackEnd = System.Math.Min(stackStart + _split, used.Length);
        if (start < stackStart || start + size > stackEnd)
            return false;
        for (var i = start; i < start + size; i++)
            if (used[i] != 0)
                return false;
        return true;
    }

    internal static void ApplyServerTargetPose(
        TrolleyLoadingBay bay,
        Transform target,
        UsableObject item,
        int targetIndex,
        bool logDetails)
    {
        if (bay is null || target is null || item is null ||
            targetIndex < 0 || targetIndex >= CartLayout.ServerSlots)
            return;

        var second = targetIndex >= _split;
        var serverStack = second ? 1 : 0;
        var level = second ? targetIndex - _split : targetIndex;
        var z = CartLayout.GetStackZ(serverStack);
        var size = System.Math.Max(1, item.sizeInU);
        var anchorOffsetU = size - CalibratedItemSizeU;
        var localPosition = new Vector3(
            0f,
            BaseY + (level + anchorOffsetU) * U,
            z);
        var localRotation = Quaternion.Euler(
            0f, CartLayout.GetServerYaw(serverStack), 0f);
        var projectedCenterX = 0f;
        if (PatchPanelLayerLayout.TryGetProjectedModelBounds(
            item,
            localRotation,
            out var projectedCenter,
            out _))
        {
            // A 180-degree turn exposes the server prefabs' off-center root:
            // X=0 alone therefore gives unequal faceplate/backplate margins.
            // Center the visible body while leaving the calibrated stack Z,
            // height, spacing and rotation untouched.
            projectedCenterX = projectedCenter.x;
            localPosition.x -= projectedCenterX;
            if (Mathf.Abs(projectedCenterX) > 0.0001f)
            {
                localPosition.x +=
                    Mathf.Sign(projectedCenterX) *
                    CartLayout.ServerVisualAlignmentTrimX;
            }
        }
        target.SetPositionAndRotation(
            bay.transform.TransformPoint(localPosition),
            bay.transform.rotation * localRotation);

        if (logDetails)
        {
            ModSettings.Debug(
                $"Size-aware target: '{item.name}', {size}U, slot {targetIndex}, " +
                $"level {level}, anchor offset {anchorOffsetU:0.##}U, " +
                $"top boundary {level + size}U, model center X " +
                $"{projectedCenterX:0.000}m, target X {localPosition.x:0.000}m.");
        }
    }

    internal static UsableObject GetHeldObject()
    {
        var player = PlayerManager.instance;
        var hand = player?.objectInHandGO;
        if (hand is null)
            return null;

        for (var i = 0; i < hand.Length; i++)
        {
            GameObject gameObject;
            try
            {
                gameObject = hand[i];
            }
            catch (System.Exception)
            {
                // An IL2CPP inventory slot can become invalid after its held
                // object has been stored or destroyed. Ignore that stale slot.
                continue;
            }

            // A destroyed IL2CPP Unity object can retain a managed wrapper
            // while its native pointer has already been cleared. Do not use
            // Unity's overloaded equality here: this helper is referenced by
            // a Harmony prefix that is JIT-compiled during game startup.
            if (gameObject is null ||
                gameObject.Pointer == System.IntPtr.Zero)
                continue;

            try
            {
                var usable = gameObject.GetComponent<UsableObject>();
                if (usable is not null &&
                    usable.Pointer != System.IntPtr.Zero &&
                    usable.objectInHands)
                    return usable;
            }
            catch (System.Exception)
            {
                // A destroyed wrapper can also fail inside GetComponent.
                // Skipping it lets the game's native click interaction run.
            }
        }

        return null;
    }

    private static int FindFirstFree(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int size)
    {
        if (used is null || size < 0)
            return -1;

        // Each pile is an independent vertical range. A free run at the end of
        // A free run at the end of pile 1 may never continue into pile 2.
        for (var stack = 0; stack < CartLayout.StackCount; stack++)
        {
            var stackStart = stack * _split;
            var stackEnd = System.Math.Min(stackStart + _split, used.Length);
            if (stackStart >= used.Length)
                break;

            for (var start = stackStart; start + size <= stackEnd; start++)
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
        }

        return -1;
    }

    internal static int FindFirstFreePublic(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int size) => FindFirstFree(used, size);

    private static void ApplySplit(TrolleyLoadingBay bay, int split)
    {
        var targets = bay.positionsOnTrolley;
        var serverTargetCount =
            System.Math.Min(CartLayout.ServerSlots, targets.Length);
        for (var index = 0; index < serverTargetCount; index++)
        {
            var target = targets[index];
            if (target is null)
                continue;

            var second = index >= split;
            var stack = second ? 1 : 0;
            var level = second ? index - split : index;
            var z = CartLayout.GetStackZ(stack);
            var localPosition = new Vector3(
                0f, BaseY + level * U, z);
            var localRotation = Quaternion.Euler(
                0f, CartLayout.GetServerYaw(stack), 0f);
            target.SetPositionAndRotation(
                bay.transform.TransformPoint(localPosition),
                bay.transform.rotation * localRotation);
        }
    }
}
