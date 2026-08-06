using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(
    typeof(CartItemStacker.Mod),
    CartItemStacker.Mod.DisplayName,
    CartItemStacker.Mod.Version,
    CartItemStacker.Mod.Author)]
[assembly: MelonGame("Waseku", "Data Center")]

namespace CartItemStacker;

internal static class CartLayout
{
    internal const int StackCount = 2;
    internal const int SlotsPerStack = CapacityLimits.MaximumEquipmentUnits;
    internal const int ServerSlots = StackCount * SlotsPerStack;
    internal const int ModuleTypeCount = 4;
    internal const int ActiveModuleTraySlots = ModuleTypeCount;
    internal const int FilledOverflowTraySlots = 20;
    internal const int FilledModuleTraySlots =
        ActiveModuleTraySlots + FilledOverflowTraySlots;
    internal const int EmptyModuleTraySlots = 20;
    internal const int ModuleTraySlots =
        FilledModuleTraySlots + EmptyModuleTraySlots;
    internal const int CableStackCount = 2;
    internal const int CableSlotsPerStack = CapacityLimits.MaximumCableSpools;
    internal const int CableSlots = CableStackCount * CableSlotsPerStack;
    internal const int ModuleStart = ServerSlots;
    internal const int CableStart = ModuleStart + ModuleTraySlots;
    internal const int TotalSlots =
        ServerSlots + ModuleTraySlots + CableSlots;
    // The game's boxed-rack item remains natively 4U when held/dropped. Its
    // upright cart row reserves more vertical capacity in our custom layout.
    internal const int NativeBoxSizeU = 4;
    internal const int BoxLayerU = 13;
    internal const int BoxesPerLayer = 3;
    internal const int BoxRowsPerStack = SlotsPerStack / BoxLayerU;
    internal const int TotalBoxLayers = StackCount * BoxRowsPerStack;
    internal const int PatchPanelsPerLayer = 7;
    internal const int PatchLayerU = 4;
    // Keep both equipment stacks at the calibrated longitudinal positions and
    // compensate each rotated model's visible X center at its target.
    private const float ServerPairCenterZ = -0.130f;
    private const float ServerCenterSpacing = 0.480f;
    // Renderer-bound centering removes the prefab pivot offset. The selected
    // outline and mounting details leave a final visual mismatch, so move both
    // opposite rotations another 2mm toward their shared lateral alignment.
    internal const float ServerVisualAlignmentTrimX = 0.001f;
    private const float FallbackStack1Z =
        ServerPairCenterZ - ServerCenterSpacing * 0.5f;
    private const float FallbackStack2Z =
        ServerPairCenterZ + ServerCenterSpacing * 0.5f;
    private static float _stack1Z = FallbackStack1Z;
    private static float _stack2Z = FallbackStack2Z;
    private static int _rotatedStack = 1;

    internal static float GetStackZ(int stack) =>
        stack == 0 ? _stack1Z : _stack2Z;

    internal static float GetServerYaw(int stack) =>
        stack == _rotatedStack ? 270f : 90f;

    internal static void ConfigureHandleSide(TrolleyLoadingBay bay)
    {
        _stack1Z = FallbackStack1Z;
        _stack2Z = FallbackStack2Z;
        _rotatedStack = 1;
        try
        {
            var bayTransform = bay?.transform;
            var trolleyRoot = bayTransform?.root;
            if (bayTransform is null || trolleyRoot is null)
                throw new InvalidOperationException(
                    "Trolley transform is not ready.");

            var handle =
                trolleyRoot.GetComponentInChildren<PushTrolleyHandle>(true);
            var handleTransform = handle?.transform;
            if (handleTransform is null)
                throw new InvalidOperationException(
                    "Trolley handle is not ready.");

            var localHandle =
                bayTransform.InverseTransformPoint(handleTransform.position);
            if (localHandle.z < 0f)
            {
                _stack1Z = FallbackStack1Z;
                _stack2Z = FallbackStack2Z;
                _rotatedStack = 1;
            }
            else
            {
                // Mirrored trolley: stack 2 is closest to the positive-Z handle.
                _stack1Z = FallbackStack1Z;
                _stack2Z = FallbackStack2Z;
                _rotatedStack = 0;
            }

            ModSettings.Debug(
                $"Trolley handle local position ({localHandle.x:0.000}, " +
                $"{localHandle.y:0.000}, {localHandle.z:0.000}); server stack Z " +
                $"positions are {_stack1Z:0.000} and {_stack2Z:0.000}m; " +
                $"stack {_rotatedStack + 1} is rotated 180 degrees.");
        }
        catch (Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Could not configure trolley handle during load; using " +
                $"fallback Z layout ({_stack1Z:0.000}, {_stack2Z:0.000}). " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}

public sealed class Mod : MelonMod
{
    internal const string DisplayName = "Cart Item Stacker";
    internal const string Version = "1.0.0";
    internal const string Author = "Gamareth";

    public override void OnInitializeMelon()
    {
        ModSettings.Initialize();
        NativeSaveLifecycle.Initialize();
        LoggerInstance.Msg(
            $"{DisplayName} {Version} initialized. " +
            $"Requested state: {(ModSettings.RequestedEnabled ? "enabled" : "disabled")}; " +
            $"equipment limit: {ModSettings.EquipmentStackMaxUnits}U per stack; " +
            $"cable-spool limit: {ModSettings.CableSpoolsPerStack} per stack; " +
            $"animation speed: {ModSettings.AnimationSpeed:0.00}x; " +
            $"restack cargo indicator: " +
            $"{(ModSettings.RestackCargoIndicator ? "enabled" : "disabled")}.");
    }
}
