# WinUI 3 for the GUI

The GUI uses WinUI 3 (Windows App SDK, C# on .NET 10), unpackaged. We chose it over WPF's Fluent theme (still experimental in .NET 10), Avalonia (third-party, fresh major version) and Tauri (two languages), because it gives the native Windows 11 look with the least hand-styling. The cost is the size of what it needs to run: the Windows App SDK and the .NET runtime, neither of which a clean Windows 11 has.

## Distribution

The package (`app/build-package.ps1`) is a zip that carries both runtimes inside, self-contained, so the user unzips it and opens `ArkPar.exe` with nothing to install. The zip is about 93 MB.

At first we planned to have the user install the Windows App SDK runtime once. That plan overlooked that a clean Windows 11 lacks .NET 10 too. With .NET inside the zip and the Windows App SDK runtime installed separately, the zip is about 64 MB. But an app that does not open until the user has done a manual step is easy to get wrong, so we chose the bigger zip with no step to forget (WPC-380).

Development builds (`dotnet build`, `dotnet run`) stay framework-dependent, so they are quick. Only the package carries the runtimes.
