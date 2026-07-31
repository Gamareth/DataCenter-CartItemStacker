using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class BoxLayerLayout
{
    private readonly struct BoxSlot
    {
        internal readonly int Stack;
        internal readonly int Ordinal;
        internal BoxSlot(int stack, int ordinal)
        {
            Stack = stack;
            Ordinal = ordinal;
        }
    }

    private const float U = LayoutConstants.UnitHeight;
    private const float BoxDeckY = 0.4100f;
    private const float GravityMoveDuration =
        LayoutConstants.GravityAnimationDuration;
    private const float HandleStackBoxZOffset = -0.075f;
    private const float FrontStackBoxZOffset = -0.045f;
    private static readonly float[] ColumnZ = { -0.17f, 0f, 0.17f };
    private static readonly Dictionary<System.IntPtr, BoxSlot> Slots = new();
    private static UsableObject _pendingBox;
    private static int _pendingPatchShiftStack = -1;
    private static int _pendingPatchShiftU;
    private static int _gravityMovementToken;

    internal static bool IsBox(UsableObject item) =>
        item is not null && (item.name ?? string.Empty).StartsWith(
            "BoxedRack", System.StringComparison.OrdinalIgnoreCase);

    private static float GetBoxStackZ(int stack) =>
        CartLayout.GetStackZ(stack) +
        (stack == 0 ? HandleStackBoxZOffset : FrontStackBoxZOffset);

    internal static int ExistingBoxCount(UsableObject exclude = null)
    {
        var count = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (!IsBox(item) || item.objectInHands)
                continue;
            if (exclude is not null && item.Pointer == exclude.Pointer)
                continue;
            count++;
        }
        return count;
    }

    internal static int GetItemStack(UsableObject item)
    {
        if (item is null)
            return -1;
        return Slots.TryGetValue(item.Pointer, out var slot)
            ? slot.Stack
            : (item.trolleySlotIndex >= CartLayout.SlotsPerStack ? 1 : 0);
    }

    internal static int GetStackOrdinal(UsableObject item, int stack)
    {
        if (item is null || stack < 0)
            return -1;
        if (Slots.TryGetValue(item.Pointer, out var known))
            return known.Ordinal;
        var row = System.Math.Max(0,
            (item.trolleySlotIndex - stack * CartLayout.SlotsPerStack -
                GetServerHeight(stack)) / CartLayout.BoxLayerU);
        var local = TrolleyContext.Current.transform.InverseTransformPoint(item.transform.position);
        var baseZ = GetBoxStackZ(stack);
        var column = 0;
        var distance = float.PositiveInfinity;
        for (var i = 0; i < ColumnZ.Length; i++)
        {
            var candidate = System.Math.Abs(
                local.z - (baseZ + ColumnZ[i]));
            if (candidate < distance)
            {
                distance = candidate;
                column = i;
            }
        }
        return row * CartLayout.BoxesPerLayer + column;
    }

    internal static void ForgetBox(UsableObject item)
    {
        if (item is not null)
            Slots.Remove(item.Pointer);
    }

    internal static bool HasPendingPlacement => _pendingBox is not null;

    internal static void Reset()
    {
        Slots.Clear();
        _pendingBox = null;
        _pendingPatchShiftStack = -1;
        _pendingPatchShiftU = 0;
        _gravityMovementToken++;
    }

    internal static void RehydrateLoaded(
        TrolleyLoadingBay bay,
        IEnumerable<UsableObject> loadedItems)
    {
        Slots.Clear();
        if (bay?.transform is null || loadedItems is null)
            return;

        var byStack = new[]
        {
            new List<UsableObject>(),
            new List<UsableObject>(),
        };
        foreach (var item in loadedItems)
        {
            if (!IsBox(item) || item?.transform is null)
                continue;

            var local = bay.transform.InverseTransformPoint(item.transform.position);
            var stack = System.Math.Abs(local.z - GetBoxStackZ(0)) <=
                System.Math.Abs(local.z - GetBoxStackZ(1))
                ? 0
                : 1;
            byStack[stack].Add(item);
        }

        for (var stack = 0; stack < byStack.Length; stack++)
        {
            byStack[stack].Sort((left, right) =>
            {
                var leftLocal = bay.transform.InverseTransformPoint(
                    left.transform.position);
                var rightLocal = bay.transform.InverseTransformPoint(
                    right.transform.position);
                var vertical = leftLocal.y.CompareTo(rightLocal.y);
                return vertical != 0
                    ? vertical
                    : leftLocal.z.CompareTo(rightLocal.z);
            });

            for (var ordinal = 0; ordinal < byStack[stack].Count; ordinal++)
            {
                var box = byStack[stack][ordinal];
                Slots[box.Pointer] = new BoxSlot(stack, ordinal);
                box.sizeInU = 0;
            }
        }
    }

    internal static bool PrepareNativeReservation(
        TrolleyLoadingBay bay,
        UsableObject box)
    {
        _pendingBox = null;
        _pendingPatchShiftStack = -1;
        _pendingPatchShiftU = 0;
        // Fill every existing virtual layer before consuming another box row.
        var freeStack = -1;
        var freeRow = -1;
        var freeColumn = -1;
        var freeStackHeight = int.MaxValue;
        for (var globalLayer = 0; globalLayer < CartLayout.TotalBoxLayers; globalLayer++)
        {
            var stack = globalLayer % CartLayout.StackCount;
            var row = globalLayer / CartLayout.StackCount;
            var occupied = new bool[CartLayout.BoxesPerLayer];
            var layerExists = false;
            foreach (var item in TrolleyContext.Items)
            {
                if (!IsBox(item) || item.objectInHands || item.Pointer == box.Pointer ||
                    !Slots.TryGetValue(item.Pointer, out var slot) ||
                    slot.Stack != stack ||
                    slot.Ordinal / CartLayout.BoxesPerLayer != row)
                    continue;
                layerExists = true;
                occupied[slot.Ordinal % CartLayout.BoxesPerLayer] = true;
            }

            if (!layerExists)
                continue;
            for (var column = 0; column < CartLayout.BoxesPerLayer; column++)
            {
                if (occupied[column])
                    continue;

                var stackHeight = GetOccupiedHeightU(stack);
                if (stackHeight < freeStackHeight)
                {
                    freeStack = stack;
                    freeRow = row;
                    freeColumn = column;
                    freeStackHeight = stackHeight;
                }
                break;
            }
        }

        if (freeStack >= 0)
        {
            Slots[box.Pointer] = new BoxSlot(
                freeStack,
                freeRow * CartLayout.BoxesPerLayer + freeColumn);
            box.sizeInU = 0;
            _pendingBox = box;
            ModSettings.Debug(
                $"Using free box slot on lowest stack {freeStack + 1}: " +
                $"row {freeRow}, column {freeColumn}, " +
                $"height {freeStackHeight}U.");
            return true;
        }

        // No existing boxslot is free: add one box row to the currently lowest
        // stack. Equal heights deliberately prefer stack 1.
        var selectedStack = -1;
        var selectedRow = -1;
        var selectedStart = -1;
        var selectedHeight = int.MaxValue;
        for (var stack = 0; stack < CartLayout.StackCount; stack++)
        {
            var row = GetBoxRowCount(stack);
            if (row >= CartLayout.BoxRowsPerStack)
                continue;

            var stackStart = stack * CartLayout.SlotsPerStack;
            var patchU = PatchPanelLayerLayout.GetOccupiedHeightU(stack);
            var stackHeight = GetServerHeight(stack) +
                row * CartLayout.BoxLayerU;
            var totalHeight = stackHeight + patchU;
            var start = stackStart + stackHeight;
            var stackEnd = stackStart + CartLayout.SlotsPerStack;
            if (start < stackStart ||
                !CapacityRules.CanAddEquipmentHeight(
                    totalHeight,
                    CartLayout.BoxLayerU,
                    ModSettings.EquipmentStackMaxUnits))
                continue;

            if (totalHeight >= selectedHeight)
                continue;
            selectedStack = stack;
            selectedRow = row;
            selectedStart = start;
            selectedHeight = totalHeight;
        }

        if (selectedStack >= 0)
        {
            var oldPatchStart =
                selectedStack * CartLayout.SlotsPerStack +
                GetServerHeight(selectedStack) +
                selectedRow * CartLayout.BoxLayerU;
            var patchU =
                PatchPanelLayerLayout.GetOccupiedHeightU(selectedStack);
            for (var i = oldPatchStart;
                i < oldPatchStart + patchU &&
                i < bay.usedPositions.Length;
                i++)
                bay.usedPositions[i] = 0;
            for (var i = selectedStart;
                i < selectedStart + CartLayout.BoxLayerU;
                i++)
                bay.usedPositions[i] = 1;
            for (var i = oldPatchStart + CartLayout.BoxLayerU;
                i < oldPatchStart + CartLayout.BoxLayerU + patchU &&
                i < bay.usedPositions.Length;
                i++)
                bay.usedPositions[i] = 1;
            Slots[box.Pointer] = new BoxSlot(
                selectedStack, selectedRow * CartLayout.BoxesPerLayer);
            box.sizeInU = 0;
            PatchPanelLayerLayout.ShiftPanelsVertically(
                bay, selectedStack, CartLayout.BoxLayerU);
            _pendingBox = box;
            _pendingPatchShiftStack = selectedStack;
            _pendingPatchShiftU = CartLayout.BoxLayerU;
            ModSettings.Debug(
                $"Created {CartLayout.BoxLayerU}U box layer on lowest stack " +
                $"{selectedStack + 1}: row {selectedRow}, " +
                $"previous height {selectedHeight}U.");
            return true;
        }

        Melon<CartItemStacker.Mod>.Logger.Warning(
            $"No free boxslot and no {CartLayout.BoxLayerU}U available for a new box layer.");
        return false;
    }

    internal static void CompletePlacement(TrolleyLoadingBay bay)
    {
        if (_pendingBox is null)
            return;

        var box = _pendingBox;
        var succeeded = DynamicTargetAllocator.GetHeldObject() is null;
        if (!succeeded)
        {
            if (_pendingPatchShiftStack >= 0 && _pendingPatchShiftU != 0)
                PatchPanelLayerLayout.ShiftPanelsVertically(
                    bay,
                    _pendingPatchShiftStack,
                    -_pendingPatchShiftU);
            ForgetBox(box);
            box.sizeInU = CartLayout.NativeBoxSizeU;
            TrolleyContext.Unregister(box);
        }
        RebuildLayerReservations(bay);

        _pendingBox = null;
        _pendingPatchShiftStack = -1;
        _pendingPatchShiftU = 0;
    }

    internal static void RebuildLayerReservations(TrolleyLoadingBay bay)
    {
        var used = bay?.usedPositions;
        if (bay is null || used is null)
            return;

        for (var i = 0; i < used.Length; i++)
            used[i] = 0;

        foreach (var item in TrolleyContext.Items)
        {
            if (item is null || IsBox(item) ||
                PatchPanelLayerLayout.IsPatchPanel(item) ||
                ModuleTrayLayout.IsTray(item) ||
                CableWheelLayout.IsCableWheel(item) ||
                item.objectInHands ||
                !item.isOnTrolley)
                continue;
            var start = item.trolleySlotIndex;
            var size = System.Math.Max(1, item.sizeInU);
            for (var i = start;
                i < start + size &&
                i >= 0 &&
                i < CartLayout.ServerSlots &&
                i < used.Length;
                i++)
                used[i] = 1;
        }

        for (var stack = 0; stack < CartLayout.StackCount; stack++)
        {
            var highestRow = -1;
            foreach (var item in TrolleyContext.Items)
                if (IsBox(item) && !item.objectInHands && GetItemStack(item) == stack)
                    highestRow = System.Math.Max(
                        highestRow,
                        GetStackOrdinal(item, stack) / CartLayout.BoxesPerLayer);
            var layers = highestRow + 1;
            for (var row = 0; row < layers; row++)
            {
                var start = stack * CartLayout.SlotsPerStack +
                    GetServerHeight(stack) + row * CartLayout.BoxLayerU;
                var stackEnd = (stack + 1) * CartLayout.SlotsPerStack;
                for (var i = start;
                    i < start + CartLayout.BoxLayerU && i < stackEnd;
                    i++)
                    used[i] = 1;
            }
        }
        PatchPanelLayerLayout.RebuildReservations(bay);
    }

    internal static bool PrepareRackInsertion(
        TrolleyLoadingBay bay,
        int size,
        out int stack,
        out int start)
    {
        stack = -1;
        start = -1;
        if (bay?.usedPositions is null)
            return false;

        var selectedRackHeight = 0;
        var selectedBoxU = 0;
        var selectedTotalHeight = int.MaxValue;
        for (var candidate = 0; candidate < CartLayout.StackCount; candidate++)
        {
            var rackHeight = GetServerHeight(candidate);
            var boxU = GetBoxRowCount(candidate) * CartLayout.BoxLayerU;
            var patchU = PatchPanelLayerLayout.GetOccupiedHeightU(candidate);
            var totalHeight = rackHeight + boxU + patchU;
            if (!CapacityRules.CanAddEquipmentHeight(
                    totalHeight,
                    size,
                    ModSettings.EquipmentStackMaxUnits) ||
                totalHeight >= selectedTotalHeight)
                continue;

            stack = candidate;
            selectedRackHeight = rackHeight;
            selectedBoxU = boxU;
            selectedTotalHeight = totalHeight;
        }

        if (stack < 0)
            return false;

        start = stack * CartLayout.SlotsPerStack + selectedRackHeight;

        var selectedPatchU =
            PatchPanelLayerLayout.GetOccupiedHeightU(stack);
        var upperU = selectedBoxU + selectedPatchU;
        // Move the virtual box and patch reservations upward. Their visible
        // objects are moved before the native placement begins.
        for (var i = start; i < start + upperU; i++)
            bay.usedPositions[i] = 0;
        for (var i = start + size; i < start + size + upperU; i++)
            bay.usedPositions[i] = 1;
        PatchPanelLayerLayout.ShiftPanelsVertically(
            bay, stack, size);

        ModSettings.Debug(
            $"Lowest-stack allocator chose stack {stack + 1} at " +
            $"{selectedTotalHeight}U for a {size}U item.");
        return true;
    }

    internal static int GetBoxRowCount(int stack)
    {
        var highestRow = -1;
        foreach (var pair in Slots)
            if (pair.Value.Stack == stack)
                highestRow = System.Math.Max(
                    highestRow,
                    pair.Value.Ordinal / CartLayout.BoxesPerLayer);
        return System.Math.Min(highestRow + 1, CartLayout.BoxRowsPerStack);
    }

    internal static int GetOccupiedHeightWithoutPatchesU(int stack) =>
        GetServerHeight(stack) +
        GetBoxRowCount(stack) * CartLayout.BoxLayerU;

    internal static int GetOccupiedHeightU(int stack) =>
        GetOccupiedHeightWithoutPatchesU(stack) +
        PatchPanelLayerLayout.GetOccupiedHeightU(stack);

    internal static void ShiftBoxesVertically(
        TrolleyLoadingBay bay,
        int stack,
        int deltaU)
    {
        if (bay is null || deltaU == 0)
            return;
        var delta = bay.transform.TransformVector(new Vector3(0f, deltaU * U, 0f));
        var movementToken = ++_gravityMovementToken;
        var moved = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (!IsBox(item) || item.objectInHands || GetItemStack(item) != stack)
                continue;
            MelonCoroutines.Start(AnimateGravityMove(
                item,
                item.transform.position + delta,
                item.transform.rotation,
                movementToken));
            moved++;
        }
        if (moved > 0)
            ModSettings.Debug(
                $"Shifted {moved} box(es) by {deltaU}U on stack {stack + 1}.");
    }

    private static bool RangeIsFree(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used,
        int start,
        int size)
    {
        if (used is null || start < 0 || start + size > used.Length)
            return false;
        for (var i = start; i < start + size; i++)
            if (used[i] != 0)
                return false;
        return true;
    }

    internal static void ApplyPose(
        TrolleyLoadingBay bay,
        Transform target,
        UsableObject box)
    {
        if (Slots.TryGetValue(box.Pointer, out var assigned))
        {
            var row = assigned.Ordinal / CartLayout.BoxesPerLayer;
            var column = assigned.Ordinal % CartLayout.BoxesPerLayer;
            var z = GetBoxStackZ(assigned.Stack);
            var localPosition = new Vector3(
                0f,
                BoxDeckY + GetServerHeight(assigned.Stack) * U + row * GetVisualLayerHeight(),
                z + ColumnZ[column]);
            target.SetPositionAndRotation(
                bay.transform.TransformPoint(localPosition),
                bay.transform.rotation * Quaternion.Euler(90f, 180f, 0f));
            return;
        }

        var ordinal = ExistingBoxCount(box);
        ApplyPoseAtOrdinal(bay, target, box, ordinal);
    }

    private static void ApplyPoseAtOrdinal(
        TrolleyLoadingBay bay,
        Transform target,
        UsableObject box,
        int ordinal)
    {
        var layer = ordinal / CartLayout.BoxesPerLayer;
        var column = ordinal % CartLayout.BoxesPerLayer;
        var stack = layer % CartLayout.StackCount;
        var row = layer / CartLayout.StackCount;
        Slots[box.Pointer] = new BoxSlot(
            stack, row * CartLayout.BoxesPerLayer + column);
        var serverHeight = GetServerHeight(stack);
        var visualLayerHeight = GetVisualLayerHeight();
        var z = GetBoxStackZ(stack);
        var localPosition = new Vector3(
            0f,
            BoxDeckY + serverHeight * U + row * visualLayerHeight,
            z + ColumnZ[column]);
        target.SetPositionAndRotation(
            bay.transform.TransformPoint(localPosition),
            bay.transform.rotation * Quaternion.Euler(90f, 180f, 0f));
    }

    internal static void CompactStack(TrolleyLoadingBay bay, int stack, int removedOrdinal)
    {
        var targets = bay?.positionsOnTrolley;
        if (bay is null || targets is null || stack < 0)
            return;

        UsableObject candidate = null;
        var candidateOrdinal = -1;
        var removedRow = removedOrdinal / CartLayout.BoxesPerLayer;
        foreach (var box in TrolleyContext.Items)
        {
            if (!IsBox(box) || box.objectInHands || GetItemStack(box) != stack)
                continue;
            var ordinal = GetStackOrdinal(box, stack);
            // Pull exactly one box from the highest occupied layer. Picking the
            // highest ordinal also empties that top layer from right to left.
            if (ordinal / CartLayout.BoxesPerLayer > removedRow &&
                ordinal > candidateOrdinal)
            {
                candidate = box;
                candidateOrdinal = ordinal;
            }
        }

        if (candidate is null)
        {
            ModSettings.Debug(
                $"Local box gravity found no box above the gap on stack {stack + 1}.");
            return;
        }

        var row = removedOrdinal / CartLayout.BoxesPerLayer;
        var column = removedOrdinal % CartLayout.BoxesPerLayer;
        var slot = stack * CartLayout.SlotsPerStack + GetServerHeight(stack) +
            row * CartLayout.BoxLayerU;
        candidate.sizeInU = 0;
        ApplyPoseAtStackSlot(
            bay,
            candidate,
            stack,
            row,
            column,
            animate: true);
        candidate.trolleySlotIndex = slot;
        ModSettings.Debug(
            $"Local box gravity moved one rack from slot {candidateOrdinal} " +
            $"to gap {removedOrdinal} on stack {stack + 1}.");
    }

    private static void ApplyPoseAtStackSlot(
        TrolleyLoadingBay bay,
        UsableObject box,
        int stack,
        int row,
        int column,
        bool animate)
    {
        var visualHeight = GetVisualLayerHeight();
        var z = GetBoxStackZ(stack);
        var newAnchor = new Vector3(0f,
            BoxDeckY + GetServerHeight(stack) * U + row * visualHeight,
            z + ColumnZ[column]);

        if (Slots.TryGetValue(box.Pointer, out var previous))
        {
            var previousRow = previous.Ordinal / CartLayout.BoxesPerLayer;
            var previousColumn = previous.Ordinal % CartLayout.BoxesPerLayer;
            var previousZ = GetBoxStackZ(previous.Stack);
            var oldAnchor = new Vector3(0f,
                BoxDeckY + GetServerHeight(previous.Stack) * U +
                    previousRow * visualHeight,
                previousZ + ColumnZ[previousColumn]);
            var destination = box.transform.position +
                bay.transform.TransformVector(newAnchor - oldAnchor);
            if (animate)
            {
                var movementToken = ++_gravityMovementToken;
                MelonCoroutines.Start(AnimateGravityMove(
                    box,
                    destination,
                    box.transform.rotation,
                    movementToken));
            }
            else
                box.transform.position = destination;
        }
        else
        {
            box.transform.SetPositionAndRotation(
                bay.transform.TransformPoint(newAnchor),
                bay.transform.rotation * Quaternion.Euler(90f, 180f, 0f));
        }
        Slots[box.Pointer] = new BoxSlot(
            stack, row * CartLayout.BoxesPerLayer + column);
    }

    private static IEnumerator AnimateGravityMove(
        UsableObject box,
        Vector3 destination,
        Quaternion rotation,
        int movementToken)
    {
        if (box?.transform is null)
            yield break;

        var startPosition = box.transform.position;
        var startRotation = box.transform.rotation;
        var elapsed = 0f;
        var duration = ModSettings.GetAnimationDuration(GravityMoveDuration);
        while (elapsed < duration)
        {
            yield return null;
            if (movementToken != _gravityMovementToken ||
                box is null ||
                box.objectInHands ||
                box.transform is null)
                yield break;

            elapsed += Time.deltaTime;
            var linear = Mathf.Clamp01(elapsed / duration);
            var eased = linear * linear * (3f - 2f * linear);
            box.transform.SetPositionAndRotation(
                Vector3.Lerp(startPosition, destination, eased),
                Quaternion.Slerp(startRotation, rotation, eased));
        }

        if (movementToken == _gravityMovementToken &&
            box is not null &&
            !box.objectInHands &&
            box.transform is not null)
            box.transform.SetPositionAndRotation(destination, rotation);
    }

    private static float GetVisualLayerHeight()
    {
        var measured = 0f;
        foreach (var item in TrolleyContext.Items)
        {
            if (!IsBox(item) || item.objectInHands)
                continue;

            var renderers = item.GetComponentsInChildren<Renderer>(true);
            if (renderers is null || renderers.Length == 0)
                continue;

            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            foreach (var renderer in renderers)
            {
                if (renderer is null)
                    continue;
                var bounds = renderer.bounds;
                minY = System.Math.Min(minY, bounds.min.y);
                maxY = System.Math.Max(maxY, bounds.max.y);
            }

            if (!float.IsInfinity(minY) && !float.IsInfinity(maxY))
                measured = System.Math.Max(measured, maxY - minY);
        }

        // The first layer needs no vertical repetition yet. The configured
        // box-row height is a safe
        // fallback; later layers use the actual upright model height.
        return measured > 0.01f
            ? measured + 0.005f
            : CartLayout.BoxLayerU * U;
    }

    internal static int GetServerHeight(int stack)
    {
        var height = 0;
        var split = DynamicTargetAllocator.Split;
        foreach (var item in TrolleyContext.Items)
        {
            if (item is null ||
                IsBox(item) ||
                PatchPanelLayerLayout.IsPatchPanel(item) ||
                ModuleTrayLayout.IsTray(item) ||
                CableWheelLayout.IsCableWheel(item) ||
                item.objectInHands ||
                !item.isOnTrolley)
                continue;
            var slot = item.trolleySlotIndex;
            // Accessory targets start at slot 84. Without this boundary the
            // module trays looked like impossible 43-66U cargo on stack 2.
            if (slot < 0 || slot >= CartLayout.ServerSlots)
                continue;
            var itemStack = slot < split ? 0 : 1;
            if (itemStack != stack)
                continue;
            var level = itemStack == 0 ? slot : slot - split;
            height = System.Math.Max(height, level + System.Math.Max(1, item.sizeInU));
        }
        return height;
    }
}
