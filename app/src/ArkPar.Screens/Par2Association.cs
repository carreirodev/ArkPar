// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32;

namespace ArkPar.Screens;

/// <summary>
/// Whether a double click on a .par2 file in Explorer opens ArkPar. Written for the current user only
/// (<c>HKEY_CURRENT_USER\Software\Classes</c>), so it needs no administrator. The app is a folder the user
/// unzips, not an install (ADR-0002), so registering again from a moved folder points the double click there.
/// </summary>
/// <remarks>
/// After a change Explorer must be told (<c>SHChangeNotify</c>), which the app does.
/// </remarks>
/// <param name="user">HKEY_CURRENT_USER, or a throwaway key standing for it in tests.</param>
/// <param name="appPath">The ArkPar.exe a double click starts.</param>
public sealed class Par2Association(RegistryKey user, string appPath)
{
    /// <summary>ArkPar's file type for .par2 files.</summary>
    public const string ProgId = "ArkPar.par2";

    private const string Classes = @"Software\Classes";
    private const string Extension = Classes + @"\.par2";
    private const string FileType = Classes + @"\" + ProgId;
    private const string OpenCommand = FileType + @"\shell\open\command";
    private const string OpenWith = Extension + @"\OpenWithProgids";

    // Where Windows keeps "Always use this app" for .par2. Its hash cannot be written by an app, by design.
    private const string UserChoice = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.par2\UserChoice";

    // The file type .par2 files had before ArkPar, given back when the user turns this off.
    private const string PreviousDefault = "ArkPar.PreviousDefault";

    private string Command => $"\"{appPath}\" \"%1\"";

    /// <summary>A double click on a .par2 starts this very ArkPar.exe (unless <see cref="AnotherAppChosen"/>).</summary>
    public bool IsRegistered
    {
        get
        {
            using var extension = user.OpenSubKey(Extension);
            using var command = user.OpenSubKey(OpenCommand);
            return extension?.GetValue("") as string == ProgId && command?.GetValue("") as string == Command;
        }
    }

    /// <summary>
    /// The user told Windows to always open .par2 files with another app, which wins over this association
    /// until the user picks ArkPar in Explorer's "Open with".
    /// </summary>
    public bool AnotherAppChosen
    {
        get
        {
            using var userChoice = user.OpenSubKey(UserChoice);
            return userChoice?.GetValue("ProgId") is string chosen && chosen != ProgId;
        }
    }

    /// <summary>Makes a double click on a .par2 open this ArkPar.exe and verify the set.</summary>
    public void Register()
    {
        using var extension = user.CreateSubKey(Extension);
        using var fileType = user.CreateSubKey(FileType);
        if (extension.GetValue("") is string previous && previous.Length > 0 && previous != ProgId)
        {
            fileType.SetValue(PreviousDefault, previous);
        }

        fileType.SetValue("", "Arquivo de recuperação");
        using (var icon = user.CreateSubKey(FileType + @"\DefaultIcon"))
        {
            icon.SetValue("", $"\"{appPath}\",0");
        }

        using (var command = user.CreateSubKey(OpenCommand))
        {
            command.SetValue("", Command);
        }

        using (var openWith = user.CreateSubKey(OpenWith))
        {
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        extension.SetValue("", ProgId);
    }

    /// <summary>Removes what <see cref="Register"/> wrote, giving .par2 files back to the app that had them before.</summary>
    public void Unregister()
    {
        string? previous;
        using (var fileType = user.OpenSubKey(FileType))
        {
            previous = fileType?.GetValue(PreviousDefault) as string;
        }

        using (var extension = user.OpenSubKey(Extension, writable: true))
        {
            if (extension is not null)
            {
                if (extension.GetValue("") as string == ProgId)
                {
                    if (previous is null)
                    {
                        extension.DeleteValue("");
                    }
                    else
                    {
                        extension.SetValue("", previous);
                    }
                }

                using (var openWith = extension.OpenSubKey("OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                }

                DeleteIfEmpty(extension, "OpenWithProgids");
            }
        }

        user.DeleteSubKeyTree(FileType, throwOnMissingSubKey: false);
        using var classes = user.OpenSubKey(Classes, writable: true);
        if (classes is not null)
        {
            DeleteIfEmpty(classes, ".par2");
        }
    }

    private static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        using (var key = parent.OpenSubKey(name))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
            {
                return;
            }
        }

        parent.DeleteSubKey(name);
    }
}
