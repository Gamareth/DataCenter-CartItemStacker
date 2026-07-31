using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class PatchPanelLayerLayout
{
    private readonly struct PatchSlot
    {
        internal readonly int Stack;
        internal readonly int Ordinal;

        internal PatchSlot(int stack, int ordinal)
        {
            Stack = stack;
            Ordinal = ordinal;
        }
    }

    private const float U = LayoutConstants.UnitHeight;
    // With the final face-down orientation the visible panel bottom is about
    // 0.3495m below its prefab root. The supported 3U/7U tests put the trolley
    // deck at local Y ~= 0.019m, so a 2mm surface gap requires a 0.3705m root
    // target. This fallback is used only when the stack has no support item;
    // occupied stacks continue to use measured renderer-bounds support tops.
    private const float DeckY = 0.3705f;
    // Seven centers span exactly from the calibrated first center to its
    // mirrored counterpart at the opposite server edge.
    private const float ColumnSpacingX = 0.095833f;
    private const float VisualLayerSpacingY = 0.185f;
    private const float GravityMoveDuration =
        LayoutConstants.GravityAnimationDuration;
    // Keep the calibrated first-column center independent from row capacity
    // and spacing so separation changes cannot move the outer edge.
    private const float FirstColumnCenterAbsX = 0.2875f;
    private static readonly Dictionary<System.IntPtr, PatchSlot> Slots = new();
    private static UsableObject _pendingPanel;
    private static int _pendingStart = -1;
    private static int _gravityMovementToken;

    internal static bool IsPatchPanel(UsableObject item)
    {
        if (item is null)
            return false;

        // PatchPanel_combo is exposed at runtime as a plain UsableObject rather
        // than the PatchPanel component type found in the generated assembly.
        var objectName = item.name ?? string.Empty;
        return objectName.StartsWith(
            "PatchPanel",
            System.StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasPendingPlacement => _pendingPanel is not null;

    internal static void Reset()
    {
        Slots.Clear();
        _pendingPanel = null;
        _pendingStart = -1;
        _gravityMovementToken++;
    }

    internal static bool TryGetPendingNativeTarget(
        TrolleyLoadingBay bay,
        UsableObject panel,
        out int slot,
        out Transform target)
    {
        slot = -1;
        target = null;
        if (bay?.positionsOnTrolley is null ||
            panel is null ||
            _pendingPanel is null ||
            panel.Pointer != _pendingPanel.Pointer ||
            _pendingStart < 0 ||
            _pendingStart >= bay.positionsOnTrolley.Length)
            return false;

        slot = _pendingStart;
        target = bay.positionsOnTrolley[slot];
        return target is not null;
    }

    internal static int GetRowCount(int stack)
    {
        var highestRow = -1;
        foreach (var pair in Slots)
            if (pair.Value.Stack == stack)
                highestRow = System.Math.Max(
                    highestRow,
                    pair.Value.Ordinal / CartLayout.PatchPanelsPerLayer);
        return highestRow + 1;
    }

    internal static int GetOccupiedHeightU(int stack) =>
        GetRowCount(stack) * CartLayout.PatchLayerU;

    internal static int GetItemStack(UsableObject item)
    {
        if (item is null)
            return -1;
        return Slots.TryGetValue(item.Pointer, out var slot)
            ? slot.Stack
            : -1;
    }

    internal static int GetItemOrdinal(UsableObject item)
    {
        if (item is null)
            return -1;
        return Slots.TryGetValue(item.Pointer, out var slot)
            ? slot.Ordinal
            : -1;
    }

    internal static bool PreparePlacement(
        TrolleyLoadingBay bay,
        UsableObject panel)
    {
        _pendingPanel = null;
        _pendingStart = -1;
        if (bay?.positionsOnTrolley is null ||
            bay.usedPositions is null ||
            panel is null)
            return false;

        var selectedStack = -1;
        var selectedRow = -1;
        var selectedColumn = -1;
        var selectedHeight = int.MaxValue;

        // Fill gaps in existing patch rows before consuming another vertical
        // PatchLayerU block. Rows never mix between the two server stacks.
        for (var stack = 0; stack < CartLayout.StackCount; stack++)
        {
            var rows = GetRowCount(stack);
            for (var row = 0; row < rows; row++)
            {
                var occupied = new bool[CartLayout.PatchPanelsPerLayer];
                foreach (var item in TrolleyContext.Items)
                {
                    if (!IsPatchPanel(item) ||
                        item.objectInHands ||
                        item.Pointer == panel.Pointer ||
                        !Slots.TryGetValue(item.Pointer, out var slot) ||
                        slot.Stack != stack ||
                        slot.Ordinal / CartLayout.PatchPanelsPerLayer != row)
                        continue;
                    occupied[slot.Ordinal % CartLayout.PatchPanelsPerLayer] = true;
                }

                for (var column = 0;
                    column < CartLayout.PatchPanelsPerLayer;
                    column++)
                {
                    if (occupied[column])
                        continue;
                    var height = BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack) +
                        rows * CartLayout.PatchLayerU;
                    if (height < selectedHeight)
                    {
                        selectedStack = stack;
                        selectedRow = row;
                        selectedColumn = column;
                        selectedHeight = height;
                    }
                    break;
                }
            }
        }

        if (selectedStack < 0)
        {
            for (var stack = 0; stack < CartLayout.StackCount; stack++)
            {
                var row = GetRowCount(stack);
                var baseHeight =
                    BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack);
                var height = baseHeight +
                    row * CartLayout.PatchLayerU;
                if (!CapacityRules.CanAddEquipmentHeight(
                        height,
                        CartLayout.PatchLayerU,
                        ModSettings.EquipmentStackMaxUnits) ||
                    height >= selectedHeight)
                    continue;

                selectedStack = stack;
                selectedRow = row;
                selectedColumn = 0;
                selectedHeight = height;
            }
        }

        if (selectedStack < 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"No {CartLayout.PatchLayerU}U available for another " +
                "seven-position patch-panel layer.");
            return false;
        }

        Slots[panel.Pointer] = new PatchSlot(
            selectedStack,
            selectedRow * CartLayout.PatchPanelsPerLayer + selectedColumn);
        panel.sizeInU = 0;
        TrolleyContext.Register(panel);

        _pendingStart =
            selectedStack * CartLayout.SlotsPerStack +
            BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(selectedStack) +
            selectedRow * CartLayout.PatchLayerU;
        if (_pendingStart < 0 ||
            _pendingStart >= CartLayout.ServerSlots ||
            _pendingStart >= bay.positionsOnTrolley.Length)
        {
            Slots.Remove(panel.Pointer);
            TrolleyContext.Unregister(panel);
            panel.sizeInU = 1;
            _pendingStart = -1;
            return false;
        }

        RebuildReservations(bay);
        ApplyPose(bay, bay.positionsOnTrolley[_pendingStart], panel);

        // Native storage scans from slot zero. Temporarily occupy earlier free
        // positions so this zero-U member lands on its logical patch row.
        for (var i = 0; i < _pendingStart && i < bay.usedPositions.Length; i++)
            if (bay.usedPositions[i] == 0)
                bay.usedPositions[i] = 1;
        bay.usedPositions[_pendingStart] = 0;

        _pendingPanel = panel;
        ModSettings.Debug(
            $"Prepared patch panel for stack {selectedStack + 1}, " +
            $"row {selectedRow}, column {selectedColumn}, " +
            $"logical top {selectedHeight + CartLayout.PatchLayerU}U.");
        return true;
    }

    internal static void CompletePlacement(TrolleyLoadingBay bay)
    {
        if (_pendingPanel is null)
            return;

        var panel = _pendingPanel;
        var succeeded = DynamicTargetAllocator.GetHeldObject() is null;
        if (succeeded)
        {
            panel.trolleySlotIndex = _pendingStart;
            panel.storedPosition = _pendingStart;
            panel.isOnTrolley = true;
        }
        else
        {
            Slots.Remove(panel.Pointer);
            TrolleyContext.Unregister(panel);
            panel.sizeInU = 1;
        }
        // This clears temporary slots used to force native scanning onto stack
        // 2, then reconstructs servers, boxes and patch layers from their
        // authoritative logical assignments.
        BoxLayerLayout.RebuildLayerReservations(bay);

        _pendingPanel = null;
        _pendingStart = -1;
    }

    internal static void Forget(UsableObject panel)
    {
        if (panel is not null)
            Slots.Remove(panel.Pointer);
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
            if (!IsPatchPanel(item) || item?.transform is null)
                continue;

            var local = bay.transform.InverseTransformPoint(item.transform.position);
            var stack = System.Math.Abs(local.z - CartLayout.GetStackZ(0)) <=
                System.Math.Abs(local.z - CartLayout.GetStackZ(1))
                ? 0
                : 1;
            byStack[stack].Add(item);
        }

        for (var stack = 0; stack < byStack.Length; stack++)
        {
            var selectedStack = stack;
            byStack[stack].Sort((left, right) =>
            {
                var leftLocal = bay.transform.InverseTransformPoint(
                    left.transform.position);
                var rightLocal = bay.transform.InverseTransformPoint(
                    right.transform.position);
                var vertical = leftLocal.y.CompareTo(rightLocal.y);
                if (vertical != 0)
                    return vertical;

                var direction = CartLayout.GetServerYaw(selectedStack) > 180f
                    ? -1
                    : 1;
                return direction * leftLocal.x.CompareTo(rightLocal.x);
            });

            for (var ordinal = 0; ordinal < byStack[stack].Count; ordinal++)
            {
                var panel = byStack[stack][ordinal];
                Slots[panel.Pointer] = new PatchSlot(stack, ordinal);
                panel.sizeInU = 0;
            }
        }
    }

    internal static void CompactStack(
        TrolleyLoadingBay bay,
        int stack,
        int removedOrdinal,
        string reason)
    {
        if (bay is null || stack < 0)
            return;

        UsableObject candidate = null;
        var candidateOrdinal = -1;
        foreach (var item in TrolleyContext.Items)
            if (IsPatchPanel(item) &&
                !item.objectInHands &&
                GetItemStack(item) == stack)
            {
                var ordinal = GetItemOrdinal(item);
                if (ordinal > removedOrdinal && ordinal > candidateOrdinal)
                {
                    candidate = item;
                    candidateOrdinal = ordinal;
                }
            }

        if (candidate is not null)
        {
            var oldAnchor = GetLocalAnchor(stack, candidateOrdinal);
            var newAnchor = GetLocalAnchor(stack, removedOrdinal);
            var destination = candidate.transform.position +
                bay.transform.TransformVector(newAnchor - oldAnchor);
            var movementToken = ++_gravityMovementToken;
            MelonCoroutines.Start(AnimateGravityMove(
                candidate,
                destination,
                candidate.transform.rotation,
                movementToken));
            Slots[candidate.Pointer] =
                new PatchSlot(stack, removedOrdinal);
            candidate.trolleySlotIndex =
                stack * CartLayout.SlotsPerStack +
                BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack) +
                (removedOrdinal / CartLayout.PatchPanelsPerLayer) *
                    CartLayout.PatchLayerU;
            candidate.storedPosition = candidate.trolleySlotIndex;
        }
        RebuildReservations(bay);
        ModSettings.Debug(
            candidate is null
                ? $"Patch gravity found no panel above gap {removedOrdinal} " +
                    $"on stack {stack + 1}."
                : $"Patch gravity moved panel {candidateOrdinal} to gap " +
                    $"{removedOrdinal} on stack {stack + 1} after {reason}.");
    }

    private static IEnumerator AnimateGravityMove(
        UsableObject panel,
        Vector3 destination,
        Quaternion rotation,
        int movementToken)
    {
        if (panel?.transform is null)
            yield break;

        var start = panel.transform.position;
        var elapsed = 0f;
        var duration = ModSettings.GetAnimationDuration(GravityMoveDuration);
        while (elapsed < duration)
        {
            yield return null;
            if (movementToken != _gravityMovementToken ||
                panel is null ||
                panel.objectInHands ||
                panel.transform is null)
                yield break;

            elapsed += Time.deltaTime;
            var linear = Mathf.Clamp01(elapsed / duration);
            var eased = linear * linear * (3f - 2f * linear);
            panel.transform.SetPositionAndRotation(
                Vector3.Lerp(start, destination, eased),
                rotation);
        }

        if (movementToken == _gravityMovementToken &&
            panel is not null &&
            !panel.objectInHands &&
            panel.transform is not null)
            panel.transform.SetPositionAndRotation(destination, rotation);
    }

    internal static void Arrange(TrolleyLoadingBay bay, string reason)
    {
        if (bay?.positionsOnTrolley is null)
            return;

        foreach (var item in TrolleyContext.Items)
        {
            if (!IsPatchPanel(item) ||
                item.objectInHands ||
                !Slots.TryGetValue(item.Pointer, out var slot))
                continue;

            var row = slot.Ordinal / CartLayout.PatchPanelsPerLayer;
            var targetIndex =
                slot.Stack * CartLayout.SlotsPerStack +
                BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(slot.Stack) +
                row * CartLayout.PatchLayerU;
            if (targetIndex < 0 ||
                targetIndex >= CartLayout.ServerSlots ||
                targetIndex >= bay.positionsOnTrolley.Length)
                continue;

            ApplyPose(bay, bay.positionsOnTrolley[targetIndex], item);
            item.trolleySlotIndex = targetIndex;
            item.storedPosition = targetIndex;
            item.isOnTrolley = true;
        }

        RebuildReservations(bay);
        ModSettings.Debug(
            $"Arranged patch-panel layers after {reason}.");
    }

    internal static void ApplyPose(
        TrolleyLoadingBay bay,
        Transform target,
        UsableObject panel)
    {
        if (bay?.transform is null ||
            target is null ||
            panel is null ||
            !Slots.TryGetValue(panel.Pointer, out var slot))
            return;

        var localPosition = GetLocalAnchor(slot.Stack, slot.Ordinal);

        // The narrow 1U edges advance over the cart width (local X), while the
        // long panel bodies point through the cart length. This is the final
        // face-down pose: the prior (0,90,0) target still needed a visible late
        // turn, and the resulting (270,90,0) pose pointed the faceplate upward.
        var localRotation = Quaternion.Euler(90f, 90f, 0f);

        if (TryGetProjectedModelBounds(
            panel,
            localRotation,
            out var projectedCenter,
            out var projectedMinY))
        {
            // Target the visible geometry, not the prefab root. This makes the
            // native animation land correctly on its first attempt for all
            // three patch-panel variants.
            localPosition.x -= projectedCenter.x;
            localPosition.z -= projectedCenter.z;

            var row =
                slot.Ordinal / CartLayout.PatchPanelsPerLayer;
            if (TryGetSupportTopLocal(bay, panel, slot.Stack, out var supportTop))
                localPosition.y =
                    supportTop +
                    0.002f +
                    row * VisualLayerSpacingY -
                    projectedMinY;
        }
        target.SetPositionAndRotation(
            bay.transform.TransformPoint(localPosition),
            bay.transform.rotation * localRotation);
    }

    internal static bool TryGetProjectedModelBounds(
        UsableObject item,
        Quaternion targetLocalRotation,
        out Vector3 center,
        out float minY)
    {
        center = Vector3.zero;
        minY = 0f;
        if (item?.transform is null)
            return false;

        var minimum = new Vector3(
            float.PositiveInfinity,
            float.PositiveInfinity,
            float.PositiveInfinity);
        var maximum = new Vector3(
            float.NegativeInfinity,
            float.NegativeInfinity,
            float.NegativeInfinity);
        var found = false;
        foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is null || !renderer.enabled)
                continue;

            var bounds = renderer.localBounds;
            var boundsCenter = bounds.center;
            var boundsExtents = bounds.extents;
            for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var rendererLocal = boundsCenter + Vector3.Scale(
                            boundsExtents,
                            new Vector3(x, y, z));
                        var rootLocal = item.transform.InverseTransformPoint(
                            renderer.transform.TransformPoint(rendererLocal));
                        var projected = targetLocalRotation * rootLocal;
                        minimum = Vector3.Min(minimum, projected);
                        maximum = Vector3.Max(maximum, projected);
                        found = true;
                    }
        }

        if (!found)
            return false;
        center = (minimum + maximum) * 0.5f;
        minY = minimum.y;
        return true;
    }

    private static bool TryGetSupportTopLocal(
        TrolleyLoadingBay bay,
        UsableObject panel,
        int stack,
        out float supportTop)
    {
        supportTop = float.NegativeInfinity;
        if (bay?.transform is null)
            return false;

        foreach (var item in TrolleyContext.Items)
        {
            if (item is null ||
                item.Pointer == panel.Pointer ||
                item.objectInHands ||
                !item.isOnTrolley ||
                IsPatchPanel(item) ||
                ModuleTrayLayout.IsTray(item) ||
                CableWheelLayout.IsCableWheel(item))
                continue;

            var itemStack = BoxLayerLayout.IsBox(item)
                ? BoxLayerLayout.GetItemStack(item)
                : item.trolleySlotIndex >= 0 &&
                    item.trolleySlotIndex < CartLayout.ServerSlots
                    ? item.trolleySlotIndex / CartLayout.SlotsPerStack
                    : -1;
            if (itemStack != stack ||
                !TryGetWorldBounds(item, out var itemBounds))
                continue;

            var localTop = bay.transform.InverseTransformPoint(
                itemBounds.max).y;
            supportTop = System.Math.Max(supportTop, localTop);
        }
        return !float.IsNegativeInfinity(supportTop);
    }

    internal static void NormalizeStoredPose(
        TrolleyLoadingBay bay,
        UsableObject panel)
    {
        if (bay?.transform is null ||
            panel?.transform is null ||
            !Slots.TryGetValue(panel.Pointer, out var slot) ||
            !TryGetWorldBounds(panel, out var panelBounds))
            return;

        var column = slot.Ordinal % CartLayout.PatchPanelsPerLayer;
        var currentLocalCenter =
            bay.transform.InverseTransformPoint(panelBounds.center);
        var desiredLocalCenter = new Vector3(
            GetColumnCenterX(slot.Stack, column),
            currentLocalCenter.y,
            CartLayout.GetStackZ(slot.Stack));

        // Normalize the visible body rather than the prefab root. The three
        // patch-panel prefabs expose different storage pivots, but their
        // renderer bounds must occupy the same logical column footprint.
        panel.transform.position += bay.transform.TransformVector(
            new Vector3(
                desiredLocalCenter.x - currentLocalCenter.x,
                0f,
                desiredLocalCenter.z - currentLocalCenter.z));
        Physics.SyncTransforms();

        if (!TryGetWorldBounds(panel, out panelBounds))
            return;

        var supportTop = float.NegativeInfinity;
        foreach (var item in TrolleyContext.Items)
        {
            if (item is null ||
                item.Pointer == panel.Pointer ||
                item.objectInHands ||
                !item.isOnTrolley ||
                IsPatchPanel(item) ||
                ModuleTrayLayout.IsTray(item) ||
                CableWheelLayout.IsCableWheel(item))
                continue;

            var itemStack = BoxLayerLayout.IsBox(item)
                ? BoxLayerLayout.GetItemStack(item)
                : item.trolleySlotIndex >= 0 &&
                    item.trolleySlotIndex < CartLayout.ServerSlots
                    ? item.trolleySlotIndex / CartLayout.SlotsPerStack
                    : -1;
            if (itemStack != slot.Stack ||
                !TryGetWorldBounds(item, out var itemBounds))
                continue;

            supportTop = System.Math.Max(supportTop, itemBounds.max.y);
        }

        if (!float.IsNegativeInfinity(supportTop))
        {
            const float SurfaceGap = 0.002f;
            var row =
                slot.Ordinal / CartLayout.PatchPanelsPerLayer;
            panel.transform.position += new Vector3(
                0f,
                supportTop +
                    SurfaceGap +
                    row * VisualLayerSpacingY -
                    panelBounds.min.y,
                0f);
            Physics.SyncTransforms();
        }

        if (TryGetWorldBounds(panel, out var finalBounds))
        {
            var localCenter =
                bay.transform.InverseTransformPoint(finalBounds.center);
            var localRotation =
                Quaternion.Inverse(bay.transform.rotation) *
                panel.transform.rotation;
            var euler = localRotation.eulerAngles;
            ModSettings.Debug(
                $"Bounds-aligned '{panel.name}' on stack {slot.Stack + 1}, " +
                $"column {column}: center local " +
                $"({localCenter.x:0.000}, {localCenter.y:0.000}, " +
                $"{localCenter.z:0.000}), size " +
                $"({finalBounds.size.x:0.000}, {finalBounds.size.y:0.000}, " +
                $"{finalBounds.size.z:0.000}), rotation " +
                $"({euler.x:0.0}, {euler.y:0.0}, {euler.z:0.0}), " +
                $"bottom {finalBounds.min.y:0.000}, " +
                $"support top " +
                $"{(float.IsNegativeInfinity(supportTop) ? "<deck>" : supportTop.ToString("0.000"))}.");
        }
    }

    internal static bool TryGetWorldBounds(
        UsableObject item,
        out Bounds bounds)
    {
        bounds = default;
        if (item is null)
            return false;

        var found = false;
        foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is null || !renderer.enabled)
                continue;
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
                bounds.Encapsulate(renderer.bounds);
        }
        return found;
    }

    private static Vector3 GetModelLongAxis(UsableObject item)
    {
        if (!TryGetModelLocalBounds(item, out var bounds))
            return Vector3.right;

        var size = bounds.size;
        if (size.y >= size.x && size.y >= size.z)
            return Vector3.up;
        if (size.z >= size.x && size.z >= size.y)
            return Vector3.forward;
        return Vector3.right;
    }

    private static bool TryGetModelLocalBounds(
        UsableObject item,
        out Bounds bounds)
    {
        bounds = default;
        if (item?.transform is null)
            return false;

        var found = false;
        foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is null || !renderer.enabled)
                continue;

            var localBounds = renderer.localBounds;
            var center = localBounds.center;
            var extents = localBounds.extents;
            for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var rendererLocal = center + Vector3.Scale(
                            extents,
                            new Vector3(x, y, z));
                        var rootLocal = item.transform.InverseTransformPoint(
                            renderer.transform.TransformPoint(rendererLocal));
                        if (!found)
                        {
                            bounds = new Bounds(rootLocal, Vector3.zero);
                            found = true;
                        }
                        else
                            bounds.Encapsulate(rootLocal);
                    }
        }
        return found;
    }

    internal static void ShiftPanelsVertically(
        TrolleyLoadingBay bay,
        int stack,
        int deltaU)
    {
        if (bay?.transform is null || stack < 0 || deltaU == 0)
            return;
        var delta =
            bay.transform.TransformVector(new Vector3(0f, deltaU * U, 0f));
        var movementToken = ++_gravityMovementToken;
        var moved = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (!IsPatchPanel(item) ||
                item.objectInHands ||
                GetItemStack(item) != stack)
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
                $"Shifted {moved} patch panel(s) by {deltaU}U on " +
                $"stack {stack + 1}.");
    }

    private static Vector3 GetLocalAnchor(int stack, int ordinal)
    {
        var row = ordinal / CartLayout.PatchPanelsPerLayer;
        var column = ordinal % CartLayout.PatchPanelsPerLayer;
        return new Vector3(
            GetColumnCenterX(stack, column),
            DeckY +
                BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack) * U +
                row * VisualLayerSpacingY,
            CartLayout.GetStackZ(stack));
    }

    private static float GetColumnCenterX(int stack, int column)
    {
        // The second physical server stack is deliberately rotated 180 degrees
        // so adjacent server flaps do not collide. Its visible "first" edge is
        // therefore the opposite cart-X edge. Preserve the calibrated first
        // center and walk inward, mirrored per stack.
        var outwardSign = CartLayout.GetServerYaw(stack) > 180f ? 1f : -1f;
        return outwardSign *
            (FirstColumnCenterAbsX - column * ColumnSpacingX);
    }

    internal static void RebuildReservations(TrolleyLoadingBay bay)
    {
        var used = bay?.usedPositions;
        if (used is null)
            return;

        for (var stack = 0; stack < CartLayout.StackCount; stack++)
        {
            var baseSlot =
                stack * CartLayout.SlotsPerStack +
                BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack);
            var stackEnd = (stack + 1) * CartLayout.SlotsPerStack;
            for (var row = 0; row < GetRowCount(stack); row++)
            {
                var start = baseSlot + row * CartLayout.PatchLayerU;
                for (var i = start;
                    i < start + CartLayout.PatchLayerU &&
                    i < stackEnd &&
                    i < used.Length;
                    i++)
                    used[i] = 1;
            }
        }
    }
}
