using UnityEngine;

namespace CartItemStacker;

internal static class CartLocalGeometry
{
    internal static bool TryGetBounds(
        Transform cart,
        Component root,
        out Bounds bounds)
    {
        bounds = default;
        if (cart is null || root?.transform is null)
            return false;

        var found = false;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer?.transform is null || !renderer.enabled)
                continue;

            var local = renderer.localBounds;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = local.center + new Vector3(
                    (corner & 1) == 0 ? -local.extents.x : local.extents.x,
                    (corner & 2) == 0 ? -local.extents.y : local.extents.y,
                    (corner & 4) == 0 ? -local.extents.z : local.extents.z);
                var cartLocal = cart.InverseTransformPoint(
                    renderer.transform.TransformPoint(point));
                if (!found)
                {
                    bounds = new Bounds(cartLocal, Vector3.zero);
                    found = true;
                }
                else
                    bounds.Encapsulate(cartLocal);
            }
        }
        return found;
    }

    internal static bool TryGetProjectedModelBounds(
        Component root,
        Quaternion cartLocalRotation,
        out Bounds bounds)
    {
        bounds = default;
        if (root?.transform is null)
            return false;

        var found = false;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer?.transform is null || !renderer.enabled)
                continue;

            var local = renderer.localBounds;
            for (var corner = 0; corner < 8; corner++)
            {
                var rendererPoint = local.center + new Vector3(
                    (corner & 1) == 0 ? -local.extents.x : local.extents.x,
                    (corner & 2) == 0 ? -local.extents.y : local.extents.y,
                    (corner & 4) == 0 ? -local.extents.z : local.extents.z);
                var rootLocal = root.transform.InverseTransformPoint(
                    renderer.transform.TransformPoint(rendererPoint));
                var projected = cartLocalRotation * rootLocal;
                if (!found)
                {
                    bounds = new Bounds(projected, Vector3.zero);
                    found = true;
                }
                else
                    bounds.Encapsulate(projected);
            }
        }
        return found;
    }

    internal static bool HasMeaningfulOverlap(
        Bounds cargo,
        Vector3 envelopeMin,
        Vector3 envelopeMax)
    {
        return LocalEnvelopeRules.HasMeaningfulOverlap(
            cargo.min.x, cargo.max.x,
            cargo.min.y, cargo.max.y,
            cargo.min.z, cargo.max.z,
            envelopeMin.x, envelopeMax.x,
            envelopeMin.y, envelopeMax.y,
            envelopeMin.z, envelopeMax.z);
    }
}
