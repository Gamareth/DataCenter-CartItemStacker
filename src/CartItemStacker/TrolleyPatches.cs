using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class ServerSectionCatalog
{
    private static readonly string[] SupportedRecordPrefixes =
    {
        "ShopItemSO_Server_",
        "ShopItemSO_Switch_",
        "ShopItemSO_Router_",
        "ShopItemSO_Firewall_",
        "ShopItemSO_Rack47U",
    };

    private static readonly HashSet<string> Allowed = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "ShopItemSO_Server_Blue1",
        "ShopItemSO_Server_Blue2",
        "ShopItemSO_Server_Green1",
        "ShopItemSO_Server_Green2",
        "ShopItemSO_Server_Purple1",
        "ShopItemSO_Server_Purple2",
        "ShopItemSO_Server_Yellow1",
        "ShopItemSO_Server_Yellow2",
        "ShopItemSO_Switch_16RJ",
        "ShopItemSO_Switch_32QSFP",
        "ShopItemSO_Switch_4QSFP_16SFP",
        "ShopItemSO_Switch_4SFP",
        "ShopItemSO_Router_4QSFP_16SFP 1",
        "ShopItemSO_Firewall_4QSFP_16SFP 2",
        "ShopItemSO_Rack47U",
        "ShopItemSO_Rack47U custom color",
    };

    internal static bool IsAllowed(UsableObject item, out string record)
    {
        record = item?.shopItemSO?.name ?? string.Empty;
        if (Allowed.Contains(record))
            return true;
        foreach (var prefix in SupportedRecordPrefixes)
            if (record.StartsWith(
                prefix,
                System.StringComparison.OrdinalIgnoreCase))
                return true;

        // The delivered rack box can temporarily lose its ScriptableObject link,
        // but its runtime prefab identity remains unambiguous.
        return BoxLayerLayout.IsBox(item);
    }
}

[HarmonyPatch(typeof(TrolleyLoadingBay), nameof(TrolleyLoadingBay.InteractOnClick))]
internal static class TrolleyClickPatch
{
    private readonly struct ClickState
    {
        internal readonly int Used;
        internal readonly UsableObject Held;

        internal ClickState(int used, UsableObject held)
        {
            Used = used;
            Held = held;
        }
    }

    private static int CountUsed(TrolleyLoadingBay bay)
    {
        var used = bay?.usedPositions;
        if (used is null)
            return -1;

        var count = 0;
        for (var i = 0; i < used.Length; i++)
            if (used[i] != 0)
                count++;
        return count;
    }

    private static bool Prefix(TrolleyLoadingBay __instance, out ClickState __state)
    {
        TrolleyTargetPatch.ScheduleDisableIfRequested();
        var held = TrolleyContext.LayoutEnabled
            ? DynamicTargetAllocator.GetHeldObject()
            : null;
        __state = new ClickState(CountUsed(__instance), held);
        if (!TrolleyContext.LayoutEnabled)
            return true;

        var boxCount = BoxLayerLayout.ExistingBoxCount(held);
        ModSettings.Debug(
            $"Trolley click BEFORE: held='{held?.name ?? "<none>"}', " +
            $"boxOrdinal={boxCount}, registered={TrolleyContext.Items.Count}, " +
            $"used={__state.Used}/{__instance?.usedPositions?.Length ?? 0}.");
        PlacementPoseDiagnostics.LogItem(
            "click-before-native",
            __instance,
            held);
        var proceed = DynamicTargetAllocator.PrepareForClick(__instance);
        if (proceed &&
            ModuleTrayLayout.TryGetTray(held, out var incomingTray))
        {
            // The native storage animation begins immediately after Prefix.
            // Isolate the incoming tray first so it cannot kick the trolley or
            // existing cargo before Postfix gets a chance to register it.
            TrolleyPhysicsIsolation.IgnoreStoredItem(incomingTray);
            ModuleTrayLayout.IsolateIncomingTray(incomingTray);
        }
        else if (proceed &&
            (PatchPanelLayerLayout.IsPatchPanel(held) ||
             CableWheelLayout.IsCableWheel(held)))
        {
            TrolleyPhysicsIsolation.IgnoreStoredItem(held);
        }
        return proceed;
    }

    private static void Postfix(TrolleyLoadingBay __instance, ClickState __state)
    {
        if (!TrolleyContext.LayoutEnabled)
            return;

        DynamicTargetAllocator.CompleteClick(__instance);
        PlacementPoseDiagnostics.LogItem(
            "click-postfix",
            __instance,
            __state.Held);
        var after = CountUsed(__instance);
        var held = DynamicTargetAllocator.GetHeldObject();
        if (__state.Held is not null && held is null &&
            !__state.Held.objectInHands)
        {
            TrolleyContext.Register(__state.Held);
            __state.Held.isOnTrolley = true;
            TrolleyPhysicsIsolation.IgnoreStoredItem(__state.Held);
            TrolleyItemInteraction.SchedulePlacementFinalize(
                __state.Held, "placement");
        }
        ModSettings.Debug(
            $"Trolley click AFTER: held='{held?.name ?? "<none>"}', " +
            $"used={after}/{__instance?.usedPositions?.Length ?? 0}, delta={after - __state.Used}.");
    }
}

