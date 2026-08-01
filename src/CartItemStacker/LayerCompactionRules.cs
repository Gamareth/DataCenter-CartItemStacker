namespace CartItemStacker;

/// <summary>
/// Defines when layered cargo may fall into a vacant position. Items remain in
/// their current horizontal position within a row; only cargo from a strictly
/// higher row may fill a lower-row gap.
/// </summary>
internal static class LayerCompactionRules
{
    internal static bool IsFromHigherRow(
        int candidateOrdinal,
        int removedOrdinal,
        int itemsPerRow) =>
        candidateOrdinal >= 0 &&
        removedOrdinal >= 0 &&
        itemsPerRow > 0 &&
        candidateOrdinal / itemsPerRow > removedOrdinal / itemsPerRow;
}
