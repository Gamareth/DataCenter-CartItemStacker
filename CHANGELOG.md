# Changelog

All notable changes to Cart Item Stacker are documented in this file.

## [1.1.0] - 2026-08-08

### Added

- Added an event-driven Steam Workshop update check after Data Center completes
  its native Workshop synchronization.
- Added an embedded platform-neutral managed updater that waits for the exact
  game process to exit before atomically replacing the installed mod DLL, then
  cleans up its temporary runtime files on the next successful launch.
- Added assembly identity, semantic version, Workshop timestamp, and SHA-256
  validation before and during installation.
- Added the `AutoUpdateFromWorkshop` preference, enabled by default.

### Changed

- Increased filled SFP overflow-tray capacity from 20 to 21 trays.
- Increased empty SFP tray capacity from 20 to 21 trays.

### Compatibility

- Built against Data Center Steam build `24534930`, Unity `6000.4.12f1`,
  MelonLoader `0.7.3 Open-Beta`, and .NET 6.

## [1.0.1] - 2026-08-06

### Changed

- Increased filled SFP overflow-tray capacity from 19 to 20 trays.
- Increased empty SFP tray capacity from 17 to 20 trays.

### Compatibility

- Built and tested with Data Center Steam build `24534930`, Unity
  `6000.4.12f1`, MelonLoader `0.7.3 Open-Beta`, and .NET 6.

## [1.0.0] - 2026-08-01

### Added

- Two independent equipment stacks with a configurable limit of 24U through
  42U per stack.
- Dedicated layouts for boxed racks, seven patch panels per layer, filled and
  empty SFP module trays, and two cable-spool stacks.
- Deterministic compaction, save-load reconstruction, and an optional visual
  restack indicator.
- Configuration for the requested mod state, equipment and cable-spool limits,
  animation speed, movement indicators, and debug logging.

### Changed

- Centralized slot-pose resolution and kinematic stored-cargo handling make
  placement, removal, compaction, and reconstruction use the same layout data.
- Loose SFP modules keep the game's native manual insertion behavior while
  complete SFP trays remain managed trolley cargo.
- Disabling the mod is a desired state that becomes active when the trolley is
  empty, so loaded cargo is never abandoned between layout systems.

### Compatibility

- Built for Data Center on Windows using Unity `6000.4.12f1`, MelonLoader
  `0.7.3 Open-Beta`, and .NET 6.
- Single-player is supported. Multiplayer and co-op are untested and
  unsupported in this release.

## Development candidates

## [0.9.0-rc.27] - 2026-08-01

### Changed

- Loose SFP modules are no longer automatically routed by Cart Item Stacker.
  Players select and click the intended tray themselves, using the game's
  native module-insertion behavior.
- The mod only observes the public native interaction on the specifically
  clicked tray and then rearranges that stored tray when it changes between the
  empty and filled layout.

### Removed

- Removed the experimental best-compatible-tray selection and remote-insertion
  code from the release runtime. Its design and lessons are retained in
  `docs/DEFERRED_SFP_AUTO_ROUTING.md` for possible future work.

## [0.9.0-rc.26] - 2026-08-01

### Fixed

- Automatic SFP routing now invokes only the game's native module-insertion
  branch. It no longer executes the destination tray's subsequent pickup path,
  which could put the empty destination tray into the player's hand.
- Native SFP insertion is validated before success: the destination fill must
  increase by exactly one and the inserted module must leave the player's hand.
  A failed postcondition is logged and never retried against another tray.

## [0.9.0-rc.25] - 2026-08-01

### Fixed

- Automatic SFP routing now invokes the destination tray's complete native
  interaction flow. This clears the player's held-object state after insertion
  and prevents the same ghost-held module from being inserted repeatedly or
  blocking subsequent item pickup.

## [0.9.0-rc.24] - 2026-08-01

### Fixed

- A filled SFP tray can no longer be misclassified as a loose SFP module just
  because it contains module child components. Complete trays always use the
  normal trolley tray-placement flow; only genuinely loose modules use
  automatic in-tray routing.

## [0.9.0-rc.23] - 2026-08-01

### Fixed

- Held SFP modules now route to the best compatible tray when any stored item
  on the same trolley is clicked. These clicks are intercepted before the
  generic equipment whitelist, so an SFP is no longer rejected as ordinary
  server-section cargo when a server, box, patch panel, or cable spool is
  clicked.

## [0.9.0-rc.22] - 2026-08-01

### Added

- Clicking any stored trolley tray with an SFP module now automatically routes
  the module to the best compatible destination: the fullest partially filled
  tray first, then an eligible empty tray.

