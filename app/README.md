# ArkPar app

The new GUI (WinUI 3, C# on .NET 10, unpackaged) and its tests. It drives the upstream engine `par2j64.exe`, built unmodified from `../source/par2j` (see `../docs/adr/`).

New code in `app/` is licensed GPL-2.0-or-later, like the upstream engine.

## Requirements

- Windows 11, x64.
- .NET 10 SDK.
- Visual Studio Build Tools 2026 with the C++ desktop workload (toolset v145), for the engine.
- Windows App SDK 2.6 runtime, to run the app.

## Commands

Run from the repository root.

Build the engine into `app/artifacts/engine/par2j64.exe` (pass `-PlatformToolset` to use another compiler):

```
pwsh -File app/build-engine.ps1
```

Build the app and tests:

```
dotnet build app/ArkPar.slnx
```

Run the tests (build the engine first; the engine tests fail with a message if it is missing):

```
dotnet test app/ArkPar.slnx
```

Run the app:

```
dotnet run --project app/src/ArkPar.App
```
