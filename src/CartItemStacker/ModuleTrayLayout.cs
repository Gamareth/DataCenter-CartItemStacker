using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class ModuleTrayLayout
{
    private readonly struct TrayPose
    {
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;

        internal TrayPose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

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

    private static bool TryGetModule(
        UsableObject item,
        out SFPModule module)
    {
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

    internal static bool IsNativeModuleReturn(
        UsableObject held,
        UsableObject target)
    {
        if (!TryGetModule(held, out var module) ||
            !TryGetTray(target, out var tray))
            return false;

        try
        {
            return tray.CanAcceptSFP(module.sfpType);
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not evaluate native SFP return: {exception.Message}");
            return false;
        }
    }

    internal static bool IsModuleTrayInteraction(
        UsableObject held,
        UsableObject target) =>
        TryGetModule(held, out _) && TryGetTray(target, out _);

    internal static bool TryRouteModuleToEmptyTray(
        UsableObject held,
        UsableObject clickedTarget,
        out SFPBox destination)
    {
        destination = null;
        if (!TryGetModule(held, out var module) ||
            !TryGetTray(clickedTarget, out var clickedTray))
            return false;

        // Only provide overflow routing when the clicked tray itself cannot
        // accept the module. A compatible non-full tray keeps native behavior.
        try
        {
            if (clickedTray.CanAcceptSFP(module.sfpType))
                return false;
        }
        catch (System.Exception)
        {
            return false;
        }

        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox candidate ||
                candidate.objectInHands ||
                !candidate.isOnTrolley ||
                CountModules(candidate) != 0)
                continue;

            var type = NormalizeType(candidate);
            if (type < 0 || !CanAddFilledTray(type, candidate))
                continue;

            bool accepts;
            try
            {
                accepts = candidate.CanAcceptSFP(module.sfpType);
            }
            catch (System.Exception)
            {
                continue;
            }
            if (!accepts)
                continue;

            BeforeModuleChange(candidate);
            try
            {
                if (!candidate.ReturnSFPDirectly(module))
                    continue;
            }
            catch (System.Exception exception)
            {
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"Empty-tray overflow return failed: {exception.Message}");
                continue;
            }

            destination = candidate;
            ScheduleArrange(candidate, "full tray overflow");
            return true;
        }

        return false;
    }

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
            FixAllModuleOrientations(tray);
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
        if (bay?.transform is null || target is null || !IsAccessorySlot(slot))
            return;

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
        target.SetPositionAndRotation(
            bay.transform.TransformPoint(localPosition),
            bay.transform.rotation * localRotation);
    }

    internal static void Arrange(TrolleyLoadingBay bay, string reason)
    {
        if (bay?.positionsOnTrolley is null)
            return;

        var movementToken = ++_movementToken;
        var provenPoses = CaptureProvenPoses();
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
                    movementToken,
                    provenPoses);
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
            movementToken,
            provenPoses);

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
            movementToken,
            provenPoses);

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
            var expected =
                bay.transform.InverseTransformPoint(
                    bay.positionsOnTrolley[slot].position);
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

    private static int CurrentOrdinal(SFPBox tray, TrayZone zone)
    {
        var slot = tray?.trolleySlotIndex ?? -1;
        if (GetZone(slot) != zone)
            return int.MaxValue;
        return slot - (zone == TrayZone.Active
            ? ActiveStart
            : zone == TrayZone.Overflow
                ? OverflowStart
                : EmptyStart);
    }

    private static void PlaceList(
        TrolleyLoadingBay bay,
        List<SFPBox> trays,
        TrayZone zone,
        int start,
        int capacity,
        string reason,
        int movementToken,
        Dictionary<int, TrayPose> provenPoses)
    {
        var count = System.Math.Min(trays.Count, capacity);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var tray = trays[ordinal];
            var slot = start + ordinal;
            var target = bay.positionsOnTrolley[slot];
            var currentOrdinal = CurrentOrdinal(tray, zone);
            // Accessory targets are configured once during trolley startup and
            // again only for a genuinely free native-placement slot. Stored
            // trays can remain parented to their original target. Mutating a
            // target here would therefore drag its current child while another
            // tray is being animated into that same slot.
            FixAllModuleOrientations(tray);
            if (tray.trolleySlotIndex == slot)
                continue;

            if (!TryGetProvenPose(
                bay,
                provenPoses,
                zone,
                slot,
                out var targetPosition,
                out var targetRotation))
            {
                targetPosition = target.position;
                targetRotation = target.rotation;
                Melon<CartItemStacker.Mod>.Logger.Warning(
                    $"No proven {zone} pose available for tray slot {slot}; " +
                    "using the configured target as fallback.");
            }

            tray.sizeInU = 1;
            tray.transform.SetParent(bay.transform, true);
            tray.trolleySlotIndex = slot;
            tray.storedPosition = slot;
            MelonCoroutines.Start(MoveTrayWorldPose(
                tray,
                targetPosition,
                targetRotation,
                movementToken));
            tray.isOnTrolley = true;
            TrolleyPhysicsIsolation.IgnoreStoredItem(tray);
            ModSettings.Debug(
                $"Reordered m{NormalizeType(tray) + 1} tray to " +
                $"{zone.ToString().ToLowerInvariant()} slot {slot} " +
                "using an occupied destination-zone pose " +
                $"({reason}).");
        }
    }

    private static bool TryGetProvenPose(
        TrolleyLoadingBay bay,
        Dictionary<int, TrayPose> provenPoses,
        TrayZone zone,
        int desiredSlot,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (bay?.transform is null)
            return false;

        // During a reorder the destination is commonly still occupied by the
        // tray that will move next. Its root pose includes Data Center's
        // prefab-specific storage offset and is therefore the best template.
        if (provenPoses is not null &&
            provenPoses.TryGetValue(desiredSlot, out var exactPose))
        {
            position = exactPose.Position;
            rotation = exactPose.Rotation;
            return true;
        }

        // After extraction the exact destination can be empty. Extrapolate
        // from another proven pose in the same zone along the cart-local row.
        if (provenPoses is null)
            return false;
        foreach (var pair in provenPoses)
        {
            if (GetZone(pair.Key) != zone)
                continue;

            var slotDelta = desiredSlot - pair.Key;
            var spacing = zone == TrayZone.Active
                ? TypeSpacingX
                : zone == TrayZone.Empty
                    ? EmptyTraySpacing
                    : OverflowTraySpacing;
            position = pair.Value.Position +
                bay.transform.TransformVector(new Vector3(
                    slotDelta * spacing,
                    0f,
                    0f));
            rotation = pair.Value.Rotation;
            return true;
        }

        return false;
    }

    private static Dictionary<int, TrayPose> CaptureProvenPoses()
    {
        var result = new Dictionary<int, TrayPose>();
        foreach (var item in TrolleyContext.Items)
        {
            if (item is not SFPBox tray ||
                tray.objectInHands ||
                !tray.isOnTrolley ||
                tray.transform is null ||
                GetZone(tray.trolleySlotIndex) == TrayZone.Invalid)
                continue;

            result[tray.trolleySlotIndex] = new TrayPose(
                tray.transform.position,
                tray.transform.rotation);
        }
        return result;
    }

    private static IEnumerator MoveTrayWorldPosition(
        SFPBox tray,
        Vector3 targetPosition,
        Quaternion fixedRotation,
        int movementToken)
    {
        if (tray?.transform is null)
            yield break;

        var startPosition = tray.transform.position;
        var elapsed = 0f;
        var duration = ModSettings.GetAnimationDuration(TrayMoveDuration);
        while (elapsed < duration)
        {
            yield return null;
            if (movementToken != _movementToken ||
                tray is null ||
                tray.objectInHands ||
                tray.transform is null)
                yield break;

            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / duration);
            tray.transform.SetPositionAndRotation(
                Vector3.Lerp(startPosition, targetPosition, progress),
                fixedRotation);
        }

        if (movementToken == _movementToken &&
            tray is not null &&
            !tray.objectInHands &&
            tray.transform is not null)
            tray.transform.SetPositionAndRotation(
                targetPosition, fixedRotation);
    }

    private static IEnumerator MoveTrayWorldPose(
        SFPBox tray,
        Vector3 targetPosition,
        Quaternion targetRotation,
        int movementToken)
    {
        if (tray?.transform is null)
            yield break;

        var startPosition = tray.transform.position;
        var startRotation = tray.transform.rotation;
        var elapsed = 0f;
        var duration = ModSettings.GetAnimationDuration(TrayMoveDuration);
        while (elapsed < duration)
        {
            yield return null;
            if (movementToken != _movementToken ||
                tray is null ||
                tray.objectInHands ||
                tray.transform is null)
                yield break;

            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / duration);
            tray.transform.SetPositionAndRotation(
                Vector3.Lerp(startPosition, targetPosition, progress),
                Quaternion.Slerp(startRotation, targetRotation, progress));
        }

        if (movementToken == _movementToken &&
            tray is not null &&
            !tray.objectInHands &&
            tray.transform is not null)
            tray.transform.SetPositionAndRotation(
                targetPosition, targetRotation);
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

    internal static void FixReturnedModuleOrientation(SFPModule module)
    {
        if (module?.transform is null)
            return;

        // The native return method resets this to Quaternion.identity. In this
        // prefab the module's length/insertion direction is local Z. Rolling
        // around Z corrects top/bottom without reversing the insertion axis or
        // moving the connector through the tray around its offset pivot.
        module.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
    }

    private static void FixAllModuleOrientations(SFPBox tray)
    {
        if (tray is null)
            return;

        try
        {
            var modules = tray.GetComponentsInChildren<SFPModule>(true);
            foreach (var module in modules)
                FixReturnedModuleOrientation(module);
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                $"Could not normalize modules already in tray: {exception.Message}");
        }
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

    private static void Postfix(
        SFPBox __instance,
        SFPModule __0,
        bool __result)
    {
        if (TrolleyContext.LayoutEnabled && __result)
        {
            ModuleTrayLayout.FixReturnedModuleOrientation(__0);
            ModuleTrayLayout.ScheduleArrange(__instance, "module returned");
        }
    }
}
