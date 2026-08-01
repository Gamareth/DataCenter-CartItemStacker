using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class CableWheelLayout
{
    // Retain a small visual safety margin above the trolley platform.
    private const float BaseY = 0.14665f;
    // Renderer bounds change during native placement, so use the calibrated
    // fixed-coordinate spacing instead of measuring live bounds.
    private const float VerticalSpacing = 0.232f;
    private const float GravityMoveDuration =
        LayoutConstants.GravityAnimationDuration;
    private const float FrontZ = 0.585f;
    // Move both columns 3cm outward. This removes the flange overlap while
    // retaining a small margin to the trolley's outer edges.
    private const float StackOffsetX = 0.205f;
    private static CableSpinner _pendingWheel;
    private static int _pendingSlot = -1;
    private static int _movementToken;

    internal static bool TryGetCableWheel(
        UsableObject item,
        out CableSpinner wheel)
    {
        wheel = item as CableSpinner;
        if (wheel is not null)
            return true;
        if (item is null || item.Pointer == System.IntPtr.Zero)
            return false;

        try
        {
            wheel = item.GetComponent<CableSpinner>();
            if (wheel is null)
                wheel = item.GetComponentInChildren<CableSpinner>(true);
        }
        catch (System.Exception)
        {
            wheel = null;
        }
        return wheel is not null && wheel.Pointer != System.IntPtr.Zero;
    }

    internal static bool IsCableWheel(UsableObject item) =>
        TryGetCableWheel(item, out _);

    internal static bool IsCableSlot(int slot) =>
        slot >= CartLayout.CableStart &&
        slot < CartLayout.CableStart + CartLayout.CableSlots;

    internal static bool HasPendingPlacement => _pendingWheel is not null;

    internal static void Reset()
    {
        _pendingWheel = null;
        _pendingSlot = -1;
        _movementToken++;
    }

    internal static void RehydrateLoaded(
        TrolleyLoadingBay bay,
        IEnumerable<UsableObject> loadedItems)
    {
        if (bay?.transform is null || loadedItems is null)
            return;

        var byStack = new[]
        {
            new List<CableSpinner>(),
            new List<CableSpinner>(),
        };
        foreach (var item in loadedItems)
        {
            if (!TryGetCableWheel(item, out var wheel) ||
                wheel?.transform is null)
                continue;

            var local = bay.transform.InverseTransformPoint(
                wheel.transform.position);
            var stack = System.Math.Abs(local.x + StackOffsetX) <=
                System.Math.Abs(local.x - StackOffsetX)
                ? 0
                : 1;
            byStack[stack].Add(wheel);
        }

        for (var stack = 0; stack < byStack.Length; stack++)
        {
            byStack[stack].Sort((left, right) =>
            {
                var leftY = bay.transform.InverseTransformPoint(
                    left.transform.position).y;
                var rightY = bay.transform.InverseTransformPoint(
                    right.transform.position).y;
                return leftY.CompareTo(rightY);
            });

            for (var level = 0;
                level < byStack[stack].Count &&
                level < CartLayout.CableSlotsPerStack;
                level++)
            {
                var wheel = byStack[stack][level];
                var slot = CartLayout.CableStart +
                    stack * CartLayout.CableSlotsPerStack +
                    level;
                wheel.trolleySlotIndex = slot;
                wheel.storedPosition = slot;
            }
        }
    }

    internal static bool TryGetPendingNativeTarget(
        TrolleyLoadingBay bay,
        UsableObject item,
        out int slot,
        out Transform target)
    {
        slot = -1;
        target = null;
        if (bay?.positionsOnTrolley is null ||
            item is null ||
            _pendingWheel is null ||
            item.Pointer != _pendingWheel.Pointer ||
            _pendingSlot < 0 ||
            _pendingSlot >= bay.positionsOnTrolley.Length)
            return false;

        slot = _pendingSlot;
        target = bay.positionsOnTrolley[slot];
        return target is not null;
    }

    internal static bool PreparePlacement(
        TrolleyLoadingBay bay,
        CableSpinner wheel)
    {
        _pendingWheel = null;
        _pendingSlot = -1;
        if (bay?.positionsOnTrolley is null ||
            bay.usedPositions is null ||
            wheel is null)
            return false;

        var counts = new int[CartLayout.CableStackCount];
        foreach (var item in TrolleyContext.Items)
        {
            if (!IsCableWheel(item) ||
                item.objectInHands ||
                item.Pointer == wheel.Pointer)
                continue;
            var slot = item.trolleySlotIndex;
            if (!IsCableSlot(slot))
                continue;
            var stack =
                (slot - CartLayout.CableStart) /
                CartLayout.CableSlotsPerStack;
            if (stack >= 0 && stack < counts.Length)
                counts[stack]++;
        }

        var stackIndex = counts[0] <= counts[1] ? 0 : 1;
        if (!CapacityRules.CanAddCableSpool(
            counts[stackIndex],
            ModSettings.CableSpoolsPerStack))
        {
            stackIndex = 1 - stackIndex;
            if (!CapacityRules.CanAddCableSpool(
                counts[stackIndex],
                ModSettings.CableSpoolsPerStack))
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Both cable-wheel stacks are full " +
                    $"({ModSettings.CableSpoolsPerStack} wheels each).");
                return false;
            }
        }

        var ordinal = counts[stackIndex];
        var slotIndex =
            CartLayout.CableStart +
            stackIndex * CartLayout.CableSlotsPerStack +
            ordinal;
        SetTargetPose(
            bay,
            bay.positionsOnTrolley[slotIndex],
            slotIndex);

        wheel.sizeInU = 1;
        TrolleyContext.Register(wheel);
        for (var i = 0; i < slotIndex && i < bay.usedPositions.Length; i++)
            if (bay.usedPositions[i] == 0)
                bay.usedPositions[i] = 1;
        bay.usedPositions[slotIndex] = 0;

        _pendingWheel = wheel;
        _pendingSlot = slotIndex;
        ModSettings.Debug(
            $"Prepared cable wheel for front stack {stackIndex + 1}, " +
            $"level {ordinal + 1}.");
        return true;
    }

    internal static void CompletePlacement(TrolleyLoadingBay bay)
    {
        if (_pendingWheel is null)
            return;

        var wheel = _pendingWheel;
        var succeeded = DynamicTargetAllocator.GetHeldObject() is null;
        if (succeeded)
        {
            wheel.trolleySlotIndex = _pendingSlot;
            wheel.storedPosition = _pendingSlot;
            wheel.isOnTrolley = true;
            Arrange(bay, "cable placement");
        }
        else
        {
            TrolleyContext.Unregister(wheel);
            BoxLayerLayout.RebuildLayerReservations(bay);
        }

        _pendingWheel = null;
        _pendingSlot = -1;
    }

    internal static bool CancelPendingPlacement(TrolleyLoadingBay bay)
    {
        if (_pendingWheel is null)
            return false;

        TrolleyContext.Unregister(_pendingWheel);
        _pendingWheel = null;
        _pendingSlot = -1;
        BoxLayerLayout.RebuildLayerReservations(bay);
        return true;
    }

    internal static void Arrange(TrolleyLoadingBay bay, string reason)
    {
        if (bay?.positionsOnTrolley is null)
            return;

        var movementToken = ++_movementToken;
        var animateGravity = reason.Contains(
            "extraction",
            System.StringComparison.OrdinalIgnoreCase);
        var stacks = new[]
        {
            new List<CableSpinner>(),
            new List<CableSpinner>(),
        };
        foreach (var item in TrolleyContext.Items)
        {
            if (!TryGetCableWheel(item, out var wheel) ||
                wheel.objectInHands)
                continue;
            var slot = wheel.trolleySlotIndex;
            var stack = IsCableSlot(slot)
                ? (slot - CartLayout.CableStart) /
                    CartLayout.CableSlotsPerStack
                : 0;
            stack = System.Math.Max(
                0, System.Math.Min(CartLayout.CableStackCount - 1, stack));
            stacks[stack].Add(wheel);
        }

        for (var stack = 0; stack < stacks.Length; stack++)
        {
            stacks[stack].Sort((left, right) =>
                left.trolleySlotIndex.CompareTo(right.trolleySlotIndex));
            for (var level = 0;
                level < stacks[stack].Count &&
                level < CartLayout.CableSlotsPerStack;
                level++)
            {
                var slot =
                    CartLayout.CableStart +
                    stack * CartLayout.CableSlotsPerStack +
                    level;
                var wheel = stacks[stack][level];
                var oldSlot = wheel.trolleySlotIndex;
                SetTargetPose(bay, bay.positionsOnTrolley[slot], slot);
                if (
                    IsCableSlot(oldSlot) &&
                    oldSlot < bay.positionsOnTrolley.Length &&
                    bay.positionsOnTrolley[oldSlot] is not null &&
                    oldSlot != slot)
                {
                    var destination = wheel.transform.position +
                        bay.positionsOnTrolley[slot].position -
                        bay.positionsOnTrolley[oldSlot].position;
                    if (animateGravity)
                    {
                        MelonCoroutines.Start(StoredCargoMotion.AnimateAbsolute(
                            wheel,
                            destination,
                            bay.positionsOnTrolley[slot].rotation,
                            ModSettings.GetAnimationDuration(GravityMoveDuration),
                            StoredCargoMotion.PositionEasing.SmoothStep,
                            StoredCargoMotion.RotationMotion.Interpolate,
                            () => movementToken == _movementToken));
                    }
                    else
                        wheel.transform.SetPositionAndRotation(
                            destination,
                            bay.positionsOnTrolley[slot].rotation);
                }
                else if (!IsCableSlot(oldSlot))
                    wheel.transform.SetPositionAndRotation(
                        bay.positionsOnTrolley[slot].position,
                        bay.positionsOnTrolley[slot].rotation);
                else
                    wheel.transform.rotation =
                        bay.positionsOnTrolley[slot].rotation;
                wheel.trolleySlotIndex = slot;
                wheel.storedPosition = slot;
                wheel.isOnTrolley = true;
            }
        }

        BoxLayerLayout.RebuildLayerReservations(bay);
        ModSettings.Debug(
            $"Arranged twin cable-wheel stacks after {reason}.");
    }

    internal static void ApplyTargetPose(
        TrolleyLoadingBay bay,
        Transform target,
        int slot)
    {
        SetTargetPose(bay, target, slot);
    }

    private static void SetTargetPose(
        TrolleyLoadingBay bay,
        Transform target,
        int slot)
    {
        if (bay?.transform is null ||
            target is null ||
            !IsCableSlot(slot))
            return;

        var local = slot - CartLayout.CableStart;
        var stack = local / CartLayout.CableSlotsPerStack;
        var level = local % CartLayout.CableSlotsPerStack;
        var x = stack == 0 ? -StackOffsetX : StackOffsetX;
        var localPosition = new Vector3(
            x,
            BaseY + level * VerticalSpacing,
            FrontZ);
        var flatRotation = Quaternion.Euler(0f, 0f, 270f);
        var labelRotation = Quaternion.Euler(155f, 0f, 0f);
        var localRotation = flatRotation * labelRotation;
        target.SetPositionAndRotation(
            bay.transform.TransformPoint(localPosition),
            bay.transform.rotation * localRotation);
    }
}