### Changed

- Automatic SFP insertion delegates the final in-tray position and rotation to
  the game's native `ReturnSFPDirectly` method. The mod no longer overwrites
  module-local rotations after native insertion or during tray arrangement.

## [0.9.0-rc.21] - 2026-08-01

### Changed

- Patch panels now remain in their existing horizontal positions when another
  panel is removed from the same row. Only a panel from a strictly higher row
  may fall into a lower-row gap, matching boxed-rack compaction.
- Box and patch-panel candidate selection now share one tested layered-cargo
  compaction rule.

## [0.9.0-rc.20] - 2026-08-01

### Changed

- The orange-and-charcoal cargo pulse and one-second ready-green state now run
  exclusively during save-load restacking. Normal placement, removal,
  compaction and section gravity retain their standard visuals.
- The preference was renamed from `CargoMovementIndicator` to
  `RestackCargoIndicator` to reflect its intentionally limited scope.

## [0.9.0-rc.19] - 2026-08-01

### Added

- An optional event-driven cargo movement indicator. Every item involved in a
  placement, compaction or section reorder pulses orange and charcoal while it
  moves, shows ready green for one second, and then restores its original
  renderer property block.
- The `CargoMovementIndicator` preference is enabled by default and can be
  disabled independently from movement animation and debug logging.

### Changed

- Compaction of already stored server equipment now uses the cart's
  deterministic absolute-pose animation instead of invoking the native
  placement route a second time.

### Fixed

- Interrupted server compaction now commits every affected item to its exact
  slot-derived pose before restoring collisions, preventing equipment from
  remaining off-screen or out of position until a later cart interaction.

## [0.9.0-rc.18] - 2026-08-01

### Changed

- Box movement now reflows the complete affected stack toward absolute targets
  derived from the authoritative logical assignments.
- Patch-panel animation retains its visible gravity movement but always ends
  with a one-shot absolute normalization against the settled support surface.

### Fixed

- Rapid remove-and-replace sequences can no longer leave a permanent empty
  visual gap when a newer layout event interrupts an older box or patch-panel
  animation.
- Placement finalization now reconciles dependent boxes and patch panels after
  the supporting equipment has reached its final native pose.

## [0.9.0-rc.17] - 2026-08-01

### Changed

- The reconstruction indicator now alternates between safety orange and dark
  charcoal instead of two orange-red shades, while ready remains green.
- Stored cargo physics normalization is centralized and reused by normal
  placement, compaction, and save-load reconstruction.

### Fixed

- Manually placed equipment now retains an item-owned kinematic Rigidbody
  instead of contributing its colliders to the trolley's dynamic compound
  body. Later compaction can therefore no longer launch the trolley through
  collisions with other stored cargo.
- Compaction repairs missing item Rigidbodies before movement and again before
  collider restoration, covering cargo placed by an earlier release candidate.
- Collision diagnostics isolate optional Unity collision properties so a
  stripped accessor can no longer suppress the complete collision record.

## [0.9.0-rc.16] - 2026-08-01

### Added

- Extended the temporary physics diagnostics across the complete save-load
  reconstruction without changing trolley physics behaviour.
- Reconstruction checkpoints now report trolley position and rotation drift,
  chassis Rigidbody velocity, and cargo Rigidbody state before staging, after
  staging, after every parallel placement round, and after collider restore.
- Collision probes are attached to the trolley hierarchy and loaded cargo
  before staging so an external collision or native reconstruction impulse can
  be identified at the moment it occurs.

### Fixed

- Save-load reconstruction now freezes the trolley chassis reference frame and
  disables handle interaction until all final poses and colliders have survived
  one protected physics step. Original constraints and handle state are then
  restored with stale linear and angular momentum cleared.

## [0.9.0-rc.15] - 2026-08-01

### Added

- Temporary event-driven physics diagnostics for trolley lift investigation
  while debug logging is enabled.
- One-shot post-placement snapshots at 0, 1, 5, and 12 seconds that report
  trolley velocity, cargo Rigidbody ownership, and kinematic state.
- Collision-event logging during the 15 seconds after a placement, including
  both collision partners, relative velocity, impulse, and Rigidbody state.
  No per-frame polling is used.

## [0.9.0-rc.14] - 2026-08-01

### Fixed

- Save-load reconstruction no longer replaces the trolley's persistent native temporary placement transform with short-lived reconstruction targets.
- Failed native trolley placements now roll back all pending section assignments, reservations, and collision isolation. A later unrelated click can therefore never complete a stale tray transaction or launch the trolley.

