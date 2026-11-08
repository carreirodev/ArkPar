// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;

namespace ArkPar.Screens;

/// <summary>
/// Held by the ArkPar window making a set, so that another window making the same set at the same time stops
/// instead of taking the first one's new set for a leftover (WPC-415). A file of the user's temporary folder,
/// never of the set's, open for one window alone: Windows lets go of it and deletes it when the window is done
/// with it, or when the app ends, even ended hard.
/// </summary>
internal sealed class SetLock : IDisposable
{
    // ERROR_SHARING_VIOLATION: another window has it open.
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly FileStream _file;

    private SetLock(FileStream file) => _file = file;

    /// <summary>Takes the lock of the set whose index is <paramref name="recoveryFile"/>.</summary>
    /// <returns>Null while another window holds it.</returns>
    /// <exception cref="IOException">The temporary folder cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The temporary folder cannot be written.</exception>
    public static SetLock? TryTake(string recoveryFile)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ArkPar", "locks");
        Directory.CreateDirectory(folder);

        // One name per set, whatever the case its path was typed in: Windows' names ignore case.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(recoveryFile).ToUpperInvariant())));
        var path = Path.Combine(folder, key + ".lock");
        const int Attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new SetLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose));
            }
            catch (IOException failure) when (failure.HResult == SharingViolation)
            {
                return null;
            }
            catch (UnauthorizedAccessException) when (attempt < Attempts)
            {
                // Let go of by the other window a moment ago, but not yet deleted (a program scanning it holds it
                // still): "access denied" until it is gone.
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
