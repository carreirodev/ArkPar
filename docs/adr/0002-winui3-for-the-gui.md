# WinUI 3 for the GUI

The GUI uses WinUI 3 (Windows App SDK, C# on .NET 10), unpackaged. We chose it over WPF's Fluent theme (still experimental in .NET 10), Avalonia (third-party, fresh major version) and Tauri (two languages), because it gives the native Windows 11 look with the least hand-styling. The cost is that users install the Windows App SDK runtime once, which we accepted because ArkPar is Windows-only and meant for personal use.
