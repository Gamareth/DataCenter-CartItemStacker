# Release testing

Use copied or disposable save files. Do not use the only copy of a primary save.

## Clean-start tests

- Start with an empty trolley.
- Confirm that the trolley remains still after the first placement.
- Place and remove every supported equipment category.
- Confirm that stored items remain selectable.
- Click stored cargo while holding another supported item and confirm that the held item is placed.

## Equipment tests

- Mix 3U and 7U servers, switches, routers, and firewalls.
- Confirm that the lowest stack is selected, with stack 1 winning equal-height ties.
- Fill both stacks to the configured U limit.
- Confirm that the next item is rejected without disturbing existing cargo.
- Remove bottom, middle, and top items and verify smooth local compaction.

## Boxed-rack and patch-panel tests

- Fill an existing boxed-rack row before creating another 13U row.
- Confirm that boxed racks never move between equipment stacks during local compaction.
- Place seven patch panels per layer on both equipment stacks.
- Remove panels from different columns and layers.
- Verify that boxes remain below patch panels.

## Accessory tests

- Test all four filled SFP module tray types.
- Test empty tray storage and module return behavior.
- Fill the shared overflow row.
- Fill both cable-spool stacks to the configured limit.
- Remove lower cable spools and verify smooth vertical compaction.

## Save and load tests

- Empty trolley.
- Each storage section in isolation.
- Fully mixed trolley.
- Trolley with gaps created by prior removals.
- Two consecutive save/load cycles.

After each load, verify item count, section assignment, alignment, selectability, compaction, trolley movement, and log errors.

## Configuration tests

- `EquipmentStackMaxUnits = 24`, `42`, and an invalid value.
- `CableSpoolsPerStack = 1`, `4`, `8`, and an invalid value.
- `AnimationSpeed = 0.5`, `1.0`, and `2.0`.
- Disable with an empty trolley.
- Request disable with a loaded trolley, unload it completely, and confirm native behavior afterwards.
