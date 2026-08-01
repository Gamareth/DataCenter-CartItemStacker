using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class ModuleTrayLayout
{
    private enum TrayZone
    {
        Invalid,
        Active,
        Overflow,
        Empty
    }

    private readonly struct AccessoryCollisionPair
    {
        internal readonly Collider First;
        internal readonly Collider Second;
        internal readonly bool OwnedByMod;

        internal AccessoryCollisionPair(
            Collider first,
            Collider second,
            bool ownedByMod)
        {
            First = first;
            Second = second;
            OwnedByMod = ownedByMod;
        }
    }

    private const int ActiveStart = CartLayout.ServerSlots;
    private const int OverflowStart =
        ActiveStart + CartLayout.ActiveModuleTraySlots;
    private const int EmptyStart =
        ActiveStart + CartLayout.FilledModuleTraySlots;
    private const float ActiveTrayY = 0.715f;
    private const float OverflowTrayY = 0.6175f;
    private const float EmptyTrayY = 0.825f;
    private const float OverflowTraySpacing = 0.030f;
    private const float EmptyTraySpacing = OverflowTraySpacing;
    private const float OverflowRowStartShiftX = -0.0675f;
    private const float EmptyRowStartShiftX = OverflowRowStartShiftX;
    private const float TrayMoveDuration =
        LayoutConstants.TrayAnimationDuration;
    private const float NativePlacementDelay =
        LayoutConstants.NativePlacementDelay;
    private const float TypeSpacingX = 0.160f;
    private const float ActiveDistanceFromHandle = -0.065f;
    private const float OverflowDistanceFromHandle = -0.065f;
    private const float EmptyDistanceFromHandle = -0.005f;
    // Data Center maps ObjectInHand.SFPBox to native trolley profile index 1.
    // Use that same position and Euler-rotation profile when converting a
    // visible layout anchor into the tray prefab's definitive root pose.
    private const int NativeSfpBoxProfileIndex = 1;
    private static readonly Vector3 FallbackNativeRootPositionOffset =
        new(0f, -0.300f, 0f);
    private static float _handleX;
    private static float _handleZ = -0.677f;
    private static SFPBox _pendingTray;
    private static int _pendingSlot = -1;
    private static int _arrangeToken;
    private static int _movementToken;
    private static readonly List<AccessoryCollisionPair> CollisionPairs = new();

    internal static bool TryGetTray(UsableObject item, out SFPBox tray)
    {
        tray = item as SFPBox;
        if (tray is not null)
            return true;
        if (item is null || item.Pointer == System.IntPtr.Zero)
            return false;

        try
        {
            tray = item.GetComponent<SFPBox>();
            if (tray is null)
                tray = item.GetComponentInChildren<SFPBox>(true);
        }
        catch (System.Exception)
        {
            tray = null;
        }
        return tray is not null && tray.Pointer != System.IntPtr.Zero;
    }

    internal static bool IsTray(UsableObject item) =>
        TryGetTray(item, out _);

    internal static bool TryGetPendingSlot(
        UsableObject item,
        out int slot)
    {
        slot = -1;
        if (item is null ||
            _pendingTray is null ||
            item.Pointer != _pendingTray.Pointer ||
            !IsAccessorySlot(_pendingSlot))
            return false;

        slot = _pendingSlot;
        return true;
    }

    private static bool TryGetModule(
        UsableObject item,
        out SFPModule module)
    {
        // A filled SFPBox contains SFPModule child components. Classify the
        // complete tray first so its contents can never make the held tray
        // masquerade as a loose module.
        if (TryGetTray(item, out _))
        {
            module = null;
            return false;
        }

        module = item as SFPModule;
        if (module is not null)
            return true;
        if (item is null || item.Pointer == System.IntPtr.Zero)
            return false;

        try
        {
            module = item.GetComponent<SFPModule>();
            if (module is null)
                module = item.GetComponentInChildren<SFPModule>(true);
        }
        catch (System.Exception)
        {
            module = null;
        }
        return module is not null && module.Pointer != System.IntPtr.Zero;
    }

    internal static bool IsModule(UsableObject item) =>
        TryGetModule(item, out _);

    internal static bool IsAccessorySlot(int slot) =>
        slot >= ActiveStart && slot < CartLayout.CableStart;

    internal static void Reset()
    {
        RestoreAccessoryCollisions();
        _pendingTray = null;
        _pendingSlot = -1;
        _arrangeToken++;
        _movementToken++;
    }

    internal static void Configure(TrolleyLoadingBay bay)
    {
        _handleX = 0f;
        _handleZ = -0.677f;
        var handle = bay?.transform?.root?.GetComponentInChildren<PushTrolleyHandle>(true);
        if (bay?.transform is not null && handle?.transform is not null)
        {
            var local = bay.transform.InverseTransformPoint(handle.transform.position);
            _handleX = local.x;
            _handleZ = local.z;
        }

        ModSettings.Debug(
            $"Module tray layout uses handle-local origin " +
            $"({_handleX:0.000}, {_handleZ:0.000}); four active slots, " +
            $"{CartLayout.FilledOverflowTraySlots} shared filled-overflow slots " +
            $"and {CartLayout.EmptyModuleTraySlots} separate empty slots.");
    }

    internal static void RehydrateLoaded(
        TrolleyLoadingBay bay,
        IEnumerable<UsableObject> loadedItems)
    {
        if (bay?.transform is null || loadedItems is null)
            return;

        var filledByType = new[]
        {
            new List<SFPBox>(),
            new List<SFPBox>(),
            new List<SFPBox>(),
            new List<SFPBox>(),
        };
        var empty = new List<SFPBox>();
        foreach (var item in loadedItems)
        {
            if (!TryGetTray(item, out var tray) || tray?.transform is null)
                continue;

            var type = NormalizeType(tray);
            if (type < 0)
                continue;
            if (CountModules(tray) == 0)
                empty.Add(tray);
            else
                filledByType[type].Add(tray);
        }

        var overflow = new List<SFPBox>();
        for (var type = 0; type < filledByType.Length; type++)
        {
            var selectedType = type;
            filledByType[type].Sort((left, right) =>
                DistanceToActivePose(bay, left, selectedType).CompareTo(
                    DistanceToActivePose(bay, right, selectedType)));
            if (filledByType[type].Count == 0)
                continue;

            AssignLoadedSlot(
                bay,
                filledByType[type][0],
                ActiveStart + type);
            for (var index = 1; index < filledByType[type].Count; index++)
                overflow.Add(filledByType[type][index]);
        }

        SortBySavedRowPosition(bay, overflow);
        for (var index = 0;
            index < overflow.Count &&
            index < CartLayout.FilledOverflowTraySlots;
            index++)
        {
            AssignLoadedSlot(bay, overflow[index], OverflowStart + index);
        }

        SortBySavedRowPosition(bay, empty);
        for (var index = 0;
            index < empty.Count &&
            index < CartLayout.EmptyModuleTraySlots;
            index++)
        {
            AssignLoadedSlot(bay, empty[index], EmptyStart + index);
        }
    }

    private static float DistanceToActivePose(
        TrolleyLoadingBay bay,
        SFPBox tray,
        int type)
    {
        var local = bay.transform.InverseTransformPoint(
            tray.transform.position);
        var centeredType =
            type - (CartLayout.ModuleTypeCount - 1) * 0.5f;
        var expected = new Vector3(
            _handleX + centeredType * TypeSpacingX,
            ActiveTrayY + GetNativeRootPositionOffset(bay).y,
            _handleZ + ActiveDistanceFromHandle);
        return (local - expected).sqrMagnitude;
    }

    private static void SortBySavedRowPosition(
        TrolleyLoadingBay bay,
        List<SFPBox> trays)
    {
        trays.Sort((left, right) =>
        {
            var leftLocal = bay.transform.InverseTransformPoint(
                left.transform.position);
            var rightLocal = bay.transform.InverseTransformPoint(
                right.transform.position);
            return leftLocal.x.CompareTo(rightLocal.x);
        });
    }

    private static void AssignLoadedSlot(
        TrolleyLoadingBay bay,
        SFPBox tray,
        int slot)
    {
        tray.sizeInU = 1;
        tray.trolleySlotIndex = slot;
        tray.storedPosition = slot;
        tray.isOnTrolley = true;
        tray.transform.SetParent(bay.transform, true);
    }

    internal static int CountModules(SFPBox tray)
    {
        var used = tray?.usedPositions;
        if (used is null)
            return 0;

        var count = 0;
        for (var i = 0; i < used.Length; i++)
            if (used[i] != 0)
                count++;
        return count;
    }

    private static int NormalizeType(SFPBox tray)
    {
        var type = tray?.sfpBoxType ?? -1;
        return type >= 0 && type < CartLayout.ModuleTypeCount ? type : -1;
    }

    internal static bool PreparePlacement(TrolleyLoadingBay bay, SFPBox tray)
    {
        ClearPending();
        if (bay?.positionsOnTrolley is null || bay.usedPositions is null || tray is null)
            return false;

        var type = NormalizeType(tray);
        if (type < 0)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Rejected module tray with unsupported sfpBoxType={tray.sfpBoxType}.");
            return false;
        }

        var empty = CountModules(tray) == 0;
        int slot;
        if (empty)
        {
            var emptyCount = CountTrays(true, tray);
            if (emptyCount >= CartLayout.EmptyModuleTraySlots)
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Shared empty-tray row is full " +
                    $"({CartLayout.EmptyModuleTraySlots} trays).");
                return false;
            }
            slot = EmptyStart + emptyCount;
        }
        else
        {
            if (!CanAddFilledTray(type, tray))
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Shared filled-tray overflow is full " +
                    $"({CartLayout.FilledOverflowTraySlots} overflow trays).");
                return false;
            }

            var typeAlreadyActive = CountTrays(type, false, tray) > 0;
            slot = typeAlreadyActive
                ? OverflowStart + CountFilledOverflowTrays(tray)
                : ActiveStart + type;
        }
        if (slot < 0 || slot >= bay.positionsOnTrolley.Length)
            return false;

        tray.sizeInU = 1;
        TrolleyContext.Register(tray);
        SetTargetPose(bay, bay.positionsOnTrolley[slot], slot);

        // Force the game's native first-free scan to select the dedicated
        // accessory target. The real reservations are rebuilt immediately
        // after the click, so server capacity is not consumed.
        for (var i = 0; i < slot && i < bay.usedPositions.Length; i++)
            if (bay.usedPositions[i] == 0)
                bay.usedPositions[i] = 1;
        bay.usedPositions[slot] = 0;

        _pendingTray = tray;
        _pendingSlot = slot;
        ModSettings.Debug(
            $"Prepared {(empty ? "empty" : "filled")} m{type + 1} tray " +
            $"with {CountModules(tray)} module(s) for accessory slot {slot}.");
        return true;
    }

    internal static bool HasPendingPlacement => _pendingTray is not null;

    internal static void CompletePlacement(TrolleyLoadingBay bay)
    {
        if (_pendingTray is null)
            return;

        var tray = _pendingTray;
        var slot = _pendingSlot;
        var succeeded = DynamicTargetAllocator.GetHeldObject() is null;
        if (succeeded)
        {
            tray.trolleySlotIndex = slot;
            tray.storedPosition = slot;
            tray.isOnTrolley = true;
            ScheduleArrangeAfterPlacement(tray, "tray placement");
        }
        else
        {
            TrolleyContext.Unregister(tray);
            BoxLayerLayout.RebuildLayerReservations(bay);
            TrolleyPhysicsIsolation.RestoreItem(tray);
            RefreshAccessoryCollisions();
        }

        ModSettings.Debug(
            succeeded
                ? $"Module tray placement completed in slot {slot}."
                : "Module tray placement failed; accessory reservations restored.");
        ClearPending();
    }

    internal static bool CancelPendingPlacement(TrolleyLoadingBay bay)
    {
        if (_pendingTray is null)
            return false;

        var tray = _pendingTray;
        TrolleyContext.Unregister(tray);
        BoxLayerLayout.RebuildLayerReservations(bay);
        TrolleyPhysicsIsolation.RestoreItem(tray);
        RefreshAccessoryCollisions();
        ClearPending();
        return true;
    }

    private static void ClearPending()
    {
        _pendingTray = null;
        _pendingSlot = -1;
    }

    private static int CountTrays(
        int type,
        bool empty,
        UsableObject exclude)
    {
        var count = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray || item.objectInHands ||
                (exclude is not null && item.Pointer == exclude.Pointer) ||
                NormalizeType(tray) != type ||
                (CountModules(tray) == 0) != empty)
                continue;
            count++;
        }
        return count;
    }

    private static int CountTrays(bool empty, UsableObject exclude)
    {
        var count = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray || item.objectInHands ||
                (exclude is not null && item.Pointer == exclude.Pointer) ||
                (CountModules(tray) == 0) != empty)
                continue;
            count++;
        }
        return count;
    }

    private static int CountActiveTypes(UsableObject exclude)
    {
        var seen = new bool[CartLayout.ModuleTypeCount];
        var count = 0;
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray || item.objectInHands ||
                CountModules(tray) == 0 ||
                (exclude is not null && item.Pointer == exclude.Pointer))
                continue;

            var type = NormalizeType(tray);
            if (type < 0 || seen[type])
                continue;
            seen[type] = true;
            count++;
        }
        return count;
    }

    private static int CountFilledOverflowTrays(UsableObject exclude)
    {
        var filled = CountTrays(false, exclude);
        return System.Math.Max(0, filled - CountActiveTypes(exclude));
    }

    private static bool CanAddFilledTray(int type, UsableObject exclude)
    {
        if (type < 0 || type >= CartLayout.ModuleTypeCount)
            return false;

        // A new type gets its own active slot. Additional trays of an already
        // active type consume one of the shared overflow positions.
        if (CountTrays(type, false, exclude) == 0)
            return true;
        return CountFilledOverflowTrays(exclude) <
            CartLayout.FilledOverflowTraySlots;
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
        if (target is null ||
            !TryResolvePose(
                bay,
                slot,
                rootAdjusted: false,
                out var position,
                out var rotation))
            return;

        target.SetPositionAndRotation(position, rotation);
    }

    internal static bool TryResolveRootPose(
        TrolleyLoadingBay bay,
        int slot,
        out Vector3 position,
        out Quaternion rotation) =>
        TryResolvePose(
            bay,
            slot,
            rootAdjusted: true,
            out position,
            out rotation);

    internal static bool ApplyResolvedRootPose(
        TrolleyLoadingBay bay,
        Transform target,
        int slot)
    {
        if (target is null ||
            !TryResolveRootPose(
                bay,
                slot,
                out var position,
                out var rotation))
            return false;

        target.SetPositionAndRotation(position, rotation);
        return true;
    }

    private static bool TryResolvePose(
        TrolleyLoadingBay bay,
        int slot,
        bool rootAdjusted,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (bay?.transform is null || !IsAccessorySlot(slot))
            return false;

        var zone = GetZone(slot);
        Vector3 localPosition;
        Quaternion localRotation;
        if (zone == TrayZone.Active)
        {
            var type = slot - ActiveStart;
            var centeredType =
                type - (CartLayout.ModuleTypeCount - 1) * 0.5f;
            localPosition = new Vector3(
                _handleX + centeredType * TypeSpacingX,
                ActiveTrayY,
                _handleZ + ActiveDistanceFromHandle);
            localRotation = Quaternion.Euler(180f, 180f, 0f);
        }
        else
        {
            var ordinal = slot - (zone == TrayZone.Empty
                ? EmptyStart
                : OverflowStart);
            var firstRowSlotX = _handleX -
                (CartLayout.ModuleTypeCount - 1) * 0.5f * TypeSpacingX;
            if (zone == TrayZone.Overflow)
                firstRowSlotX += OverflowRowStartShiftX;
            else if (zone == TrayZone.Empty)
                firstRowSlotX += EmptyRowStartShiftX;
            var rowSpacing = zone == TrayZone.Empty
                ? EmptyTraySpacing
                : OverflowTraySpacing;
            localPosition = new Vector3(
                firstRowSlotX + ordinal * rowSpacing,
                zone == TrayZone.Empty ? EmptyTrayY : OverflowTrayY,
                _handleZ + (zone == TrayZone.Empty
                    ? EmptyDistanceFromHandle
                    : OverflowDistanceFromHandle));
            // Shared rows store trays upright like books.
            localRotation = Quaternion.Euler(180f, 180f, 0f) *
                Quaternion.Euler(0f, 0f, 90f);
        }
        if (rootAdjusted)
        {
            localPosition += GetNativeRootPositionOffset(bay);
            localRotation = Quaternion.Euler(
                localRotation.eulerAngles +
                GetNativeRootEulerOffset(bay));
        }
        position = bay.transform.TransformPoint(localPosition);
        rotation = bay.transform.rotation * localRotation;
        return true;
    }

    private static Vector3 GetNativeRootPositionOffset(
        TrolleyLoadingBay bay)
    {
        var offsets = bay?.additionalPositions;
        return offsets is not null &&
            offsets.Length > NativeSfpBoxProfileIndex
            ? offsets[NativeSfpBoxProfileIndex]
            : FallbackNativeRootPositionOffset;
    }

    private static Vector3 GetNativeRootEulerOffset(
        TrolleyLoadingBay bay)
    {
        var offsets = bay?.additionalRotations;
        return offsets is not null &&
            offsets.Length > NativeSfpBoxProfileIndex
            ? offsets[NativeSfpBoxProfileIndex]
            : Vector3.zero;
    }

    internal static void Arrange(TrolleyLoadingBay bay, string reason)
    {
        if (bay?.positionsOnTrolley is null)
            return;

        var movementToken = ++_movementToken;
        var overflow = new List<SFPBox>();
        for (var type = 0; type < CartLayout.ModuleTypeCount; type++)
        {
            var filled = Collect(type, false);
            filled.Sort(CompareFilledWithinType);
            if (filled.Count > 0)
            {
                PlaceList(
                    bay,
                    new List<SFPBox> { filled[0] },
                    TrayZone.Active,
                    ActiveStart + type,
                    1,
                    reason,
                    movementToken);
                for (var i = 1; i < filled.Count; i++)
                    overflow.Add(filled[i]);
            }
        }

        overflow.Sort(CompareFilledShared);
        PlaceList(
            bay,
            overflow,
            TrayZone.Overflow,
            OverflowStart,
            CartLayout.FilledOverflowTraySlots,
            reason,
            movementToken);

        var empty = CollectAll(true);
        empty.Sort((left, right) =>
        {
            var byType = NormalizeType(left).CompareTo(NormalizeType(right));
            if (byType != 0)
                return byType;
            return CurrentSlot(left).CompareTo(CurrentSlot(right));
        });
        PlaceList(
            bay,
            empty,
            TrayZone.Empty,
            EmptyStart,
            CartLayout.EmptyModuleTraySlots,
            reason,
            movementToken);

        BoxLayerLayout.RebuildLayerReservations(bay);
        RefreshAccessoryCollisions();
        LogTrayPoses(bay, reason);
    }

    private static void LogTrayPoses(
        TrolleyLoadingBay bay,
        string reason)
    {
        if (bay?.transform is null || bay.positionsOnTrolley is null)
            return;

        foreach (var item in TrolleyContext.Items)
        {
            if (!TryGetTray(item, out var tray) ||
                tray.objectInHands ||
                !tray.isOnTrolley ||
                tray.transform is null)
                continue;

            var slot = tray.trolleySlotIndex;
            if (!IsAccessorySlot(slot) ||
                slot >= bay.positionsOnTrolley.Length ||
                bay.positionsOnTrolley[slot] is null)
                continue;

            var actual =
                bay.transform.InverseTransformPoint(tray.transform.position);
            if (!TryResolveRootPose(
                bay,
                slot,
                out var expectedPosition,
                out _))
                continue;
            var expected =
                bay.transform.InverseTransformPoint(expectedPosition);
            ModSettings.Debug(
                $"Tray pose after {reason}: m{NormalizeType(tray) + 1}, " +
                $"slot {slot}, actual ({actual.x:0.000}, {actual.y:0.000}, " +
                $"{actual.z:0.000}), target ({expected.x:0.000}, " +
                $"{expected.y:0.000}, {expected.z:0.000}).");
        }
    }

    private static int CompareFilledWithinType(SFPBox left, SFPBox right)
    {
        // The least-filled non-empty tray is promoted to the easy-grab slot.
        var byFill = CountModules(left).CompareTo(CountModules(right));
        if (byFill != 0)
            return byFill;
        return CurrentSlot(left).CompareTo(CurrentSlot(right));
    }

    private static int CompareFilledShared(SFPBox left, SFPBox right)
    {
        var byType = NormalizeType(left).CompareTo(NormalizeType(right));
        if (byType != 0)
            return byType;
        return CompareFilledWithinType(left, right);
    }

    private static List<SFPBox> Collect(int type, bool empty)
    {
        var result = new List<SFPBox>();
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray || item.objectInHands ||
                !item.isOnTrolley || NormalizeType(tray) != type ||
                (CountModules(tray) == 0) != empty)
                continue;
            result.Add(tray);
        }
        return result;
    }

    private static List<SFPBox> CollectAll(bool empty)
    {
        var result = new List<SFPBox>();
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray || item.objectInHands ||
                !item.isOnTrolley ||
                (CountModules(tray) == 0) != empty)
                continue;
            result.Add(tray);
        }
        return result;
    }

    private static int CurrentSlot(SFPBox tray) =>
        tray?.trolleySlotIndex ?? int.MaxValue;

    private static TrayZone GetZone(int slot)
    {
        if (slot >= ActiveStart && slot < OverflowStart)
            return TrayZone.Active;
        if (slot >= OverflowStart && slot < EmptyStart)
            return TrayZone.Overflow;
        if (slot >= EmptyStart && slot < CartLayout.TotalSlots)
            return TrayZone.Empty;
        return TrayZone.Invalid;
    }

    private static void PlaceList(
        TrolleyLoadingBay bay,
        List<SFPBox> trays,
        TrayZone zone,
        int start,
        int capacity,
        string reason,
        int movementToken)
    {
        var count = System.Math.Min(trays.Count, capacity);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var tray = trays[ordinal];
            var slot = start + ordinal;
            // Accessory targets are configured once during trolley startup and
            // again only for a genuinely free native-placement slot. Stored
            // trays can remain parented to their original target. Mutating a
            // target here would therefore drag its current child while another
            // tray is being animated into that same slot.
            if (tray.trolleySlotIndex == slot)
                continue;

            if (!TryResolveRootPose(
                bay,
                slot,
                out var targetPosition,
                out var targetRotation))
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Could not resolve the {zone} root pose for tray slot " +
                    $"{slot}; reorder skipped.");
                continue;
            }

            tray.sizeInU = 1;
            tray.transform.SetParent(bay.transform, true);
            tray.trolleySlotIndex = slot;
            tray.storedPosition = slot;
            MelonCoroutines.Start(StoredCargoMotion.AnimateAbsolute(
                tray,
                targetPosition,
                targetRotation,
                ModSettings.GetAnimationDuration(TrayMoveDuration),
                StoredCargoMotion.PositionEasing.Linear,
                StoredCargoMotion.RotationMotion.Interpolate,
                () => movementToken == _movementToken));
            tray.isOnTrolley = true;
            TrolleyPhysicsIsolation.IgnoreStoredItem(tray);
            ModSettings.Debug(
                $"Reordered m{NormalizeType(tray) + 1} tray to " +
                $"{zone.ToString().ToLowerInvariant()} slot {slot} " +
                "using the shared resolved root pose " +
                $"({reason}).");
        }
    }

    private static void RefreshAccessoryCollisions()
    {
        // Incoming trays are isolated from stored cargo before native
        // placement. Once arrangement runs, MoveToStorage has removed their
        // rigidbody and persistent cargo-to-cargo ignore pairs are unnecessary.
        // Rebuilding every collider pair here grew quadratically (13k+ pairs
        // with a full row) and caused visible placement latency.
        RestoreAccessoryCollisions();
    }

    internal static void IsolateIncomingTray(SFPBox tray)
    {
        if (tray?.transform is null)
            return;

        var sourceColliders = tray.GetComponentsInChildren<Collider>(true);
        var changed = 0;
        foreach (var other in TrolleyContext.Items)
        {
            if (other is null || other.objectInHands || !other.isOnTrolley ||
                other.Pointer == tray.Pointer)
                continue;

            var otherColliders = other.GetComponentsInChildren<Collider>(true);
            foreach (var first in sourceColliders)
            {
                if (first is null)
                    continue;
                foreach (var second in otherColliders)
                {
                    if (second is null || first.Pointer == second.Pointer ||
                        IsAccessoryPairTracked(first, second))
                        continue;

                    var alreadyIgnored = Physics.GetIgnoreCollision(first, second);
                    if (!alreadyIgnored)
                    {
                        Physics.IgnoreCollision(first, second, true);
                        changed++;
                    }
                    CollisionPairs.Add(new AccessoryCollisionPair(
                        first, second, !alreadyIgnored));
                }
            }
        }

        Physics.SyncTransforms();
        ModSettings.Debug(
            $"Pre-placement isolation for '{tray.name}' added " +
            $"{changed} incoming-tray cargo collision pair(s).");
    }

    private static bool IsAccessoryPairTracked(Collider first, Collider second)
    {
        foreach (var pair in CollisionPairs)
        {
            if ((pair.First?.Pointer == first.Pointer &&
                    pair.Second?.Pointer == second.Pointer) ||
                (pair.First?.Pointer == second.Pointer &&
                    pair.Second?.Pointer == first.Pointer))
                return true;
        }
        return false;
    }

    private static void RestoreAccessoryCollisions()
    {
        foreach (var pair in CollisionPairs)
        {
            if (!pair.OwnedByMod || pair.First is null || pair.Second is null)
                continue;
            try
            {
                Physics.IgnoreCollision(pair.First, pair.Second, false);
            }
            catch (System.Exception)
            {
                // A module can be destroyed or replaced while its tray is stored.
            }
        }
        CollisionPairs.Clear();
    }

    internal static void ScheduleArrange(SFPBox tray, string reason)
    {
        if (tray is null || !tray.isOnTrolley ||
            !TrolleyRemovalPatch.BelongsToTrolley(TrolleyContext.Current, tray))
            return;

        var token = ++_arrangeToken;
        MelonCoroutines.Start(ArrangeNextFrame(tray, token, reason));
    }

    private static void ScheduleArrangeAfterPlacement(
        SFPBox tray,
        string reason)
    {
        if (tray is null || !tray.isOnTrolley)
            return;

        var token = ++_arrangeToken;
        MelonCoroutines.Start(ArrangeAfterPlacement(
            tray, token, reason));
    }

    internal static void BeforeModuleChange(SFPBox tray)
    {
        // Keep ignored cargo pairs active throughout the native module action.
        // Arrange refreshes the complete set atomically on the next frame.
        // Restoring here opened a physics window in which trays could collide
        // with neighbouring cargo and nudge the trolley.
    }

    private static IEnumerator ArrangeNextFrame(
        SFPBox tray,
        int token,
        string reason)
    {
        yield return null;
        if (token != _arrangeToken || tray is null || !tray.isOnTrolley)
            yield break;
        Arrange(TrolleyContext.Current, reason);
    }

    private static IEnumerator ArrangeAfterPlacement(
        SFPBox tray,
        int token,
        string reason)
    {
        yield return new WaitForSeconds(NativePlacementDelay);
        if (token != _arrangeToken ||
            tray is null ||
            !tray.isOnTrolley ||
            tray.objectInHands)
            yield break;
        Arrange(TrolleyContext.Current, reason);
    }
}

