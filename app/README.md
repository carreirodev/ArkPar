# ArkPar app

The new GUI (WinUI 3, C# on .NET 10, unpackaged) and its tests. It drives the upstream engine `par2j64.exe`, built unmodified from `../source/par2j` (see `../docs/adr/`).

- `src/ArkPar.App`: the GUI.
- `src/ArkPar.Engine`: runs the engine in a hidden console, reports its progress, pauses, resumes and cancels it with the keys it reads there, and reads its result (`EngineRun`).
- `src/ArkPar.Screens`: what each screen shows and does (states, counts, texts in pt-BR), without WinUI, so the tests drive the screens directly. It also keeps the settings (`%LOCALAPPDATA%\ArkPar\settings.json`) and the `.par2` association (`HKEY_CURRENT_USER\Software\Classes`; the tests use a throwaway key instead). The app only lays them out and adds what needs Windows itself: the file picker, the thumbnails Explorer shows, and the notifications.
- `tests/ArkPar.Tests`: unit tests over captured real engine output (`Engine/Fixtures`), and tests that run the real engine.

New code in `app/` is licensed GPL-2.0-or-later, like the upstream engine.

Additional permission: the copyright holders of the code in `app/` also permit combining it with the Microsoft Windows App SDK and the .NET runtime, and distributing it together with them, each under its own license terms. The package (`build-package.ps1`) does exactly that. This permission covers only the code in `app/`. The engine (`source/par2j`) is a separate program under its own GPL terms.

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

The first time it opens, ArkPar asks whether a double click on a `.par2` should open it; Configurações changes the answer, and holds the GPU and processor cores every task uses. When a create, verification or repair ends while the window is minimized or behind another app, a Windows notification says how it ended.

Build the package that takes ArkPar to another PC, `app/artifacts/ArkPar-win-x64.zip`. It builds the engine, publishes the app self-contained (.NET and Windows App SDK inside, ADR-0002), and adds `package/LEIA-ME.txt` and the license texts. On the other PC, unzip it and open `ArkPar\ArkPar.exe`; nothing needs installing:

```
pwsh -File app/build-package.ps1
```

Redraw the app's icon, `app/src/ArkPar.App/Assets/ArkPar.ico`, after changing `ArkPar-small.svg` or `ArkPar-large.svg` beside it. It needs Chrome (pass `-Chrome` with its path if it is not in the default place); commit the `.ico` it writes:

```
pwsh -File app/build-icon.ps1
```
