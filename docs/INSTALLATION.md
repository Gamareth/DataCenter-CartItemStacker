# Installation

## Requirements

1. Install and run Data Center once.
2. Install MelonLoader 0.7.3 or a compatible 0.7.x release.
3. Start the game once through MelonLoader so that the `Mods` directory and IL2CPP support files are created.
4. Close the game.

## Install from GitHub

1. Download `CartItemStacker.dll` from the latest GitHub release.
2. Copy the DLL into:

   ```text
   <Steam Library>\steamapps\common\Data Center\Mods
   ```

3. Remove older `CartStacker.dll` or `CartItemStacker.dll` versions from that directory.
4. Start Data Center.
5. Confirm that the MelonLoader console contains:

   ```text
   Cart Item Stacker 1.1.0 initialized
   ```

## Install from Steam Workshop

Steam downloads code mods into the Workshop content directory but Data Center
does not automatically load their DLLs. Version 1.1.0 therefore needs one final
manual bootstrap copy. Later Workshop updates can install themselves safely
after the game exits.

1. Subscribe to Cart Item Stacker in the Steam Workshop.
2. Locate the downloaded Workshop item under:

   ```text
   <Steam Library>\steamapps\workshop\content\4170200\<Workshop item ID>
   ```

3. Copy `CartItemStacker.dll` into:

   ```text
   <Steam Library>\steamapps\common\Data Center\Mods
   ```

4. Start Data Center and verify the initialization message shown above.

## Automatic Workshop updates

Automatic Workshop updates are enabled by default when all of these conditions
are met:

- Cart Item Stacker 1.1.0 or newer is already present in `Data Center/Mods`;
- the Steam account is subscribed to Workshop item `3775738163`;
- Steam has finished installing that Workshop item; and
- `AutoUpdateFromWorkshop = true` in `UserData/CartItemStacker.cfg`.

After the game's native Workshop synchronization completes, the mod compares
the installed DLL with the subscribed Workshop copy. A verified newer build
starts a hidden updater that waits for the exact Data Center process to exit.
The updater then replaces only `Data Center/Mods/CartItemStacker.dll`. The new
version becomes active on the next launch.

The update is rejected if its assembly identity, version, SHA-256 hash, or
Workshop state does not match what was verified while the game was running.
Details are written to `UserData/CartItemStackerUpdater.log`.

The platform-neutral managed updater is embedded in the mod DLL. Its DLL,
dependency manifest, and runtime configuration are extracted only when an
update is ready and removed automatically on the first launch after a
successful installation. No separate updater files need to be copied into the
Workshop package or the game's `Mods` directory. The updater uses the .NET host
already available to the MelonLoader installation; Windows is the currently
tested and supported game platform.

## Manual updating

Close the game and replace the existing `CartItemStacker.dll` with the newer release.

## Disabling

Close Data Center, set `Enabled = false` in `UserData/CartItemStacker.cfg`, and restart the game. If cargo is still present, the mod continues managing that cargo until the trolley becomes empty, then restores the native trolley slots.

For complete manual removal:

1. Empty the trolley.
2. Close the game.
3. Rename `CartItemStacker.dll` to `CartItemStacker.dll.disabled`, or remove it from the `Mods` directory.

MelonLoader's `--no-mods` command-line option disables all installed mods and can be used for troubleshooting.
