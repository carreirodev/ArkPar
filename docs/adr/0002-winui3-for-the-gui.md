# WinUI 3 for the GUI

The GUI uses WinUI 3 (Windows App SDK, C# on .NET 10), unpackaged. We chose it over WPF's Fluent theme (still experimental in .NET 10), Avalonia (third-party, fresh major version) and Tauri (two languages), because it gives the native Windows 11 look with the least hand-styling. The cost is the size of what it needs to run: the Windows App SDK and the .NET runtime, neither of which a clean Windows 11 has.

## Distribution

The package (`app/build-package.ps1`) is a zip that carries both runtimes inside, self-contained, so the user unzips it and opens `ArkPar.exe` with nothing to install. The zip is about 68 MB, and about 172 MB once unzipped.

The app references only the Windows App SDK parts it uses (`Microsoft.WindowsAppSDK.WinUI`, `.Foundation` for the file pickers, and `.Runtime`, which development builds need to find the installed runtime), not the full `Microsoft.WindowsAppSDK` package. The full package also carries its AI, machine-learning, Search and Widgets parts, which made the zip about 93 MB and the unzipped folder about 235 MB (WPC-403).

At first we planned to have the user install the Windows App SDK runtime once. That plan overlooked that a clean Windows 11 lacks .NET 10 too. With .NET inside the zip and the Windows App SDK runtime installed separately, the zip was about 64 MB (measured with the full package). But an app that does not open until the user has done a manual step is easy to get wrong, so we chose the bigger zip with no step to forget (WPC-380).

Development builds (`dotnet build`, `dotnet run`) stay framework-dependent, so they are quick. Only the package carries the runtimes.

Carrying the Windows App SDK inside costs the parts of it that need its runtime installed on the PC (its Singleton package). App notifications are one: on a clean Windows 11, `AppNotificationManager.IsSupported()` is false for the package. So ArkPar's end-of-task notifications use the notifications built into Windows (`Windows.UI.Notifications`) instead, registering the app's name for the current user (WPC-382). Check any other Windows App SDK feature against a clean PC (Windows Sandbox) before relying on it.
