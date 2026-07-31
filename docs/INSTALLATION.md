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
   Cart Item Stacker 0.9.0-rc.1 initialized
   ```

## Install from Steam Workshop

Steam downloads code mods into the Workshop content directory but Data Center does not automatically load their DLLs.

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

## Updating

Close the game and replace the existing `CartItemStacker.dll` with the newer release.

## Disabling

Close Data Center, set `Enabled = false` in `UserData/CartItemStacker.cfg`, and restart the game. If cargo is still present, the mod continues managing that cargo until the trolley becomes empty, then restores the native trolley slots.

For complete manual removal:

1. Empty the trolley.
2. Close the game.
3. Rename `CartItemStacker.dll` to `CartItemStacker.dll.disabled`, or remove it from the `Mods` directory.

MelonLoader's `--no-mods` command-line option disables all installed mods and can be used for troubleshooting.