[HarmonyPatch(
    typeof(UnityStandardAssets.Characters.FirstPerson.RayLookAt),
    nameof(UnityStandardAssets.Characters.FirstPerson.RayLookAt._Init_b__20_0))]
internal static class HeldItemCargoClickPatch
{
    private static void Postfix(
        UnityStandardAssets.Characters.FirstPerson.RayLookAt __instance)
    {
        if (!TrolleyContext.LayoutEnabled || __instance is null)
            return;

        var held = DynamicTargetAllocator.GetHeldObject();
        if (held is null)
            return;

        var target = __instance.interactable as UsableObject;
        var bay = TrolleyContext.Current;
        if (ModuleTrayLayout.IsModuleTrayInteraction(held, target))
        {
            if (ModuleTrayLayout.TryRouteModuleToEmptyTray(
                held, target, out var destination))
            {
                __instance.i_interact = false;
                ModSettings.Debug(
                    $"Routed SFP from full tray '{target.name}' to empty " +
                    $"trolley tray '{destination.name}'.");
            }
            else
            {
                ModSettings.Debug(
                    $"Preserving native SFP interaction from '{held.name}' " +
                    $"to trolley tray '{target.name}'.");
            }
            return;
        }

        if (target is null ||
            target.Pointer == System.IntPtr.Zero ||
            held.Pointer == target.Pointer ||
            !TrolleyRemovalPatch.BelongsToTrolley(bay, target))
            return;

        // The native UsableObject path ignores clicks while another object is
        // held. Consume this single input event and route it through the native
        // trolley placement method instead. HandleLookAtRay therefore cannot
        // process the same click again on the following frame.
        __instance.i_interact = false;
        ModSettings.Debug(
            $"Redirecting interact event on trolley item '{target.name}' " +
            $"to placement of held item '{held.name}'.");
        bay.InteractOnClick();
    }
}

[HarmonyPatch(typeof(UsableObject), nameof(UsableObject.InteractOnClick))]
internal static class TrolleyRemovalPatch
{
    private readonly struct RemovalState
    {
        internal readonly bool Valid;
        internal readonly bool BoxedRack;
        internal readonly bool ModuleTray;
        internal readonly bool PatchPanel;
        internal readonly bool CableWheel;
        internal readonly int Start;
        internal readonly int Size;
        internal readonly int GroupStack;
        internal readonly int GroupOrdinal;
        internal readonly int BoxRowsBefore;

        internal RemovalState(
            bool valid,
            bool boxedRack,
            bool moduleTray,
            bool patchPanel,
            bool cableWheel,
            int start,
            int size,
            int groupStack,
            int groupOrdinal,
            int boxRowsBefore)
        {
            Valid = valid;
            BoxedRack = boxedRack;
            ModuleTray = moduleTray;
            PatchPanel = patchPanel;
            CableWheel = cableWheel;
            Start = start;
            Size = size;
            GroupStack = groupStack;
            GroupOrdinal = groupOrdinal;
            BoxRowsBefore = boxRowsBefore;
        }
    }

    private static bool Prefix(UsableObject __instance, out RemovalState __state)
    {
        if (!TrolleyContext.LayoutEnabled)
        {
            __state = new RemovalState(
                false, false, false, false, false,
                -1, 0, -1, -1, -1);
            return true;
        }

        var bay = TrolleyContext.Current;
        var held = DynamicTargetAllocator.GetHeldObject();
        if (held is not null &&
            ModuleTrayLayout.IsModuleTrayInteraction(held, __instance))
        {
            __state = new RemovalState(
                false, false, false, false, false,
                -1, 0, -1, -1, -1);
            return true;
        }

        if (held is not null && held.Pointer != __instance.Pointer &&
            BelongsToTrolley(bay, __instance))
        {
            __state = new RemovalState(
                false, false, false, false, false,
                -1, 0, -1, -1, -1);
            ModSettings.Debug(
                $"Redirecting click on trolley item '{__instance.name}' to trolley placement.");
            bay.InteractOnClick();
            return false;
        }

        var handsEmpty = held is null;
        var valid = handsEmpty && BelongsToTrolley(bay, __instance);
        var boxedRack = BoxLayerLayout.IsBox(__instance);
        var moduleTray = ModuleTrayLayout.IsTray(__instance);
        var patchPanel = PatchPanelLayerLayout.IsPatchPanel(__instance);
        var cableWheel = CableWheelLayout.IsCableWheel(__instance);
        var groupStack = valid && boxedRack
            ? BoxLayerLayout.GetItemStack(__instance)
            : valid && patchPanel
                ? PatchPanelLayerLayout.GetItemStack(__instance)
                : -1;
        var groupOrdinal = valid && boxedRack
            ? BoxLayerLayout.GetStackOrdinal(__instance, groupStack)
            : valid && patchPanel
                ? PatchPanelLayerLayout.GetItemOrdinal(__instance)
                : -1;
        var boxRowsBefore = valid && boxedRack && groupStack >= 0
            ? BoxLayerLayout.GetBoxRowCount(groupStack)
            : -1;
        __state = new RemovalState(
            valid,
            boxedRack,
            moduleTray,
            patchPanel,
            cableWheel,
            valid ? __instance.trolleySlotIndex : -1,
            valid ? System.Math.Max(1, __instance.sizeInU) : 0,
            groupStack,
            groupOrdinal,
            boxRowsBefore);

        if (valid)
        {
            TrolleyContext.DiscoverSaveItemsOnce(__instance.storageUID);
            if (moduleTray &&
                ModuleTrayLayout.TryGetTray(__instance, out var tray))
                ModuleTrayLayout.BeforeModuleChange(tray);
            else if (!boxedRack && !patchPanel && !cableWheel)
                TrolleyCompactor.MoveUpperItemsBeforeExtraction(
                    __instance, __state.Start, __state.Size);
        }
        return true;
    }

