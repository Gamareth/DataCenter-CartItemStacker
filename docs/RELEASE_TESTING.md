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
- Hold each filled SFP tray and click stored trolley cargo. Confirm that the
  complete tray follows the normal tray-placement animation and is never
  inserted into another tray as if it were a loose module.
- Test empty tray storage and native manual module return behavior.
- Hold a loose SFP module and click the specific compatible tray that should
  receive it. Confirm that native insertion succeeds without the mod selecting
  another tray.
- Click a server, boxed rack, patch panel, or cable spool while holding a loose
  SFP module. Confirm that the mod neither inserts it nor treats it as cargo.
- Fill an empty stored tray natively and confirm that the tray moves from the
  empty row into the appropriate filled-tray layout.
- Confirm that native module positions and rotations inside every tray type are
  preserved after insertion and subsequent tray sorting.
- Fill the shared overflow row.
- Fill both cable-spool stacks to the configured limit.
- Remove lower cable spools and verify smooth vertical compaction.

## Save and load tests

- Empty trolley.
- Each storage section in isolation.
- Fully mixed trolley.
- Trolley with gaps created by prior removals.
- Two consecutive save/load cycles.
- Load active, overflow, and empty SFP trays, then remove and replace one tray
  from each row. Its manually replaced root pose must exactly match its loaded
  pose.
- Trigger a filled-tray reorder after loading and confirm that every moved tray
  reaches the exact destination slot without flipping or inheriting another
  tray's transform.
- Remove and replace one boxed rack after loading and confirm that it retains
  the same row, column, height, and orientation.

After each load, verify item count, section assignment, alignment, selectability, compaction, trolley movement, and log errors.

## Configuration tests

- `EquipmentStackMaxUnits = 24`, `42`, and an invalid value.
- `CableSpoolsPerStack = 1`, `4`, `8`, and an invalid value.
- `AnimationSpeed = 0.5`, `1.0`, and `2.0`.
- `RestackCargoIndicator = true` and `false` during save-load reconstruction.
- Confirm that normal placement, removal, compaction and section gravity never
  apply the orange, charcoal or green restack colors.
- Disable with an empty trolley.
- Request disable with a loaded trolley, unload it completely, and confirm native behavior afterwards.
