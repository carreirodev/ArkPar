# ArkPar app

The new GUI (WinUI 3, C# on .NET 10, unpackaged) and its tests. It drives the upstream engine `par2j64.exe`, built unmodified from `../source/par2j` (see `../docs/adr/`).

- `src/ArkPar.App`: the GUI.
- `src/ArkPar.Engine`: runs the engine in a hidden console, reports its progress, pauses, resumes and cancels it with the keys it reads there, and reads its result (`EngineRun`).
- `src/ArkPar.Screens`: what each screen shows and does (states, counts, texts in pt-BR), without WinUI, so the tests drive the screens directly. The app only lays them out and adds what needs Windows itself: the file picker and the thumbnails Explorer shows.
- `tests/ArkPar.Tests`: unit tests over captured real engine output (`Engine/Fixtures`), and tests that run the real engine.

New code in `app/` is licensed GPL-2.0-or-later, like the upstream engine.

## Requirements

- Windows 11, x64.
- .NET 10 SDK.
- Visual Studio Build Tools 2026 with the C++ desktop workload (toolset v145), for the engine.
- Windows App SDK 2.6 runtime, to run the app from a development build. The package below needs nothing installed.

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

It opens on the Create screen. To verify a recovery set as soon as the app opens, pass its `.par2` file:

```
dotnet run --project app/src/ArkPar.App -- "D:\Fotos\Viagem\Viagem.par2"
```

On the Verify and repair screen, "Verificar uma pasta…" verifies every recovery set inside a folder, at any depth, and sums them up by folder; each folder opens into the set's detail.

Build the package that takes ArkPar to another PC, `app/artifacts/ArkPar-win-x64.zip`. It builds the engine, publishes the app self-contained (.NET and Windows App SDK inside, ADR-0002), and adds `package/LEIA-ME.txt` and the license texts. On the other PC, unzip it and open `ArkPar\ArkPar.exe`; nothing needs installing:

```
pwsh -File app/build-package.ps1
```
