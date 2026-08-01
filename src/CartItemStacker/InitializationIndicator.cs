using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

internal static class TrolleyInitializationIndicator
{
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

    private const float ReadyDuration = 1.0f;
    private static readonly int ColorProperty =
        Shader.PropertyToID("_Color");
    private static readonly int BaseColorProperty =
        Shader.PropertyToID("_BaseColor");
    private static readonly Color WarningDark =
        new(0.04f, 0.04f, 0.03f, 1.0f);
    private static readonly Color WarningOrange =
        new(1.0f, 0.60f, 0.04f, 1.0f);
    private static readonly Color ReadyGreen =
        new(0.10f, 1.0f, 0.20f, 1.0f);
    private static readonly List<RendererState> Renderers = new();
    private static int _token;
    private static bool _pulsing;

    internal static void Begin(TrolleyLoadingBay bay)
    {
        CancelAndRestore();
        if (bay?.transform?.root is null)
            return;

        foreach (var renderer in
            bay.transform.root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is null || IsRegisteredCargoRenderer(renderer))
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
            Renderers.Add(new RendererState(
                renderer,
                original,
                supportsColor,
                supportsBaseColor));
        }

        if (Renderers.Count == 0)
        {
            ModSettings.Debug(
                "No compatible trolley renderers were found for the " +
                "initialization indicator.");
            return;
        }

        _pulsing = true;
        var token = ++_token;
        MelonCoroutines.Start(PulseWarning(token));
        ModSettings.Debug(
            $"Started orange-charcoal initialization pulse on {Renderers.Count} " +
            "trolley renderer(s).");
    }

    internal static void ShowReady()
    {
        if (Renderers.Count == 0)
            return;

        _pulsing = false;
        var token = ++_token;
        ApplyColor(ReadyGreen);
        MelonCoroutines.Start(RestoreAfterReady(token));
        ModSettings.Debug(
            "Trolley initialization completed; showing ready green for one second.");
    }

    internal static void CancelAndRestore()
    {
        _pulsing = false;
        _token++;
        RestoreOriginals();
        Renderers.Clear();
    }

    private static IEnumerator PulseWarning(int token)
    {
        while (_pulsing && token == _token)
        {
            var wave = (Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f) + 1f) *
                0.5f;
            ApplyColor(Color.Lerp(WarningDark, WarningOrange, wave));
            yield return null;
        }
    }

    private static IEnumerator RestoreAfterReady(int token)
    {
        yield return new WaitForSeconds(ReadyDuration);
        if (token != _token)
            yield break;

        RestoreOriginals();
        Renderers.Clear();
    }

    private static void ApplyColor(Color color)
    {
        foreach (var state in Renderers)
        {
            if (state?.Renderer is null)
                continue;

            state.Working.Clear();
            if (state.SupportsColor)
                state.Working.SetColor(ColorProperty, color);
            if (state.SupportsBaseColor)
                state.Working.SetColor(BaseColorProperty, color);
            state.Renderer.SetPropertyBlock(state.Working);
        }
    }

    private static void RestoreOriginals()
    {
        foreach (var state in Renderers)
        {
            if (state?.Renderer is null)
                continue;
            try
            {
                state.Renderer.SetPropertyBlock(state.Original);
            }
            catch (System.Exception)
            {
                // The trolley can be destroyed during a new load cycle.
            }
        }
    }

    private static bool IsRegisteredCargoRenderer(Renderer renderer)
    {
        if (renderer?.transform is null)
            return false;

        foreach (var item in TrolleyContext.Items)
            if (item?.transform is not null &&
                renderer.transform.IsChildOf(item.transform))
                return true;
        return false;
    }
}
