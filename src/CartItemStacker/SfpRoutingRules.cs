namespace CartItemStacker;

internal static class SfpRoutingRules
{
    internal static bool IsBetterPartial(
        int candidateFill,
        int candidateSlot,
        int currentFill,
        int currentSlot) =>
        candidateFill > currentFill ||
        (candidateFill == currentFill && candidateSlot < currentSlot);
}