    internal static bool BelongsToTrolley(TrolleyLoadingBay bay, UsableObject item)
    {
        if (!TrolleyContext.LayoutEnabled || bay is null || item is null || item.transform is null)
            return false;

        if (item.transform.IsChildOf(bay.transform))
            return true;

        var targets = bay.positionsOnTrolley;
        var slot = item.trolleySlotIndex;
        if (targets is null || slot < 0 || slot >= targets.Length || targets[slot] is null)
            return false;

        // Normal placement and compacted items retain either trolley parenting or
        // the exact native target. Legacy-save discovery then uses storageUID.
        return (item.transform.position - targets[slot].position).sqrMagnitude < 0.25f ||
            item.isOnTrolley;
    }

    private static void Postfix(UsableObject __instance, RemovalState __state)
    {
        if (__state.Valid)
        {
            TrolleyCompactor.ReleaseAnchor(__instance);
            TrolleyContext.Unregister(__instance);
            if (__state.ModuleTray)
            {
                ModuleTrayLayout.Arrange(
                    TrolleyContext.Current, "tray extraction");
                BoxLayerLayout.RebuildLayerReservations(TrolleyContext.Current);
            }
            else if (__state.CableWheel)
            {
                CableWheelLayout.Arrange(
                    TrolleyContext.Current, "cable extraction");
            }
            else if (__state.PatchPanel)
            {
                __instance.sizeInU = 1;
                PatchPanelLayerLayout.Forget(__instance);
                PatchPanelLayerLayout.CompactStack(
                    TrolleyContext.Current,
                    __state.GroupStack,
                    __state.GroupOrdinal,
                    "patch extraction");
                BoxLayerLayout.RebuildLayerReservations(TrolleyContext.Current);
            }
            else if (__state.BoxedRack)
            {
                __instance.sizeInU = CartLayout.NativeBoxSizeU;
                BoxLayerLayout.ForgetBox(__instance);
                BoxLayerLayout.CompactStack(
                    TrolleyContext.Current,
                    __state.GroupStack,
                    __state.GroupOrdinal);
                var newBoxRows =
                    BoxLayerLayout.GetBoxRowCount(__state.GroupStack);
                if (__state.BoxRowsBefore >= 0 &&
                    newBoxRows != __state.BoxRowsBefore)
                    PatchPanelLayerLayout.ShiftPanelsVertically(
                        TrolleyContext.Current,
                        __state.GroupStack,
                        (newBoxRows - __state.BoxRowsBefore) *
                            CartLayout.BoxLayerU);
                BoxLayerLayout.RebuildLayerReservations(TrolleyContext.Current);
            }
            else
            {
                TrolleyCompactor.RebuildReservationsAfterExtraction(
                    __instance, __state.Start);
                var stack = __state.Start < CartLayout.SlotsPerStack ? 0 : 1;
                BoxLayerLayout.ShiftBoxesVertically(
                    TrolleyContext.Current, stack, -__state.Size);
                PatchPanelLayerLayout.ShiftPanelsVertically(
                    TrolleyContext.Current, stack, -__state.Size);
                BoxLayerLayout.RebuildLayerReservations(TrolleyContext.Current);
            }

        }

        // MoveToStorage animates through MoveBetweenPositions. Keep the moved
        // cargo non-collidable until that one-shot animation has reached its
        // exact target; restoring in this synchronous postfix can reintroduce
        // collisions while seven or more objects are still travelling.
        TrolleyCompactor.ScheduleCompactionColliderRestore();

        if (__state.Valid)
        {
            // Native extraction has put the object back in the player's hand.
            // Restore only the collision pairs that this mod changed.
            TrolleyPhysicsIsolation.RestoreItem(__instance);
            TrolleyTargetPatch.ScheduleDisableIfRequested();
        }
    }

    private static System.Exception Finalizer(System.Exception __exception)
    {
        // Harmony finalizers run for successful calls too. Only bypass the
        // scheduled post-animation restore when native extraction really threw.
        if (__exception is not null)
            TrolleyCompactor.RestoreCompactionColliders();
        return __exception;
    }
}
