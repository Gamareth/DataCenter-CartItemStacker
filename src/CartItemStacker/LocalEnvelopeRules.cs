namespace CartItemStacker;

internal static class LocalEnvelopeRules
{
    internal static bool HasMeaningfulOverlap(
        float cargoMinX,
        float cargoMaxX,
        float cargoMinY,
        float cargoMaxY,
        float cargoMinZ,
        float cargoMaxZ,
        float envelopeMinX,
        float envelopeMaxX,
        float envelopeMinY,
        float envelopeMaxY,
        float envelopeMinZ,
        float envelopeMaxZ)
    {
        return Fraction(cargoMinX, cargoMaxX, envelopeMinX, envelopeMaxX) >= 0.5f &&
            Fraction(cargoMinZ, cargoMaxZ, envelopeMinZ, envelopeMaxZ) >= 0.5f &&
            Fraction(cargoMinY, cargoMaxY, envelopeMinY, envelopeMaxY) >= 0.25f;
    }

    private static float Fraction(
        float minimum,
        float maximum,
        float rangeMinimum,
        float rangeMaximum)
    {
        var size = maximum - minimum;
        if (size <= 0.0001f)
            return minimum >= rangeMinimum && minimum <= rangeMaximum ? 1f : 0f;
        var overlap = System.Math.Max(0f,
            System.Math.Min(maximum, rangeMaximum) -
            System.Math.Max(minimum, rangeMinimum));
        return overlap / size;
    }
}
