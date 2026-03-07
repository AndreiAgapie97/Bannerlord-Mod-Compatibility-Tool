# Bannerlord Mod Compatibility Tool

Player-first compatibility analyzer for Mount & Blade II: Bannerlord mod stacks.

The desktop app is the primary product. It scans installed modules, reads launcher order, inspects Harmony/runtime signals, and turns that into player-facing guidance across three steps:

1. `Findings`
2. `Load Order`
3. `Validate`

## Repository layout

- `BannerlordModCompat.App` - WPF desktop app
- `BannerlordModCompat.Core` - scanner, analyzers, load-order logic, runtime correlation
- `BannerlordModCompat.Tests` - automated regression tests
- `SyntheticHarmonyLogs` - small synthetic fixtures used by tests

## Requirements

- Windows for the WPF app
- .NET 10 SDK

## Run the desktop app

```powershell
dotnet run --project BannerlordModCompat.App
```

## Build

```powershell
dotnet build BannerlordModCompat.slnx -c Debug
```

## Test

```powershell
dotnet test BannerlordModCompat.slnx -c Debug
```

## Publish the desktop app

```powershell
dotnet publish BannerlordModCompat.App\BannerlordModCompat.App.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

## What the app analyzes

- module manifest dependencies and declared incompatibilities
- launcher order and order-constraint violations
- save compatibility risks
- Harmony overlap and patch-stack metadata
- runtime log evidence from Bannerlord log sessions
- behavior/model/mission overlap heuristics

The app is intentionally player-facing. It does not treat raw static overlap as automatic proof of a crash, and it separates save compatibility warnings from live runtime incompatibility claims.

## Repository hygiene

- build outputs and local scan artifacts are ignored
- generated debug files are not part of the source tree
- exported reports should go into `Reports/` or another ignored folder

## Notes

- `SyntheticHarmonyLogs` is kept intentionally. It is test data, not user output.
- The repository is licensed under the MIT license. See `LICENSE`.