## [0.9.0-rc.13] - 2026-07-31

### Fixed

- SFP module trays now apply Data Center's native `SFPBox` position and rotation profile when resolving their prefab-root pose. Loaded, manually placed, and reordered trays therefore share the same upright orientation.

## [0.9.0-rc.12] - 2026-07-31

### Added

- A central cart pose resolver now converts every logical section assignment into either its layout anchor or its definitive stored-item root pose.

### Changed

- Normal trolley placement and save-load reconstruction now converge through the same guarded `MoveToStorage` boundary. Direct box gravity and module-tray sorting reuse the same section-owned root-pose calculations.
- Module-tray sorting now resolves exact destination poses instead of copying or extrapolating whichever occupied tray pose happened to be available.

### Fixed

- Active, overflow and empty SFP trays now share the native 30cm downward prefab-root profile across placement, save-load and sorting. Tray contents remain attached and inherit the resolved tray pose.
- Boxed racks now use one root-pose formula for placement, save-load and local gravity, eliminating accumulated relative-position drift.
- Cart pose resolution is restricted to registered trolley cargo while the custom layout is active, preventing unrelated rack storage calls from being intercepted.

## [0.9.0-rc.11] - 2026-07-31

### Fixed

- Save-load reconstruction now applies the boxed-rack prefab root offset used by normal trolley placement. Loaded boxes therefore retain the same visible pose as boxes manually removed and placed again, without changing the canonical Z-axis row layout.

## [0.9.0-rc.10] - 2026-07-31

### Fixed

- Restored boxed-rack rows to their calibrated trolley-local Z axis while keeping every box centered on local X. RC9 incorrectly treated world-aligned renderer bounds as trolley-local dimensions and moved box columns across X.

## [0.9.0-rc.9] - 2026-07-31

### Fixed

- Upright boxed-rack columns now run across trolley-local X while trolley stacks remain separated on Z. The previous layout moved neighbouring boxes toward the other server stack along their approximately 0.894m-long axis, causing severe overlap and trolley liftoff.
- Box rehydration and local gravity now use the same corrected X-axis column model as normal placement.

## [0.9.0-rc.8] - 2026-07-31

### Added

- The trolley pulses orange while loaded cargo is being reconstructed, shows ready green for one second after collider restoration, and then restores its original renderer properties.

### Fixed

- Save-load reconstruction now runs the server, module-tray, and cable-spool zones in three independent parallel lanes while keeping every lane strictly sequential.
- The server lane preserves the dependency order equipment, boxed racks, then patch panels, preventing target heights from being calculated from unfinished cargo.

## [0.9.0-rc.7] - 2026-07-31

### Changed

- Loaded items are staged in a compact grid directly above the 42U cargo envelope instead of occupying surrounding room space.
- Save-load reconstruction now starts a second collision-suppressed native placement halfway through the previous flight, reducing the rebuild time by approximately 50% without shortening any item's settle time.

### Fixed

- Boxed racks and SFP trays retain their proven saved orientation after native reconstruction, avoiding pivot-dependent clipping and upside-down trays.

## [0.9.0-rc.6] - 2026-07-31

### Fixed

- Save-load reconstruction now stages all supported cargo outside the trolley envelope with disabled colliders, then rebuilds it sequentially through private native placement targets.
- Reconstructed cargo retains its own kinematic Rigidbody, preventing its colliders from becoming part of the trolley's compound physics body.
- Cargo colliders are restored only after every section has been rebuilt and all trolley collision-ignore pairs have been re-established.

## [0.9.0-rc.5] - 2026-07-31

### Fixed

- Normal equipment reinserted after save-load reconstruction is now collision-isolated for its entire native placement flight and has its restored Rigidbody removed before colliders are re-enabled.

## [0.9.0-rc.4] - 2026-07-31

### Fixed

- Loaded cargo is now parented only after its native placement animation completes, preventing transform-space corruption and trolley physics impulses during reconstruction.

## [0.9.0-rc.3] - 2026-07-31

### Fixed

- Reconstructed servers, boxed racks, patch panels, and cable spools are now explicitly parented to the trolley before and after their native placement animation.

## [0.9.0-rc.2] - 2026-07-31

### Added

- Native load-event integration that reconstructs trolley ownership and logical slots from saved item transforms.
- Section-aware reconstruction for equipment, boxed racks, patch panels, SFP trays, and cable spools.

### Changed

- Load reconstruction no longer depends on transient native trolley reservations or runtime-only ownership fields.
- Repeated initialization coroutines are cancelled when a newer native load cycle starts.

## [0.9.0-rc.1] - 2026-07-30

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
