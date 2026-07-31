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
dotnet build src/CartItemStacker/CartItemStacker.csproj -c Release
```

The resulting DLL is written to:

```text
src/CartItemStacker/bin/Release/net6.0/CartItemStacker.dll
```

## Run the dependency-free capacity tests

```powershell
dotnet run --project tests/CartItemStacker.Tests/CartItemStacker.Tests.csproj -c Release
```

## Test in game

Never replace the installed DLL while Data Center is running. Back up test saves and validate the complete checklist in [Release testing](RELEASE_TESTING.md) before publishing a build.
