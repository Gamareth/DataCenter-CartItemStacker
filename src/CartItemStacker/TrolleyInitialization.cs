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
    private const float EquipmentEnvelopeHalfWidth = 0.70f;
    private const float EquipmentEnvelopeMinimumZ = -0.82f;
    private const float EquipmentEnvelopeMaximumZ = 0.55f;
    private const float AccessoryEnvelopeHalfWidth = 0.72f;
    private const float AccessoryEnvelopeMinimumZ = -0.95f;
    private const float AccessoryEnvelopeMaximumZ = 0.82f;
    private const float CargoEnvelopeMinimumHeight = 0.04f;
    private const float CargoEnvelopeMaximumHeight = 2.60f;
    private const float DisableDelay = 0.70f;
    private const float StagingClearance = 0.08f;
    private const float StagingColumnSpacing = 0.13f;
    private const float StagingRowSpacing = 0.25f;

    private readonly struct LoadedColliderState
    {
        internal readonly Collider Collider;
        internal readonly bool Enabled;

        internal LoadedColliderState(Collider collider, bool enabled)
        {
            Collider = collider;
            Enabled = enabled;
        }
    }

    private sealed class StagedLoadedItem
    {
        internal readonly UsableObject Item;
        internal readonly int Slot;
        internal readonly int Phase;
        internal readonly int Order;
        internal readonly Quaternion OriginalRotation;
        internal readonly List<LoadedColliderState> Colliders = new();
        internal GameObject TargetObject;

        internal StagedLoadedItem(
            UsableObject item,
            int slot,
            int phase,
            int order)
        {
            Item = item;
            Slot = slot;
            Phase = phase;
            Order = order;
            OriginalRotation = item?.transform is null
                ? Quaternion.identity
                : item.transform.rotation;
        }
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>
        _nativeTargets;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>
        _nativeUsedPositions;
    private static System.IntPtr _nativeBay;
    private static int _disableToken;
    private static int _initializationToken;
    private static bool _loadCompletionPending;
    private static readonly HashSet<System.IntPtr> StagedLoadItems = new();

    private static void Postfix(TrolleyLoadingBay __instance)
    {
        CaptureNativeLayout(__instance);
        ResetManagedLayout(__instance);
        ScheduleInitialization(__instance, "trolley startup");
    }

    internal static void NativeLoadStarted()
    {
        _loadCompletionPending = true;
        _initializationToken++;
        ResetManagedLayout(TrolleyContext.Current);
    }

    internal static void NativeLoadCompleted()
    {
        _loadCompletionPending = false;
        var bay = TrolleyContext.Current;
        if (bay is null)
            return;

        CaptureNativeLayout(bay);
        ResetManagedLayout(bay);
        ScheduleInitialization(bay, "native load completion");
    }

    private static void CaptureNativeLayout(TrolleyLoadingBay bay)
    {
        if (bay is null)
            return;

        var sameBay = _nativeBay == bay.Pointer;
        var targets = bay.positionsOnTrolley;
        if (!sameBay || _nativeTargets is null ||
            (targets is not null && targets.Length < TotalSlots))
        {
            _nativeTargets = targets;
            _nativeUsedPositions = bay.usedPositions;
            _nativeBay = bay.Pointer;
        }
    }

    private static void ResetManagedLayout(TrolleyLoadingBay bay)
    {
        _disableToken++;
        TrolleyReconstructionLock.CancelAndRestore("layout reset");
        TrolleyInitializationIndicator.CancelAndRestore();
        TrolleyContext.Current = bay;
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
        StagedLoadItems.Clear();
    }

    private static void ScheduleInitialization(
        TrolleyLoadingBay bay,
        string reason)
    {
        if (bay is null)
            return;

        var token = ++_initializationToken;
        MelonCoroutines.Start(InitializeAfterSaveLoad(
            bay, token, reason));
    }

    private static IEnumerator InitializeAfterSaveLoad(
        TrolleyLoadingBay bay,
        int token,
        string reason)
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
            if (token != _initializationToken)
                yield break;
            if (_loadCompletionPending || NativeSaveLifecycle.LoadInProgress ||
                bay is null ||
                bay.positionsOnTrolley is null ||
                bay.usedPositions is null)
                continue;

            CartLayout.ConfigureHandleSide(bay);
            ModuleTrayLayout.Configure(bay);

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

        if (token != _initializationToken)
            yield break;
        if (_loadCompletionPending || NativeSaveLifecycle.LoadInProgress ||
            bay is null ||
            bay.positionsOnTrolley is null ||
            bay.usedPositions is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Error(
                "Trolley initialization did not become ready within three seconds.");
            yield break;
        }

        var loadedItems = DiscoverLoadedItems(bay);
        LogLoadedCargoSummary(bay, loadedItems);
        if (!ModSettings.RequestedEnabled && loadedItems.Count == 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "Cart Item Stacker is disabled; the empty trolley remains native.");
            yield break;
        }

        ExpandNativeTargets(bay);
        ApplyTargetPoses(bay);

        foreach (var item in loadedItems)
            TrolleyContext.Register(item);

        TrolleyPhysicsDiagnostics.ArmInitialization(
            bay, loadedItems, reason);
        TrolleyReconstructionLock.Begin(bay);

        var equipmentCount =
            LoadedEquipmentLayout.RehydrateLoaded(bay, loadedItems);
        BoxLayerLayout.RehydrateLoaded(bay, loadedItems);
        PatchPanelLayerLayout.RehydrateLoaded(bay, loadedItems);
        ModuleTrayLayout.RehydrateLoaded(bay, loadedItems);
        CableWheelLayout.RehydrateLoaded(bay, loadedItems);

        var slots = bay.positionsOnTrolley;
        var stagedItems = CreateStagedLoadedItems(bay, loadedItems, slots);
        StageLoadedItems(bay, stagedItems);
        TrolleyPhysicsDiagnostics.LogInitializationStep(
            bay, "after staging");

        // Loaded cargo has now been identified and moved away, so its disabled
        // colliders can be permanently excluded from the chassis snapshot.
        TrolleyPhysicsIsolation.Attach(bay);
        ClearReservations(bay.usedPositions);
        TrolleyContext.LayoutEnabled = true;
        TrolleyContext.PendingDisable = !ModSettings.RequestedEnabled;
        TrolleyInitializationIndicator.Begin(bay);
        CargoMovementIndicator.Begin(loadedItems, "save-load reconstruction");

        var reconstructed = 0;
        var startedItems = new List<StagedLoadedItem>();
        var lanes = CreateReconstructionLanes(stagedItems);
        var laneIndexes = new int[lanes.Length];
        var reconstructionRound = 0;
        while (HasRemainingLaneItems(lanes, laneIndexes))
        {
            if (token != _initializationToken ||
                _loadCompletionPending ||
                NativeSaveLifecycle.LoadInProgress)
            {
                DestroyStagedTargets(startedItems);
                TrolleyReconstructionLock.CancelAndRestore(
                    "cancelled reconstruction");
                TrolleyInitializationIndicator.CancelAndRestore();
                CargoMovementIndicator.Reset();
                yield break;
            }

            var currentRound = new List<StagedLoadedItem>();
            for (var lane = 0; lane < lanes.Length; lane++)
            {
                while (laneIndexes[lane] < lanes[lane].Count)
                {
                    var staged = lanes[lane][laneIndexes[lane]++];
                    if (!StartStagedItem(bay, slots, staged))
                        continue;

                    startedItems.Add(staged);
                    currentRound.Add(staged);
                    break;
                }
            }
            if (currentRound.Count == 0)
                break;

            yield return new WaitForSeconds(
                LayoutConstants.NativePlacementDelay);
            if (token != _initializationToken ||
                _loadCompletionPending ||
                NativeSaveLifecycle.LoadInProgress)
            {
                DestroyStagedTargets(startedItems);
                TrolleyReconstructionLock.CancelAndRestore(
                    "cancelled reconstruction");
                TrolleyInitializationIndicator.CancelAndRestore();
                CargoMovementIndicator.Reset();
                yield break;
            }

            foreach (var staged in currentRound)
            {
                if (FinalizeStagedItem(
                    bay,
                    staged,
                    reconstructed + 1,
                    stagedItems.Count))
                    reconstructed++;
            }
            reconstructionRound++;
            TrolleyPhysicsDiagnostics.LogInitializationStep(
                bay,
                $"after reconstruction round {reconstructionRound}");
        }

        ModuleTrayLayout.Arrange(bay, "save-load migration");
        PatchPanelLayerLayout.Arrange(bay, "save-load migration");
        CableWheelLayout.Arrange(bay, "save-load migration");
        BoxLayerLayout.RebuildLayerReservations(bay);
        TrolleyPhysicsDiagnostics.LogInitializationStep(
            bay, "after final arrangement before collider restore");
        yield return null;
        RestoreStagedColliders(stagedItems);
        Physics.SyncTransforms();
        TrolleyPhysicsDiagnostics.LogInitializationStep(
            bay, "after collider restore while trolley remains locked");
        yield return new WaitForFixedUpdate();
        TrolleyPhysicsDiagnostics.LogInitializationStep(
            bay, "after protected physics step before unlock");
        TrolleyReconstructionLock.End("successful reconstruction");
        TrolleyPhysicsDiagnostics.CompleteInitialization(bay);
        TrolleyInitializationIndicator.ShowReady();
        CargoMovementIndicator.ShowReady(
            loadedItems, "successful save-load reconstruction");
        MelonCoroutines.Start(AuditLoadedParentsAfterDelay(
            bay, loadedItems));

        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Migrated {loadedItems.Count} loaded trolley item(s) onto " +
            $"{CartLayout.ServerSlots} server-group, " +
            $"{CartLayout.ModuleTraySlots} module-tray and " +
            $"{CartLayout.CableSlots} cable-wheel targets." +
            (TrolleyContext.PendingDisable
                ? " Disable remains pending until the trolley is empty."
                : string.Empty) +
            $" Trigger: {reason}; equipment items reconstructed: " +
            $"{equipmentCount}; sequentially rebuilt: " +
            $"{reconstructed}/{stagedItems.Count}.");
    }

    private static List<StagedLoadedItem>[] CreateReconstructionLanes(
        List<StagedLoadedItem> stagedItems)
    {
        var serverZone = new List<StagedLoadedItem>();
        var moduleZone = new List<StagedLoadedItem>();
        var cableZone = new List<StagedLoadedItem>();
        foreach (var staged in stagedItems)
        {
            if (staged.Phase <= 2)
                serverZone.Add(staged);
            else if (staged.Phase == 3)
                moduleZone.Add(staged);
            else
                cableZone.Add(staged);
        }

        ModSettings.Debug(
            $"Prepared parallel reconstruction lanes: server zone " +
            $"{serverZone.Count}, module zone {moduleZone.Count}, cable zone " +
            $"{cableZone.Count} item(s).");
        return new[] { serverZone, moduleZone, cableZone };
    }

    private static bool HasRemainingLaneItems(
        List<StagedLoadedItem>[] lanes,
        int[] laneIndexes)
    {
        for (var lane = 0; lane < lanes.Length; lane++)
            if (laneIndexes[lane] < lanes[lane].Count)
                return true;
        return false;
    }

    private static bool StartStagedItem(
        TrolleyLoadingBay bay,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>
            slots,
        StagedLoadedItem staged)
    {
        var item = staged?.Item;
        var slot = staged?.Slot ?? -1;
        if (item?.transform is null ||
            slots is null ||
            slot < 0 ||
            slot >= slots.Length ||
            slots[slot] is null)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Skipped staged item '{item?.name ?? "<destroyed>"}' " +
                $"because its trolley slot {slot} became invalid.");
            return false;
        }

        var targetObject = new GameObject(
            $"CartItemStackerLoadTarget_{item.Pointer.ToInt64():X}_{slot}");
        staged.TargetObject = targetObject;
        targetObject.transform.SetParent(bay.transform, false);
        targetObject.transform.SetPositionAndRotation(
            slots[slot].position,
            slots[slot].rotation);

        item.trolleySlotIndex = slot;
        item.storedPosition = slot;
        item.isOnTrolley = false;
        TrolleyPhysicsIsolation.IgnoreStoredItem(item);
        item.MoveToStorage(
            targetObject.transform,
            slot,
            item.storageUID);
        return true;
    }

    private static void LogLoadedCargoSummary(
        TrolleyLoadingBay bay,
        List<UsableObject> loadedItems)
    {
        var equipment = 0;
        var boxes = 0;
        var panels = 0;
        var trays = 0;
        var modules = 0;
        var spools = 0;
        var parented = 0;
        foreach (var item in loadedItems)
        {
            if (item?.transform is not null &&
                bay?.transform is not null &&
                item.transform.IsChildOf(bay.transform))
                parented++;

            if (ModuleTrayLayout.TryGetTray(item, out var tray))
            {
                trays++;
                modules += ModuleTrayLayout.CountModules(tray);
            }
            else if (CableWheelLayout.IsCableWheel(item))
                spools++;
            else if (PatchPanelLayerLayout.IsPatchPanel(item))
                panels++;
            else if (BoxLayerLayout.IsBox(item))
                boxes++;
            else
                equipment++;
        }

        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Loaded cargo discovery: {equipment} equipment, {boxes} boxed " +
            $"racks, {panels} patch panels, {trays} SFP trays containing " +
            $"{modules} modules, and {spools} cable spools. " +
            $"Trolley-parented before reconstruction: " +
            $"{parented}/{loadedItems.Count}.");
    }

    private static IEnumerator AuditLoadedParentsAfterDelay(
        TrolleyLoadingBay bay,
        List<UsableObject> loadedItems)
    {
        yield return new WaitForSeconds(
            LayoutConstants.NativePlacementDelay + 0.15f);
        if (bay?.transform is null)
            yield break;

        var valid = 0;
        var parented = 0;
        var ownRigidbodies = 0;
        var kinematicRigidbodies = 0;
        var missing = new List<string>();
        var missingRigidbodies = new List<string>();
        foreach (var item in loadedItems)
        {
            if (item?.transform is null || item.objectInHands)
                continue;
            valid++;
            if (item.transform.IsChildOf(bay.transform))
                parented++;
            else
                missing.Add(item.name);

            var body = item.GetComponent<Rigidbody>();
            if (body is null)
            {
                missingRigidbodies.Add(item.name);
                continue;
            }

            ownRigidbodies++;
            if (body.isKinematic)
                kinematicRigidbodies++;
        }

        var summary =
            $"Loaded cargo parent audit after placement: {parented}/{valid} " +
            $"item(s) attached to the trolley; {ownRigidbodies}/{valid} " +
            $"have their own Rigidbody and {kinematicRigidbodies}/" +
            $"{ownRigidbodies} are kinematic.";
        if (missing.Count == 0 && missingRigidbodies.Count == 0)
            Melon<CartItemStacker.Mod>.Logger.Msg(summary);
        else
            Melon<CartItemStacker.Mod>.Logger.Warning(
                summary +
                (missing.Count > 0
                    ? " Missing trolley parents: " +
                        string.Join(", ", missing) + "."
                    : string.Empty) +
                (missingRigidbodies.Count > 0
                    ? " Missing item Rigidbodies: " +
                        string.Join(", ", missingRigidbodies) + "."
                    : string.Empty));
    }

    private static List<StagedLoadedItem> CreateStagedLoadedItems(
        TrolleyLoadingBay bay,
        List<UsableObject> loadedItems,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>
            slots)
    {
        var stagedItems = new List<StagedLoadedItem>();
        if (bay?.transform is null || loadedItems is null || slots is null)
            return stagedItems;

        foreach (var item in loadedItems)
        {
            if (item?.transform is null)
                continue;

            var slot = GetLoadedTargetSlot(item);
            if (slot < 0 || slot >= slots.Length || slots[slot] is null)
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Could not stage loaded item '{item.name}' because its " +
                    $"trolley slot {slot} is invalid.");
                continue;
            }

            var phase = GetReconstructionPhase(item);
            var order = GetReconstructionOrder(item, slot, phase);
            stagedItems.Add(new StagedLoadedItem(
                item, slot, phase, order));
        }

        stagedItems.Sort((left, right) =>
        {
            var comparison = left.Phase.CompareTo(right.Phase);
            if (comparison != 0)
                return comparison;
            comparison = left.Order.CompareTo(right.Order);
            if (comparison != 0)
                return comparison;
            comparison = left.Slot.CompareTo(right.Slot);
            if (comparison != 0)
                return comparison;
            return string.Compare(
                left.Item?.name,
                right.Item?.name,
                System.StringComparison.OrdinalIgnoreCase);
        });
        return stagedItems;
    }

    private static int GetReconstructionPhase(UsableObject item)
    {
        if (BoxLayerLayout.IsBox(item))
            return 1;
        if (PatchPanelLayerLayout.IsPatchPanel(item))
            return 2;
        if (ModuleTrayLayout.IsTray(item))
            return 3;
        if (CableWheelLayout.IsCableWheel(item))
            return 4;
        return 0;
    }

    private static int GetReconstructionOrder(
        UsableObject item,
        int slot,
        int phase)
    {
        if (phase == 1)
        {
            var stack = BoxLayerLayout.GetItemStack(item);
            var ordinal = BoxLayerLayout.GetStackOrdinal(item, stack);
            return stack * 1000 + System.Math.Max(0, ordinal);
        }

        if (phase == 2)
        {
            var stack = PatchPanelLayerLayout.GetItemStack(item);
            var ordinal = PatchPanelLayerLayout.GetItemOrdinal(item);
            return stack * 1000 + System.Math.Max(0, ordinal);
        }

        return slot;
    }

    private static void StageLoadedItems(
        TrolleyLoadingBay bay,
        List<StagedLoadedItem> stagedItems)
    {
        if (bay?.transform is null || stagedItems is null)
            return;

        var rigidbodyCount = 0;
        for (var index = 0; index < stagedItems.Count; index++)
        {
            var staged = stagedItems[index];
            var item = staged.Item;
            if (item?.transform is null)
                continue;

            foreach (var collider in
                item.GetComponentsInChildren<Collider>(true))
            {
                if (collider is null)
                    continue;
                staged.Colliders.Add(new LoadedColliderState(
                    collider, collider.enabled));
                collider.enabled = false;
            }

            if (StoredCargoPhysics.EnsureOwnKinematicBody(
                    item, "save-load staging"))
                rigidbodyCount++;
            else
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Loaded item '{item.name}' could not enter the canonical " +
                    "stored-cargo physics state; its colliders will remain " +
                    "disabled.");
            }

            item.transform.SetParent(null, true);
            var column = index % 6;
            var row = index / 6;
            var rowCount = (stagedItems.Count + 5) / 6;
            var localPosition = new Vector3(
                (column - 2.5f) * StagingColumnSpacing,
                BaseY + SlotsPerStack * U + StagingClearance,
                (row - (rowCount - 1) * 0.5f) * StagingRowSpacing);
            item.transform.SetPositionAndRotation(
                bay.transform.TransformPoint(localPosition),
                staged.OriginalRotation);
            RaiseRendererBottomToStagingFloor(bay, item);
            item.isOnTrolley = false;
            StagedLoadItems.Add(item.Pointer);
        }

        Physics.SyncTransforms();
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Staged {stagedItems.Count} loaded trolley item(s) above the " +
            $"{SlotsPerStack}U cargo envelope with all cargo colliders " +
            $"disabled; " +
            $"{rigidbodyCount}/{stagedItems.Count} retain their own Rigidbody.");
    }

    private static void RaiseRendererBottomToStagingFloor(
        TrolleyLoadingBay bay,
        UsableObject item)
    {
        if (bay?.transform is null || item?.transform is null)
            return;

        var renderers = item.GetComponentsInChildren<Renderer>(true);
        var found = false;
        var bounds = new Bounds();
        foreach (var renderer in renderers)
        {
            if (renderer is null)
                continue;
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!found)
            return;

        var stagingFloor = bay.transform.TransformPoint(new Vector3(
            0f,
            BaseY + SlotsPerStack * U + StagingClearance,
            0f));
        item.transform.position += Vector3.up *
            (stagingFloor.y - bounds.min.y);
    }

    private static bool FinalizeStagedItem(
        TrolleyLoadingBay bay,
        StagedLoadedItem staged,
        int sequence,
        int total)
    {
        var item = staged?.Item;
        if (item?.transform is null)
        {
            if (staged?.TargetObject is not null)
                UnityEngine.Object.Destroy(staged.TargetObject);
            return false;
        }

        item.transform.SetParent(bay.transform, true);
        item.isOnTrolley = true;
        TrolleyItemInteraction.Restore(
            item, "pipelined save-load reconstruction");
        if (PatchPanelLayerLayout.IsPatchPanel(item))
            PatchPanelLayerLayout.NormalizeStoredPose(bay, item);
        ReassertStagedLoadSuppression(item);
        TrolleyPhysicsIsolation.Reassert(item);
        if (staged.TargetObject is not null)
        {
            UnityEngine.Object.Destroy(staged.TargetObject);
            staged.TargetObject = null;
        }
        ModSettings.Debug(
            $"Sequentially reconstructed '{item.name}' in phase " +
            $"{staged.Phase}, slot {staged.Slot} ({sequence}/{total}).");
        return true;
    }

    private static void DestroyStagedTargets(
        List<StagedLoadedItem> stagedItems)
    {
        if (stagedItems is null)
            return;
        foreach (var staged in stagedItems)
        {
            if (staged?.TargetObject is null)
                continue;
            UnityEngine.Object.Destroy(staged.TargetObject);
            staged.TargetObject = null;
        }
    }

    private static void RestoreStagedColliders(
        List<StagedLoadedItem> stagedItems)
    {
        if (stagedItems is null)
            return;

        var restoredItems = 0;
        var restoredColliders = 0;
        foreach (var staged in stagedItems)
        {
            var item = staged.Item;
            if (item?.transform is null)
                continue;

            if (!StoredCargoPhysics.EnsureOwnKinematicBody(
                    item, "save-load collider restore"))
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Kept colliders disabled for loaded item '{item.name}' " +
                    "because it no longer has its own Rigidbody.");
                continue;
            }

            TrolleyPhysicsIsolation.IgnoreStoredItem(item);
            foreach (var state in staged.Colliders)
            {
                if (state.Collider is null)
                    continue;
                state.Collider.enabled = state.Enabled;
                if (state.Enabled)
                    restoredColliders++;
            }
            TrolleyPhysicsIsolation.Reassert(item);
            StagedLoadItems.Remove(item.Pointer);
            restoredItems++;
        }

        Physics.SyncTransforms();
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Restored {restoredColliders} collider(s) across " +
            $"{restoredItems}/{stagedItems.Count} sequentially rebuilt item(s).");
    }

    private static void ClearReservations(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> used)
    {
        if (used is null)
            return;
        for (var index = 0; index < used.Length; index++)
            used[index] = 0;
    }

    internal static bool ReassertStagedLoadSuppression(UsableObject item)
    {
        if (item?.transform is null ||
            !StagedLoadItems.Contains(item.Pointer))
            return false;

        foreach (var collider in
            item.GetComponentsInChildren<Collider>(true))
        {
            if (collider is not null)
                collider.enabled = false;
        }
        return true;
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
            if (IsLikelyStoredOnThisTrolley(bay, item))
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
            if (item.objectInHands ||
                item.currentRackPosition is not null)
                return false;

            if (item.transform.IsChildOf(bay.transform) || item.isOnTrolley)
                return true;

            var local = bay.transform.InverseTransformPoint(
                item.transform.position);
            if (local.y < CargoEnvelopeMinimumHeight ||
                local.y > CargoEnvelopeMaximumHeight)
                return false;

            if (ModuleTrayLayout.IsTray(item) ||
                CableWheelLayout.IsCableWheel(item))
            {
                return System.Math.Abs(local.x) <=
                        AccessoryEnvelopeHalfWidth &&
                    local.z >= AccessoryEnvelopeMinimumZ &&
                    local.z <= AccessoryEnvelopeMaximumZ;
            }

            return System.Math.Abs(local.x) <=
                    EquipmentEnvelopeHalfWidth &&
                local.z >= EquipmentEnvelopeMinimumZ &&
                local.z <= EquipmentEnvelopeMaximumZ;
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
