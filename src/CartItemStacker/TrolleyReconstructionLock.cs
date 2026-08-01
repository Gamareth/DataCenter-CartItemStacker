using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

/// <summary>
/// Holds the trolley reference frame still while loaded cargo is reconstructed.
/// Original constraints and interaction state are restored afterwards, but old
/// momentum is intentionally discarded so reconstruction cannot resume a stale
/// physics impulse.
/// </summary>
internal static class TrolleyReconstructionLock
{
    private readonly struct BodyState
    {
        internal readonly Rigidbody Body;
        internal readonly RigidbodyConstraints Constraints;
        internal readonly bool WasSleeping;
        internal readonly Vector3 LinearVelocity;
        internal readonly Vector3 AngularVelocity;

        internal BodyState(Rigidbody body)
        {
            Body = body;
            Constraints = body.constraints;
            WasSleeping = body.IsSleeping();
            LinearVelocity = body.linearVelocity;
            AngularVelocity = body.angularVelocity;
        }
    }

    private static readonly List<BodyState> Bodies = new();
    private static TrolleyLoadingBay _bay;
    private static Transform _root;
    private static Vector3 _position;
    private static Quaternion _rotation;
    private static PushTrolleyHandle _handle;
    private static bool _handleWasEnabled;
    private static bool _active;

    internal static bool Active => _active;

    internal static void Begin(TrolleyLoadingBay bay)
    {
        CancelAndRestore("replacement reconstruction");
        if (bay?.transform?.root is null)
            return;

        _bay = bay;
        _root = bay.transform.root;
        _position = _root.position;
        _rotation = _root.rotation;
        _handle = _root.GetComponentInChildren<PushTrolleyHandle>(true);
        if (_handle is not null)
        {
            _handleWasEnabled = _handle.enabled;
            _handle.enabled = false;
        }

        Bodies.Clear();
        foreach (var body in _root.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body is null || BelongsToRegisteredCargo(body.transform))
                continue;

            var state = new BodyState(body);
            Bodies.Add(state);
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.constraints = RigidbodyConstraints.FreezeAll;
                body.Sleep();
            }
        }

        Physics.SyncTransforms();
        _active = true;
        Melon<CartItemStacker.Mod>.Logger.Msg(
            $"Locked trolley reconstruction frame at " +
            $"{Format(_position)} with {Bodies.Count} chassis Rigidbody " +
            $"state(s); handle interaction was " +
            $"{(_handleWasEnabled ? "enabled" : "disabled")}.");
        foreach (var state in Bodies)
        {
            ModSettings.Debug(
                $"Pre-lock trolley body '{HierarchyPath(state.Body?.transform)}': " +
                $"constraints={state.Constraints}, sleeping={state.WasSleeping}, " +
                $"velocity={Format(state.LinearVelocity)}m/s, " +
                $"angularVelocity={Format(state.AngularVelocity)}rad/s.");
        }
    }

    internal static void End(string reason)
    {
        if (!_active)
            return;

        try
        {
            if (_root is not null)
                _root.SetPositionAndRotation(_position, _rotation);
            Physics.SyncTransforms();

            foreach (var state in Bodies)
            {
                var body = state.Body;
                if (body is null)
                    continue;
                if (!body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                body.constraints = state.Constraints;
                if (state.WasSleeping)
                    body.Sleep();
                else
                    body.WakeUp();
            }

            if (_handle is not null)
                _handle.enabled = _handleWasEnabled;
            Physics.SyncTransforms();
            Melon<CartItemStacker.Mod>.Logger.Msg(
                $"Released trolley reconstruction lock after {reason}; " +
                "original constraints and handle state restored with zero " +
                "linear and angular velocity.");
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Could not fully restore trolley reconstruction lock: " +
                exception.Message);
        }
        finally
        {
            Bodies.Clear();
            _bay = null;
            _root = null;
            _handle = null;
            _active = false;
        }
    }

    internal static void CancelAndRestore(string reason) => End(reason);

    private static bool BelongsToRegisteredCargo(Transform transform)
    {
        if (transform is null)
            return false;
        foreach (var item in TrolleyContext.Items)
            if (item?.transform is not null &&
                (transform.Pointer == item.transform.Pointer ||
                 transform.IsChildOf(item.transform)))
                return true;
        return false;
    }

    private static string HierarchyPath(Transform transform)
    {
        if (transform is null)
            return "<none>";
        var names = new List<string>();
        var current = transform;
        for (var depth = 0; current is not null && depth < 12; depth++)
        {
            names.Add(current.name);
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string Format(Vector3 value) =>
        $"({value.x:0.000},{value.y:0.000},{value.z:0.000})";
}
