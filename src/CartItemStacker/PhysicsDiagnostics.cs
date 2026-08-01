using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CartItemStacker;

/// <summary>
/// Temporary event-driven diagnostics for identifying the physics source of a
/// trolley lift. Diagnostics are armed only after a placement while debug
/// logging is enabled; there is no per-frame polling.
/// </summary>
internal static class TrolleyPhysicsDiagnostics
{
    private const float DiagnosticWindowSeconds = 15f;
    private static int _generation;
    private static float _armedUntil;
    private static System.IntPtr _subjectPointer;
    private static string _subjectName = "<none>";
    private static string _reason = "<none>";
    private static Vector3 _baselinePosition;
    private static Quaternion _baselineRotation = Quaternion.identity;
    private static readonly HashSet<System.IntPtr> ProbedObjects = new();

    internal static void Reset()
    {
        _generation++;
        _armedUntil = 0f;
        _subjectPointer = System.IntPtr.Zero;
        _subjectName = "<none>";
        _reason = "<none>";
        _baselinePosition = Vector3.zero;
        _baselineRotation = Quaternion.identity;
        ProbedObjects.Clear();
    }

    internal static void ArmInitialization(
        TrolleyLoadingBay bay,
        IReadOnlyList<UsableObject> loadedItems,
        string reason)
    {
        if (!ModSettings.DebugLogging || bay?.transform?.root is null)
            return;

        _generation++;
        _armedUntil = Time.realtimeSinceStartup + 30f;
        _subjectPointer = System.IntPtr.Zero;
        _subjectName = "<save-load reconstruction>";
        _reason = reason;
        CaptureBaseline(bay);
        AttachTrolleyHierarchy(bay);
        if (loadedItems is not null)
            foreach (var item in loadedItems)
                AttachProbe(item?.gameObject);

        LogSnapshot(bay, null, "initialization armed before staging");
    }

    internal static void LogInitializationStep(
        TrolleyLoadingBay bay,
        string stage)
    {
        if (!ModSettings.DebugLogging ||
            Time.realtimeSinceStartup > _armedUntil ||
            _subjectPointer != System.IntPtr.Zero ||
            _subjectName != "<save-load reconstruction>")
            return;
        LogSnapshot(bay, null, stage);
    }

    internal static void CompleteInitialization(TrolleyLoadingBay bay)
    {
        if (!ModSettings.DebugLogging ||
            _subjectName != "<save-load reconstruction>")
            return;
        _armedUntil = Time.realtimeSinceStartup + DiagnosticWindowSeconds;
        LogSnapshot(bay, null, "initialization completed after collider restore");
    }

    internal static void Arm(
        TrolleyLoadingBay bay,
        UsableObject item,
        string reason)
    {
        if (!ModSettings.DebugLogging || bay?.transform is null || item is null)
            return;

        _generation++;
        var generation = _generation;
        _armedUntil = Time.realtimeSinceStartup + DiagnosticWindowSeconds;
        _subjectPointer = item.Pointer;
        _subjectName = item.name;
        _reason = reason;
        CaptureBaseline(bay);

        AttachTrolleyHierarchy(bay);
        AttachProbe(item.gameObject);
        foreach (var candidate in TrolleyContext.Items)
        {
            if (candidate is null || candidate.objectInHands ||
                !candidate.isOnTrolley)
                continue;
            AttachProbe(candidate.gameObject);
        }

        LogSnapshot(bay, item, "armed t=0.00s");
        MelonCoroutines.Start(LogDelayedSnapshots(
            generation, bay, item, reason));
    }

