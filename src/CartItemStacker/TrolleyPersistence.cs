using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class NativeSaveLifecycle
{
    private static SaveSystem.OnLoadingData _loadingData;
    private static SaveSystem.OnLoadingDataLater _loadingDataLater;
    private static bool _initialized;
    private static bool _loadInProgress;

    internal static bool LoadInProgress => _loadInProgress;

    internal static void Initialize()
    {
        if (_initialized)
            return;

        _loadingData = (System.Action)OnLoadingData;
        _loadingDataLater = (System.Action)OnLoadingDataLater;
        SaveSystem.onLoadingData += _loadingData;
        SaveSystem.onLoadingDataLater += _loadingDataLater;
        _initialized = true;
    }

    private static void OnLoadingData()
    {
        _loadInProgress = true;
        ModSettings.Debug(
            "Native save loading started; suspended trolley reconstruction.");
        TrolleyTargetPatch.NativeLoadStarted();
    }

    private static void OnLoadingDataLater()
    {
        _loadInProgress = false;
        ModSettings.Debug(
            "Native save loading completed; scheduling trolley reconstruction.");
        TrolleyTargetPatch.NativeLoadCompleted();
    }
}

internal static class LoadedEquipmentLayout
{
    internal static int RehydrateLoaded(
        TrolleyLoadingBay bay,
        List<UsableObject> loadedItems)
    {
        if (bay?.transform is null || loadedItems is null)
            return 0;

        var byStack = new[]
        {
            new List<UsableObject>(),
            new List<UsableObject>(),
        };
        foreach (var item in loadedItems)
        {
            if (!IsEquipment(item) || item?.transform is null)
                continue;

            var local = bay.transform.InverseTransformPoint(
                item.transform.position);
            var stack = System.Math.Abs(local.z - CartLayout.GetStackZ(0)) <=
                System.Math.Abs(local.z - CartLayout.GetStackZ(1))
                ? 0
                : 1;
            byStack[stack].Add(item);
        }

        var assigned = 0;
        var heights = new int[CartLayout.StackCount];
        var overflow = new List<(UsableObject Item, int PreferredStack, int Size)>();
        for (var stack = 0; stack < byStack.Length; stack++)
        {
            byStack[stack].Sort((left, right) =>
            {
                var leftLocal = bay.transform.InverseTransformPoint(
                    left.transform.position);
                var rightLocal = bay.transform.InverseTransformPoint(
                    right.transform.position);
                var vertical = leftLocal.y.CompareTo(rightLocal.y);
                if (vertical != 0)
                    return vertical;
                return leftLocal.x.CompareTo(rightLocal.x);
            });

            foreach (var item in byStack[stack])
            {
                var size = System.Math.Max(1, item.sizeInU);
                if (!CapacityRules.CanAddEquipmentHeight(
                    heights[stack], size, CartLayout.SlotsPerStack))
                {
                    overflow.Add((item, stack, size));
                    continue;
                }

                Assign(item, stack, size, heights);
                assigned++;
            }
        }

        foreach (var (item, preferred, size) in overflow)
        {
            var stack = CapacityRules.ChooseLoadedEquipmentStack(
                preferred, size, heights, CartLayout.SlotsPerStack);
            if (stack >= 0)
            {
                Assign(item, stack, size, heights);
                assigned++;
                continue;
            }

            loadedItems.Remove(item);
            TrolleyContext.ExcludeUnassignedLoadItem(item);
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Loaded equipment '{item.name}' ({size}U) fits neither " +
                "stack and was left at its native saved pose.");
        }

        return assigned;
    }

    private static void Assign(UsableObject item, int stack, int size, int[] heights)
    {
        var slot = stack * CartLayout.SlotsPerStack + heights[stack];
        item.trolleySlotIndex = slot;
        item.storedPosition = slot;
        item.isOnTrolley = true;
        heights[stack] += size;
    }

    private static bool IsEquipment(UsableObject item) =>
        item is not null &&
        !BoxLayerLayout.IsBox(item) &&
        !PatchPanelLayerLayout.IsPatchPanel(item) &&
        !ModuleTrayLayout.IsTray(item) &&
        !CableWheelLayout.IsCableWheel(item) &&
        ServerSectionCatalog.IsEquipment(item);
}
