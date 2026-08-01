# Cart Item Stacker

Cart Item Stacker replaces the trolley stacking system in **Data Center** with a high-capacity, organized layout.

Do you find yourself cramming more and more equipment onto the trolley, only to struggle with a cart that was never designed for it? In the real world, you would definitely stack it better.

Search no more. Cart Item Stacker replaces the trolley's entire stacking system.

The result is the kind of capacity a real-world data center technician might squeeze onto a trolley — plus an extra 25% for gameplay convenience.

## Features

- Servers, switches, firewalls, boxed racks, and patch panels neatly arranged across two vertical equipment stacks of up to 42U each.
- Dedicated storage for SFP module trays, without modifying the trolley model.
- Native manual SFP insertion: click the specific tray you want to fill, just
  as in the unmodded game. The tray is then rearranged within the trolley layout.
- Two dedicated stacks for cable spools.
- Automatic sorting and smooth compaction when items are removed.
- Configurable equipment and cable-spool capacity.
- Event-driven behavior without continuous inventory or trolley polling.

In short: everything a data center technician with ambitious stacking plans could want.

## Requirements

- Data Center on Windows.
- [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 or a compatible 0.7.x release.
- A single-player game. Multiplayer and co-op are currently untested and unsupported.

Version 1.0.0 was built against Unity `6000.4.12f1`, MelonLoader `0.7.3 Open-Beta`, and the Data Center IL2CPP assemblies available on August 1, 2026.

## Installation

See [Installation](docs/INSTALLATION.md) for GitHub and Steam Workshop instructions.

## Configuration

The mod creates `UserData/CartItemStacker.cfg` after its first successful launch.
Configuration changes are loaded when the game starts.

| Setting | Default | Supported values | Purpose |
| --- | ---: | --- | --- |
| `Enabled` | `true` | `true`, `false` | Requests whether the custom stacking system should be active. Disabling becomes effective after the trolley is empty. |
| `EquipmentStackMaxUnits` | `42` | `24` through `42` | Limits newly accepted equipment per stack without removing physical slots. |
| `CableSpoolsPerStack` | `4` | `1` through `8` | Limits newly accepted cable spools per cable stack. |
| `AnimationSpeed` | `1.0` | `0.5` through `2.0` | Controls sorting and compaction animation speed. |
| `RestackCargoIndicator` | `true` | `true`, `false` | Pulses cargo orange and charcoal only during save-load restacking, then shows ready green for one second. |
| `DebugLogging` | `false` | `true`, `false` | Enables detailed placement diagnostics for bug reports. |

Lowering a capacity never removes or relocates existing cargo merely because it is above the new limit. New items are rejected until the configured limit permits them.

## Building

See [Building from source](docs/BUILDING.md).

## Reporting problems

Please use the GitHub issue template and attach:

- `MelonLoader/Latest.log`;
- the exact action that triggered the problem;
- whether the trolley was loaded from a save;
- screenshots when alignment or physics is involved.

## License

Copyright (C) 2026 Gamareth.

Cart Item Stacker is licensed under `GPL-3.0-or-later` with the [Data Center linking exception](LINKING_EXCEPTION.md). Distributed derivative versions based on this source must remain available under the GPL terms, except for the specifically permitted external linking targets.