    internal static void RecordCollision(
        GameObject receiver,
        Collision collision)
    {
        if (!ModSettings.DebugLogging ||
            Time.realtimeSinceStartup > _armedUntil ||
            receiver is null || collision is null)
            return;

        try
        {
            var receiverBody = FindBody(receiver);
            Melon<CartItemStacker.Mod>.Logger.Msg(
                "PHYSICS COLLISION during placement diagnostic: " +
                $"receiver='{HierarchyPath(receiver.transform)}', " +
                $"other='{TryGetOtherPath(collision)}', " +
                $"relativeVelocity={TryGetRelativeVelocity(collision)}, " +
                $"contacts={TryGetContactCount(collision)}, " +
                $"receiverBody={DescribeBody(receiverBody)}, " +
                $"subject='{_subjectName}', reason='{_reason}'.");
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Could not record trolley collision diagnostic: " +
                exception.Message);
        }
    }

    private static IEnumerator LogDelayedSnapshots(
        int generation,
        TrolleyLoadingBay bay,
        UsableObject item,
        string reason)
    {
        yield return new WaitForSeconds(1f);
        if (!IsCurrent(generation, item))
            yield break;
        LogSnapshot(bay, item, "delayed t=1.00s");

        yield return new WaitForSeconds(4f);
        if (!IsCurrent(generation, item))
            yield break;
        LogSnapshot(bay, item, "delayed t=5.00s");

        yield return new WaitForSeconds(7f);
        if (!IsCurrent(generation, item))
            yield break;
        LogSnapshot(bay, item, "delayed t=12.00s");

        ModSettings.Debug(
            $"Physics diagnostic window completed for '{item.name}' " +
            $"after {reason}.");
    }

    private static bool IsCurrent(int generation, UsableObject item) =>
        generation == _generation &&
        item is not null &&
        item.Pointer == _subjectPointer;

    private static void LogSnapshot(
        TrolleyLoadingBay bay,
        UsableObject item,
        string stage)
    {
        try
        {
            var trolleyRoot = bay?.transform?.root;
            var trolleyBody = FindTrolleyBody(bay);
            var itemBody = item?.GetComponent<Rigidbody>();
            var parentPath = item?.transform?.parent is null
                ? "<none>"
                : HierarchyPath(item.transform.parent);

            var validCargo = 0;
            var ownBodies = 0;
            var kinematicBodies = 0;
            foreach (var candidate in TrolleyContext.Items)
            {
                if (candidate is null || candidate.objectInHands ||
                    !candidate.isOnTrolley)
                    continue;
                validCargo++;
                var body = candidate.GetComponent<Rigidbody>();
                if (body is null)
                    continue;
                ownBodies++;
                if (body.isKinematic)
                    kinematicBodies++;
            }

            Melon<CartItemStacker.Mod>.Logger.Msg(
                $"PHYSICS SNAPSHOT {stage}: subject='{item?.name ?? "<null>"}', " +
                $"subjectBody={DescribeBody(itemBody)}, parent='{parentPath}', " +
                $"trolleyBody={DescribeBody(trolleyBody)}, " +
                $"trolleyPosition={Format(trolleyRoot?.position ?? Vector3.zero)}, " +
                $"trolleyRotation={Format(trolleyRoot?.eulerAngles ?? Vector3.zero)}, " +
                $"positionDelta={Format((trolleyRoot?.position ?? Vector3.zero) - _baselinePosition)}, " +
                $"rotationDelta={Quaternion.Angle(_baselineRotation, trolleyRoot?.rotation ?? Quaternion.identity):0.000}deg, " +
                $"cargoBodies={ownBodies}/{validCargo}, " +
                $"kinematicCargoBodies={kinematicBodies}/{ownBodies}.");
        }
        catch (System.Exception exception)
        {
            Melon<CartItemStacker.Mod>.Logger.Warning(
                "Could not capture trolley physics snapshot: " +
                exception.Message);
        }
    }

    private static void AttachProbe(GameObject gameObject)
    {
        if (gameObject is null || !ProbedObjects.Add(gameObject.Pointer))
            return;

        if (gameObject.GetComponent<TrolleyCollisionProbe>() is null)
            gameObject.AddComponent<TrolleyCollisionProbe>();
    }

    private static void AttachTrolleyHierarchy(TrolleyLoadingBay bay)
    {
        var root = bay?.transform?.root;
        if (root is null)
            return;

        AttachProbe(root.gameObject);
        foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
            AttachProbe(body?.gameObject);
        foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            AttachProbe(collider?.gameObject);
    }

    private static void CaptureBaseline(TrolleyLoadingBay bay)
    {
        var root = bay?.transform?.root;
        _baselinePosition = root?.position ?? Vector3.zero;
        _baselineRotation = root?.rotation ?? Quaternion.identity;
    }

    private static Rigidbody FindTrolleyBody(TrolleyLoadingBay bay)
    {
        var body = bay?.GetComponentInParent<Rigidbody>();
        if (body is not null && !BelongsToCargo(body.transform))
            return body;

        var root = bay?.transform?.root;
        if (root is null)
            return null;
        foreach (var candidate in root.GetComponentsInChildren<Rigidbody>(true))
            if (candidate is not null && !BelongsToCargo(candidate.transform))
                return candidate;
        return null;
    }

    private static bool BelongsToCargo(Transform transform)
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

    private static Rigidbody FindBody(GameObject gameObject)
    {
        if (gameObject is null)
            return null;
        var body = gameObject.GetComponent<Rigidbody>();
        return body ?? gameObject.GetComponentInParent<Rigidbody>();
    }

    private static string TryGetOtherPath(Collision collision)
    {
        try
        {
            return HierarchyPath(collision.gameObject?.transform);
        }
        catch (System.Exception)
        {
            return "<unavailable>";
        }
    }

    private static string TryGetRelativeVelocity(Collision collision)
    {
        try
        {
            return Format(collision.relativeVelocity) + "m/s";
        }
        catch (System.Exception)
        {
            return "<unavailable>";
        }
    }

    private static string TryGetContactCount(Collision collision)
    {
        try
        {
            return collision.contactCount.ToString();
        }
        catch (System.Exception)
        {
            return "<unavailable>";
        }
    }

    private static string DescribeBody(Rigidbody body)
    {
        if (body is null)
            return "<none>";
        return $"'{HierarchyPath(body.transform)}' " +
               $"kinematic={body.isKinematic}, sleeping={body.IsSleeping()}, " +
               $"velocity={Format(body.linearVelocity)}m/s, " +
               $"angularVelocity={Format(body.angularVelocity)}rad/s";
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

[RegisterTypeInIl2Cpp]
public sealed class TrolleyCollisionProbe : MonoBehaviour
{
    public TrolleyCollisionProbe(System.IntPtr pointer) : base(pointer)
    {
    }

    public void OnCollisionEnter(Collision collision) =>
        TrolleyPhysicsDiagnostics.RecordCollision(gameObject, collision);
}
