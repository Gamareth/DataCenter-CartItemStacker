using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class CargoMovementIndicator
{
    private enum IndicatorPhase
    {
        Moving,
        Ready,
    }

    private sealed class RendererState
    {
        internal readonly Renderer Renderer;
        internal readonly MaterialPropertyBlock Original;
        internal readonly MaterialPropertyBlock Working;
        internal readonly bool SupportsColor;
        internal readonly bool SupportsBaseColor;

        internal RendererState(
            Renderer renderer,
            MaterialPropertyBlock original,
            bool supportsColor,
            bool supportsBaseColor)
        {
            Renderer = renderer;
            Original = original;
            Working = new MaterialPropertyBlock();
            SupportsColor = supportsColor;
            SupportsBaseColor = supportsBaseColor;
        }
    }

    private sealed class ItemState
    {
        internal readonly UsableObject Item;
        internal readonly List<RendererState> Renderers;
        internal IndicatorPhase Phase;
        internal float MovingUntil;
        internal float ReadyUntil;

        internal ItemState(
            UsableObject item,
            List<RendererState> renderers)
        {
            Item = item;
            Renderers = renderers;
        }
    }

    private const float ReadyDuration = 1.0f;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int BaseColorProperty =
        Shader.PropertyToID("_BaseColor");
    private static readonly Color WarningDark =
        new(0.04f, 0.04f, 0.03f, 1.0f);
    private static readonly Color WarningOrange =
        new(1.0f, 0.60f, 0.04f, 1.0f);
    private static readonly Color ReadyGreen =
        new(0.10f, 1.0f, 0.20f, 1.0f);
    private static readonly Dictionary<System.IntPtr, ItemState> Active = new();
    private static readonly List<System.IntPtr> Completed = new();
    private static int _runnerToken;
    private static bool _runnerActive;

    internal static void Begin(UsableObject item, string reason) =>
        BeginInternal(item, float.PositiveInfinity, reason);

    internal static void TrackForDuration(
        UsableObject item,
        float duration,
        string reason) =>
        BeginInternal(
            item,
            Time.unscaledTime + System.Math.Max(0.01f, duration),
            reason);

    internal static void Begin(
        IEnumerable<UsableObject> items,
        string reason)
    {
        if (items is null)
            return;
        foreach (var item in items)
            Begin(item, reason);
    }

    internal static void ShowReady(UsableObject item, string reason)
    {
        if (item is null ||
            !Active.TryGetValue(item.Pointer, out var state))
            return;

        state.Phase = IndicatorPhase.Ready;
        state.ReadyUntil = Time.unscaledTime + ReadyDuration;
        ApplyColor(state, ReadyGreen);
        EnsureRunner();
        ModSettings.Debug(
            $"Cargo movement indicator marked '{item.name}' ready after {reason}.");
    }

    internal static void ShowReady(
        IEnumerable<UsableObject> items,
        string reason)
    {
        if (items is null)
            return;
        foreach (var item in items)
            ShowReady(item, reason);
    }

    internal static void CancelAndRestore(UsableObject item)
    {
        if (item is null ||
            !Active.TryGetValue(item.Pointer, out var state))
            return;

        Restore(state);
        Active.Remove(item.Pointer);
    }

    internal static void Reset()
    {
        _runnerToken++;
        _runnerActive = false;
        foreach (var state in Active.Values)
            Restore(state);
        Active.Clear();
        Completed.Clear();
    }

    private static void BeginInternal(
        UsableObject item,
        float movingUntil,
        string reason)
    {
        if (!ModSettings.RestackCargoIndicator || item?.transform is null)
            return;

        if (!Active.TryGetValue(item.Pointer, out var state))
        {
            var renderers = CaptureRenderers(item);
            if (renderers.Count == 0)
                return;
            state = new ItemState(item, renderers);
            Active[item.Pointer] = state;
        }

        state.Phase = IndicatorPhase.Moving;
        state.MovingUntil = movingUntil;
        ApplyColor(state, WarningOrange);
        EnsureRunner();
        var enabledRenderers = 0;
        foreach (var rendererState in state.Renderers)
            if (rendererState?.Renderer is not null &&
                rendererState.Renderer.enabled)
                enabledRenderers++;
        ModSettings.Debug(
            $"Cargo movement indicator started for '{item.name}' on " +
            $"{state.Renderers.Count} compatible renderer(s), " +
            $"{enabledRenderers} enabled, during {reason}.");
    }

    private static List<RendererState> CaptureRenderers(UsableObject item)
    {
        var result = new List<RendererState>();
        foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is null)
                continue;

            var supportsColor = false;
            var supportsBaseColor = false;
            foreach (var material in renderer.sharedMaterials)
            {
                if (material is null)
                    continue;
                supportsColor |= material.HasProperty(ColorProperty);
                supportsBaseColor |= material.HasProperty(BaseColorProperty);
            }
            if (!supportsColor && !supportsBaseColor)
                continue;

            var original = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(original);
            result.Add(new RendererState(
                renderer,
                original,
                supportsColor,
                supportsBaseColor));
        }
        return result;
    }

    private static void EnsureRunner()
    {
        if (_runnerActive || Active.Count == 0)
            return;

        _runnerActive = true;
        var token = ++_runnerToken;
        MelonCoroutines.Start(Run(token));
    }

    private static IEnumerator Run(int token)
    {
        while (token == _runnerToken && Active.Count > 0)
        {
            var now = Time.unscaledTime;
            var wave = (Mathf.Sin(now * Mathf.PI * 2f) + 1f) * 0.5f;
            var movingColor = Color.Lerp(WarningDark, WarningOrange, wave);
            Completed.Clear();

            foreach (var pair in Active)
            {
                var state = pair.Value;
                if (state?.Item is null || state.Item.transform is null)
                {
                    Completed.Add(pair.Key);
                    continue;
                }

                if (state.Phase == IndicatorPhase.Moving)
                {
                    if (now >= state.MovingUntil)
                    {
                        state.Phase = IndicatorPhase.Ready;
                        state.ReadyUntil = now + ReadyDuration;
                        ApplyColor(state, ReadyGreen);
                    }
                    else
                        ApplyColor(state, movingColor);
                }
                else if (now >= state.ReadyUntil)
                {
                    Restore(state);
                    Completed.Add(pair.Key);
                }
            }

            foreach (var pointer in Completed)
                Active.Remove(pointer);
            yield return null;
        }

        if (token == _runnerToken)
            _runnerActive = false;
    }

    private static void ApplyColor(ItemState state, Color color)
    {
        foreach (var rendererState in state.Renderers)
        {
            if (rendererState?.Renderer is null)
                continue;

            rendererState.Working.Clear();
            if (rendererState.SupportsColor)
                rendererState.Working.SetColor(ColorProperty, color);
            if (rendererState.SupportsBaseColor)
                rendererState.Working.SetColor(BaseColorProperty, color);
            rendererState.Renderer.SetPropertyBlock(rendererState.Working);
        }
    }

    private static void Restore(ItemState state)
    {
        if (state?.Renderers is null)
            return;
        foreach (var rendererState in state.Renderers)
        {
            if (rendererState?.Renderer is null)
                continue;
            try
            {
                rendererState.Renderer.SetPropertyBlock(rendererState.Original);
            }
            catch (System.Exception)
            {
                // Cargo can be destroyed or unloaded while an indicator is active.
            }
        }
    }
}
