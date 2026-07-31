using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

[HarmonyPatch(typeof(TrolleyLoadingBay), nameof(TrolleyLoadingBay.Start))]
internal static class TrolleyTargetPatch
{
    // Calibrated against an identical server resting naturally on the trolley deck.
    private const float BaseY = LayoutConstants.EquipmentBaseY;
    private const float U = LayoutConstants.UnitHeight;
    private const int SlotsPerStack = CartLayout.SlotsPerStack;
    private const int TotalSlots = CartLayout.TotalSlots;
    private const float MinimumInitializationDelay = 0.75f;
    private const float MaximumInitializationDelay = 3.0f;
    private const float ReadinessSampleDelay = 0.20f;
    private const int RequiredStableSamples = 3;
    private const float CargoEnvelopeHalfWidth = 1.0f;
    private const float CargoEnvelopeHalfLength = 1.25f;
    private const float CargoEnvelopeMinimumHeight = -0.25f;
    private const float CargoEnvelopeMaximumHeight = 3.0f;
    private const float DisableDelay = 0.70f;

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>
        _nativeTargets;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>
        _nativeUsedPositions;
    private static System.IntPtr _nativeBay;
    private static int _disableToken;

    private static void Postfix(TrolleyLoadingBay __instance)
    {
        _nativeTargets = __instance?.positionsOnTrolley;
        _nativeUsedPositions = __instance?.usedPositions;
        _nativeBay = __instance?.Pointer ?? System.IntPtr.Zero;
        _disableToken++;
        TrolleyContext.Current = __instance;
        TrolleyContext.Items.Clear();
        TrolleyContext.SaveItemsDiscovered = false;
        TrolleyContext.LayoutEnabled = false;
        TrolleyContext.PendingDisable = false;
        BoxLayerLayout.Reset();
        PatchPanelLayerLayout.Reset();
        ModuleTrayLayout.Reset();
        CableWheelLayout.Reset();
        TrolleyCompactor.ResetAnchors();
        DynamicTargetAllocator.Reset();
        TrolleyPhysicsIsolation.Reset();
        TrolleyItemInteraction.Reset();
        MelonCoroutines.Start(InitializeAfterSaveLoad(__instance));
    }

    private static IEnumerator InitializeAfterSaveLoad(TrolleyLoadingBay bay)
    {
        if (bay is null)
            yield break;

        var elapsed = 0f;
        var stableSamples = 0;
        var previousCandidateCount = -1;
        var previousReservationCount = -1;
        while (elapsed < MaximumInitializationDelay)
        {
            yield return new WaitForSeconds(ReadinessSampleDelay);
            elapsed += ReadinessSampleDelay;
            if (bay is null ||
                bay.positionsOnTrolley is null ||
                bay.usedPositions is null)
                continue;

            var candidateCount = DiscoverLoadedItems(bay).Count;
            var reservationCount = CountReservations(bay.usedPositions);
            if (elapsed >= MinimumInitializationDelay &&
                candidateCount == previousCandidateCount &&
                reservationCount == previousReservationCount)
            {
                stableSamples++;
                if (stableSamples >= RequiredStableSamples)
                    break;
            }
            else
            {
                stableSamples = 0;
            }

            previousCandidateCount = candidateCount;
            previousReservationCount = reservationCount;
        }

        if (bay is null ||
            bay.positionsOnTrolley is null ||
            bay.usedPositions is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Error(
                "Trolley initialization did not become ready within three seconds.");
            yield break;
        }

        var loadedItems = DiscoverLoadedItems(bay);
        var hasSavedReservations = CountReservations(bay.usedPositions) > 0;
        if (hasSavedReservations && loadedItems.Count == 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "A filled trolley was detected without reliably identifiable cargo. " +
                "Cart Item Stacker left the native trolley untouched.");
            yield break;
        }

