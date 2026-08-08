# Building from source

## Prerequisites

- .NET 6 SDK.
- Data Center with MelonLoader installed.
- Generated IL2CPP assemblies under `MelonLoader/Il2CppAssemblies`.

No game assemblies or proprietary game assets are included in this repository.

## Configure the game path

PowerShell:

```powershell
$env:DATA_CENTER_GAME_PATH = 'D:\SteamLibrary\steamapps\common\Data Center'
```

Alternatively, pass the path directly to MSBuild:

```powershell
dotnet build src/CartItemStacker/CartItemStacker.csproj -c Release `
  /p:DataCenterGamePath='D:\SteamLibrary\steamapps\common\Data Center'
```

## Build

```powershell
dotnet build CartItemStacker.sln -c Release
```

The resulting DLL is written to:

```text
src/CartItemStacker/bin/Release/net6.0/CartItemStacker.dll
```

The post-exit updater is published as a platform-neutral, framework-dependent
managed application and embedded into `CartItemStacker.dll` during the build.
The embedded payload consists of its DLL, dependency manifest, and runtime
configuration; it contains no operating-system-specific app host.

Its intermediate publish output is written to:

```text
src/CartItemStacker/obj/Release/EmbeddedUpdater/
```

A Workshop package that supports automatic updates only needs the mod DLL in
its content root:

```text
CartItemStacker.dll
```

The mod extracts its embedded updater into `UserData/CartItemStackerUpdater`
only when a verified update is ready. A successful update leaves a marker that
causes the temporary updater DLL, `.deps.json`, `.runtimeconfig.json`, marker,
and empty directory to be removed on the next game launch.

## Run the dependency-free capacity tests

```powershell
dotnet run --project tests/CartItemStacker.Tests/CartItemStacker.Tests.csproj -c Release
```

## Test in game

Never replace the installed DLL while Data Center is running. Back up test saves and validate the complete checklist in [Release testing](RELEASE_TESTING.md) before publishing a build.
