# Changelog

All notable changes to Cart Item Stacker are documented in this file.

## [0.9.0-rc.1] - Unreleased

### Added

- Two equipment stacks with a physical maximum of 42U each.
- Upright boxed-rack rows that consume 13U per row.
- Seven patch panels per layer above servers and boxed racks.
- Dedicated filled and empty SFP module tray storage.
- Two dedicated cable-spool stacks with eight physical positions each.
- Event-driven sorting and local compaction animations.
- Configurable equipment limits, cable-spool limits, animation speed, debug logging, and desired enabled state.
- Bounded save-load discovery and reconstruction.
- Dependency-free capacity-rule tests.

### Changed

- Renamed the mod from Cart Stacker to Cart Item Stacker.
- Changed the public author name to Gamareth.
- Split the original single source file into functional components.
- Centralized shared layout and timing constants.
- Limited detailed placement diagnostics to debug mode.
- Removed inactive scene-wide physics diagnostics from the runtime.

### Known limitations

- Multiplayer and co-op are untested and unsupported.
- Steam Workshop subscription alone does not place the DLL in the game's `Mods` directory.