        if (!ModSettings.RequestedEnabled && loadedItems.Count == 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "Cart Item Stacker is disabled; the empty trolley remains native.");
            yield break;
        }

        CartLayout.ConfigureHandleSide(bay);
        ModuleTrayLayout.Configure(bay);
        ExpandNativeTargets(bay);
        ApplyTargetPoses(bay);

        foreach (var item in loadedItems)
        {
            TrolleyContext.Register(item);
            item.RemoveRigidbody();
        }

        BoxLayerLayout.RehydrateLoaded(bay, loadedItems);
        PatchPanelLayerLayout.RehydrateLoaded(bay, loadedItems);
        CableWheelLayout.RehydrateLoaded(bay, loadedItems);

        // Loaded cargo has now been identified, so its colliders can be
        // permanently excluded from the chassis snapshot.
        TrolleyPhysicsIsolation.Attach(bay);
        var slots = bay.positionsOnTrolley;
        TrolleyContext.LayoutEnabled = true;
        TrolleyContext.PendingDisable = !ModSettings.RequestedEnabled;

        foreach (var item in loadedItems)
        {
            if (ModuleTrayLayout.TryGetTray(item, out var moduleTray))
            {
                moduleTray.sizeInU = 1;
                moduleTray.isOnTrolley = true;
                TrolleyContext.Register(moduleTray);
                continue;
            }

            var slot = GetLoadedTargetSlot(item);
            if (slot < 0 || slot >= slots.Length || slots[slot] is null)
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Skipped loaded item '{item.name}' because its trolley slot " +
                    $"{slot} is invalid.");
                continue;
            }

            item.trolleySlotIndex = slot;
            item.storedPosition = slot;
            DynamicTargetAllocator.SetTargetRotation(bay, slots[slot], item);
            TrolleyPhysicsIsolation.IgnoreStoredItem(item);
            item.MoveToStorage(slots[slot], slot, item.storageUID);
            item.isOnTrolley = true;
            TrolleyPhysicsIsolation.IgnoreStoredItem(item);
            TrolleyItemInteraction.SchedulePlacementFinalize(
                item, "save-load migration");
        }

        ModuleTrayLayout.Arrange(bay, "save-load migration");
        PatchPanelLayerLayout.Arrange(bay, "save-load migration");
        CableWheelLayout.Arrange(bay, "save-load migration");
        BoxLayerLayout.RebuildLayerReservations(bay);

        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Migrated {loadedItems.Count} loaded trolley item(s) onto " +
            $"{CartLayout.ServerSlots} server-group, " +
            $"{CartLayout.ModuleTraySlots} module-tray and " +
            $"{CartLayout.CableSlots} cable-wheel targets." +
            (TrolleyContext.PendingDisable
                ? " Disable remains pending until the trolley is empty."
                : string.Empty));
    }

    internal static void ScheduleDisableIfRequested()
    {
        if (!TrolleyContext.LayoutEnabled || ModSettings.RequestedEnabled)
        {
            TrolleyContext.PendingDisable = false;
            return;
        }

        TrolleyContext.PendingDisable = true;
        if (TrolleyContext.HasCargo())
            return;

        var token = ++_disableToken;
        MelonCoroutines.Start(DisableAfterNativeExtraction(token));
    }

    private static IEnumerator DisableAfterNativeExtraction(int token)
    {
        yield return new WaitForSeconds(DisableDelay);
        if (token != _disableToken ||
            ModSettings.RequestedEnabled ||
            TrolleyContext.HasCargo())
            yield break;

        RestoreNativeLayout();
    }

    private static void RestoreNativeLayout()
    {
        var bay = TrolleyContext.Current;
        if (bay is null ||
            bay.Pointer != _nativeBay ||
            _nativeTargets is null ||
            _nativeUsedPositions is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "The trolley became empty, but its native slot arrays could not " +
                "be restored safely. The mod remains active until the next load.");
            return;
        }

        for (var index = 0; index < _nativeUsedPositions.Length; index++)
            _nativeUsedPositions[index] = 0;
        bay.positionsOnTrolley = _nativeTargets;
        bay.usedPositions = _nativeUsedPositions;
        TrolleyPhysicsIsolation.Reset();
        TrolleyItemInteraction.Reset();
        TrolleyCompactor.ResetAnchors();
        BoxLayerLayout.Reset();
        PatchPanelLayerLayout.Reset();
        ModuleTrayLayout.Reset();
        CableWheelLayout.Reset();
        DynamicTargetAllocator.Reset();
        TrolleyContext.Items.Clear();
        TrolleyContext.LayoutEnabled = false;
        TrolleyContext.PendingDisable = false;
        Melon<CartItemStacker.Mod>.Logger.Msg(
            "The trolley is empty and Cart Item Stacker is now disabled.");
    }

    private static List<UsableObject> DiscoverLoadedItems(
        TrolleyLoadingBay bay)
    {
        var loadedItems = new List<UsableObject>();
        if (bay?.transform is null)
            return loadedItems;

        foreach (var item in UnityEngine.Object.FindObjectsOfType<UsableObject>())
        {
            if (item?.transform is null || !IsSupportedCargo(item))
                continue;
            if (item.transform.IsChildOf(bay.transform) ||
                IsLikelyStoredOnThisTrolley(bay, item))
                loadedItems.Add(item);
        }
        return loadedItems;
    }

    private static bool IsSupportedCargo(UsableObject item) =>
        ModuleTrayLayout.IsTray(item) ||
        PatchPanelLayerLayout.IsPatchPanel(item) ||
        CableWheelLayout.IsCableWheel(item) ||
        ServerSectionCatalog.IsAllowed(item, out _);

    private static bool IsLikelyStoredOnThisTrolley(
        TrolleyLoadingBay bay,
        UsableObject item)
    {
        try
        {
            if (!item.isOnTrolley ||
                item.objectInHands ||
                item.currentRackPosition is not null)
                return false;

            var local = bay.transform.InverseTransformPoint(
                item.transform.position);
            return System.Math.Abs(local.x) <= CargoEnvelopeHalfWidth &&
                System.Math.Abs(local.z) <= CargoEnvelopeHalfLength &&
                local.y >= CargoEnvelopeMinimumHeight &&
                local.y <= CargoEnvelopeMaximumHeight;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static int CountReservations(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used)
    {
        if (used is null)
            return 0;

        var count = 0;
        for (var index = 0; index < used.Length; index++)
            if (used[index] != 0)
                count++;
        return count;
    }

    private static int GetLoadedTargetSlot(UsableObject item)
    {
        if (BoxLayerLayout.IsBox(item))
        {
            var stack = BoxLayerLayout.GetItemStack(item);
            var ordinal = BoxLayerLayout.GetStackOrdinal(item, stack);
            if (stack < 0 || ordinal < 0)
                return -1;
            return stack * CartLayout.SlotsPerStack +
                BoxLayerLayout.GetServerHeight(stack) +
                ordinal / CartLayout.BoxesPerLayer * CartLayout.BoxLayerU;
        }

        if (PatchPanelLayerLayout.IsPatchPanel(item))
        {
            var stack = PatchPanelLayerLayout.GetItemStack(item);
            var ordinal = PatchPanelLayerLayout.GetItemOrdinal(item);
            if (stack < 0 || ordinal < 0)
                return -1;
            return stack * CartLayout.SlotsPerStack +
                BoxLayerLayout.GetOccupiedHeightWithoutPatchesU(stack) +
                ordinal / CartLayout.PatchPanelsPerLayer *
                    CartLayout.PatchLayerU;
        }

        return item.trolleySlotIndex;
    }

    private static void ApplyTargetPoses(TrolleyLoadingBay bay)
    {
        var slots = bay?.positionsOnTrolley;
        if (slots is null)
            return;

        for (var index = 0; index < slots.Length; index++)
        {
            var target = slots[index];
            if (target is null)
                continue;

            if (ModuleTrayLayout.IsAccessorySlot(index))
            {
                ModuleTrayLayout.ApplyTargetPose(bay, target, index);
                continue;
            }
            if (CableWheelLayout.IsCableSlot(index))
            {
                CableWheelLayout.ApplyTargetPose(bay, target, index);
                continue;
            }
            if (index >= CartLayout.ServerSlots)
                continue;

            var stack = index / SlotsPerStack;
            var level = index % SlotsPerStack;
            var localPosition = new Vector3(
                0f,
                BaseY + level * U,
                CartLayout.GetStackZ(stack));
            var localRotation = Quaternion.Euler(
                0f,
                CartLayout.GetServerYaw(stack),
                0f);
            target.SetPositionAndRotation(
                bay.transform.TransformPoint(localPosition),
                bay.transform.rotation * localRotation);
        }
    }

    private static void ExpandNativeTargets(TrolleyLoadingBay bay)
    {
        var oldTargets = bay.positionsOnTrolley;
        var oldUsed = bay.usedPositions;
        if (oldTargets is null || oldTargets.Length >= TotalSlots)
            return;

        var targets = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(TotalSlots);
        var used = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(TotalSlots);
        for (var i = 0; i < oldTargets.Length; i++)
        {
            targets[i] = oldTargets[i];
            if (oldUsed is not null && i < oldUsed.Length)
                used[i] = oldUsed[i];
        }

        for (var i = oldTargets.Length; i < TotalSlots; i++)
        {
            var holder = new GameObject($"CartItemStackerTarget_{i:000}");
            holder.transform.SetParent(bay.transform, false);
            targets[i] = holder.transform;
        }

        bay.positionsOnTrolley = targets;
        bay.usedPositions = used;
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Expanded trolley targets from {oldTargets.Length} to {TotalSlots} " +
            $"({CartLayout.ServerSlots} server-group U-slots, " +
            $"{CartLayout.ModuleTraySlots} module targets and " +
            $"{CartLayout.CableSlots} cable targets).");
    }
}
