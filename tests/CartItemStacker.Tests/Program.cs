using CartItemStacker;

var failures = new List<string>();

Check("24U accepts exactly 24U",
    CapacityRules.CanAddEquipmentHeight(17, 7, 24));
Check("24U rejects 25U",
    !CapacityRules.CanAddEquipmentHeight(18, 7, 24));
Check("42U accepts exactly 42U",
    CapacityRules.CanAddEquipmentHeight(35, 7, 42));
Check("Existing box-row gap consumes no additional U",
    CapacityRules.CanAddEquipmentHeight(42, 0, 42));
Check("Negative equipment input is rejected",
    !CapacityRules.CanAddEquipmentHeight(-1, 1, 42));
Check("Overflowing equipment input is rejected",
    !CapacityRules.CanAddEquipmentHeight(int.MaxValue, 1, 42));
Check("Fourth spool is accepted at a four-spool limit",
    CapacityRules.CanAddCableSpool(3, 4));
Check("Fifth spool is rejected at a four-spool limit",
    !CapacityRules.CanAddCableSpool(4, 4));
Check("Non-positive spool limits are rejected",
    !CapacityRules.CanAddCableSpool(0, 0));
Check("One spool per stack is supported",
    CapacityRules.IsValidCableSpoolLimit(1));
Check("Eight spools per stack is supported",
    CapacityRules.IsValidCableSpoolLimit(8));
Check("Nine spools per stack is rejected",
    !CapacityRules.IsValidCableSpoolLimit(9));
Check("24U is a valid equipment limit",
    CapacityRules.IsValidEquipmentLimit(24));
Check("42U is a valid equipment limit",
    CapacityRules.IsValidEquipmentLimit(42));
Check("23U is rejected",
    !CapacityRules.IsValidEquipmentLimit(23));
Check("Patch panels stay in place within the same row",
    !LayerCompactionRules.IsFromHigherRow(6, 0, 7));
Check("Patch panel from the next row may fill a lower-row gap",
    LayerCompactionRules.IsFromHigherRow(7, 0, 7));
Check("Patch panel does not compact sideways in the second row",
    !LayerCompactionRules.IsFromHigherRow(13, 7, 7));
Check("Boxes use the same higher-row-only rule",
    LayerCompactionRules.IsFromHigherRow(3, 1, 3));
Check("Invalid layer compaction input is rejected",
    !LayerCompactionRules.IsFromHigherRow(7, 0, 0));
if (failures.Count == 0)
{
    Console.WriteLine("All Cart Item Stacker logic tests passed.");
    return 0;
}

Console.Error.WriteLine($"{failures.Count} test(s) failed:");
foreach (var failure in failures)
    Console.Error.WriteLine($"- {failure}");
return 1;

void Check(string name, bool passed)
{
    if (!passed)
        failures.Add(name);
}
