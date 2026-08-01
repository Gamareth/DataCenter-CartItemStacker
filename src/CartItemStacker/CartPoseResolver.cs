using Il2Cpp;
using UnityEngine;

namespace CartItemStacker;

/// <summary>
/// Resolves a logical cart assignment to the single authoritative root pose
/// consumed by native placement, save-load reconstruction and direct moves.
/// Section layout classes own their geometry; this class owns dispatch and
/// guarantees that every placement entry point uses the same result.
/// </summary>
internal static class CartPoseResolver
{
    internal static void ApplyLayoutAnchorPose(
        TrolleyLoadingBay bay,
        UsableObject item,
        Transform target)
    {
        if (bay is null || item is null || target is null)
            return;

        var targetIndex = FindTargetIndex(bay, target);
        if (ModuleTrayLayout.TryGetTray(item, out _))
        {
            ModuleTrayLayout.ApplyTargetPose(bay, target, targetIndex);
            return;
        }

        if (PatchPanelLayerLayout.IsPatchPanel(item))
        {
            PatchPanelLayerLayout.ApplyPose(bay, target, item);
            return;
        }

        if (CableWheelLayout.IsCableWheel(item))
        {
            CableWheelLayout.ApplyTargetPose(bay, target, targetIndex);
            return;
        }

        if (BoxLayerLayout.IsBox(item))
        {
            BoxLayerLayout.ApplyPose(bay, target, item);
            return;
        }

        if (targetIndex >= 0)
        {
            DynamicTargetAllocator.ApplyServerTargetPose(
                bay,
                target,
                item,
                targetIndex,
                logDetails: true);
            return;
        }

        target.rotation = bay.transform.rotation * Quaternion.Euler(
            0f,
            CartLayout.GetServerYaw(0),
            0f);
    }

    internal static bool ApplyStorageRootPose(
        TrolleyLoadingBay bay,
        UsableObject item,
        Transform target,
        int requestedSlot,
        out int logicalSlot)
    {
        logicalSlot = ResolveLogicalSlot(
            bay,
            item,
            target,
            requestedSlot);
        if (bay?.transform is null || item is null || target is null)
            return false;

        if (BoxLayerLayout.IsBox(item))
            return BoxLayerLayout.ApplyResolvedRootPose(
                bay,
                target,
                item);

        if (ModuleTrayLayout.TryGetTray(item, out _))
            return ModuleTrayLayout.ApplyResolvedRootPose(
                bay,
                target,
                logicalSlot);

        if (PatchPanelLayerLayout.IsPatchPanel(item))
        {
            PatchPanelLayerLayout.ApplyPose(bay, target, item);
            return true;
        }

        if (CableWheelLayout.IsCableWheel(item))
        {
            if (!CableWheelLayout.IsCableSlot(logicalSlot))
                return false;
            CableWheelLayout.ApplyTargetPose(
                bay,
                target,
                logicalSlot);
            return true;
        }

        if (logicalSlot < 0 ||
            logicalSlot >= CartLayout.ServerSlots ||
            !ServerSectionCatalog.IsAllowed(item, out _))
            return false;

        DynamicTargetAllocator.ApplyServerTargetPose(
            bay,
            target,
            item,
            logicalSlot,
            logDetails: true);
        if (DynamicTargetAllocator.TryGetPendingEquipmentPose(
            item,
            out _,
            out var extraLocalY) &&
            Mathf.Abs(extraLocalY) > 0.0001f)
        {
            target.position += bay.transform.TransformVector(
                new Vector3(0f, extraLocalY, 0f));
        }
        return true;
    }

    private static int ResolveLogicalSlot(
        TrolleyLoadingBay bay,
        UsableObject item,
        Transform target,
        int requestedSlot)
    {
        if (ModuleTrayLayout.TryGetPendingSlot(item, out var moduleSlot))
            return moduleSlot;

        if (PatchPanelLayerLayout.TryGetPendingNativeTarget(
            bay,
            item,
            out var patchSlot,
            out _))
            return patchSlot;

        if (CableWheelLayout.TryGetPendingNativeTarget(
            bay,
            item,
            out var cableSlot,
            out _))
            return cableSlot;

        if (DynamicTargetAllocator.TryGetPendingEquipmentPose(
            item,
            out var equipmentSlot,
            out _))
            return equipmentSlot;

        if (requestedSlot >= 0 && requestedSlot < CartLayout.TotalSlots)
            return requestedSlot;

        var storedSlot = item?.trolleySlotIndex ?? -1;
        if (storedSlot >= 0 && storedSlot < CartLayout.TotalSlots)
            return storedSlot;

        return FindTargetIndex(bay, target);
    }

    private static int FindTargetIndex(
        TrolleyLoadingBay bay,
        Transform selected)
    {
        var targets = bay?.positionsOnTrolley;
        if (targets is null || selected is null)
            return -1;

        for (var index = 0; index < targets.Length; index++)
        {
            var candidate = targets[index];
            if (candidate is not null && candidate.Pointer == selected.Pointer)
                return index;
        }

        return -1;
    }
}
