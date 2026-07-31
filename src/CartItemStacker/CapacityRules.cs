namespace CartItemStacker;

internal static class CapacityRules
{
    internal static bool CanAddEquipmentHeight(
        int currentUnits,
        int additionalUnits,
        int configuredMaximumUnits) =>
        currentUnits >= 0 &&
        additionalUnits >= 0 &&
        configuredMaximumUnits >= 0 &&
        (long)currentUnits + additionalUnits <= configuredMaximumUnits;

    internal static bool CanAddCableSpool(
        int currentSpools,
        int configuredMaximumSpools) =>
        currentSpools >= 0 &&
        configuredMaximumSpools > 0 &&
        currentSpools < configuredMaximumSpools;

    internal static bool IsValidEquipmentLimit(int units) =>
        units >= CapacityLimits.MinimumEquipmentUnits &&
        units <= CapacityLimits.MaximumEquipmentUnits;

    internal static bool IsValidCableSpoolLimit(int spools) =>
        spools >= CapacityLimits.MinimumCableSpools &&
        spools <= CapacityLimits.MaximumCableSpools;
}