[HarmonyPatch(typeof(SFPBox), nameof(SFPBox.RemoveSFPFromBox))]
internal static class ModuleTrayRemoveModulePatch
{
    private static void Prefix(SFPBox __instance)
    {
        if (TrolleyContext.LayoutEnabled)
            ModuleTrayLayout.BeforeModuleChange(__instance);
    }

    private static void Postfix(SFPBox __instance)
    {
        if (TrolleyContext.LayoutEnabled)
            ModuleTrayLayout.ScheduleArrange(__instance, "module removed");
    }
}

[HarmonyPatch(typeof(SFPBox), nameof(SFPBox.ReturnSFPDirectly))]
internal static class ModuleTrayReturnModulePatch
{
    private static void Prefix(SFPBox __instance)
    {
        if (TrolleyContext.LayoutEnabled)
            ModuleTrayLayout.BeforeModuleChange(__instance);
    }

    private static void Postfix(SFPBox __instance, bool __result)
    {
        if (TrolleyContext.LayoutEnabled && __result)
        {
            ModuleTrayLayout.ScheduleArrange(__instance, "module returned");
        }
    }
}

[HarmonyPatch(typeof(SFPBox), nameof(SFPBox.InteractOnClick))]
internal static class ModuleTrayNativeInteractionPatch
{
    private static void Prefix(SFPBox __instance)
    {
        if (TrolleyContext.LayoutEnabled)
            ModuleTrayLayout.BeforeModuleChange(__instance);
    }

    private static void Postfix(SFPBox __instance)
    {
        if (TrolleyContext.LayoutEnabled)
            ModuleTrayLayout.ScheduleArrange(__instance, "native tray interaction");
    }
}
