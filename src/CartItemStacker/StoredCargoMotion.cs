using System.Collections;
using Il2Cpp;
using UnityEngine;

namespace CartItemStacker;

/// <summary>
/// Provides the single transform-animation path for cargo that is already
/// stored on the trolley. Section layout classes remain responsible for slot
/// selection and target geometry; this class owns interpolation and exact
/// convergence to the supplied absolute pose.
/// </summary>
internal static class StoredCargoMotion
{
    internal enum PositionEasing
    {
        Linear,
        SmoothStep
    }

    internal enum RotationMotion
    {
        Fixed,
        Interpolate
    }

    internal static IEnumerator AnimateAbsolute(
        UsableObject item,
        Vector3 destination,
        Quaternion rotation,
        float duration,
        PositionEasing positionEasing,
        RotationMotion rotationMotion,
        System.Func<bool> remainsCurrent)
    {
        if (item?.transform is null)
            yield break;

        var startPosition = item.transform.position;
        var startRotation = item.transform.rotation;
        var elapsed = 0f;
        while (elapsed < duration)
        {
            yield return null;
            if (!CanContinue(item, remainsCurrent))
                yield break;

            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / duration);
            if (positionEasing == PositionEasing.SmoothStep)
                progress = progress * progress * (3f - 2f * progress);

            item.transform.SetPositionAndRotation(
                Vector3.Lerp(startPosition, destination, progress),
                rotationMotion == RotationMotion.Interpolate
                    ? Quaternion.Slerp(startRotation, rotation, progress)
                    : rotation);
        }

        if (CanContinue(item, remainsCurrent))
            SnapAbsolute(item, destination, rotation);
    }

    internal static bool SnapAbsolute(
        UsableObject item,
        Vector3 destination,
        Quaternion rotation)
    {
        if (item?.transform is null || item.objectInHands)
            return false;

        item.transform.SetPositionAndRotation(destination, rotation);
        return true;
    }

    private static bool CanContinue(
        UsableObject item,
        System.Func<bool> remainsCurrent) =>
        item is not null &&
        !item.objectInHands &&
        item.transform is not null &&
        (remainsCurrent is null || remainsCurrent());
}
