// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;

namespace ArkPar.Engine;

/// <summary>
/// Runs the engine as a plain child process, for the short tasks that read no keys (trial, list): unlike
/// <see cref="EngineRun"/>, no hidden console, since they cannot be paused, and cancelling one simply ends it.
/// </summary>
internal static class PlainEngineProcess
{
    /// <summary>Runs the engine to its end and returns its exit code and everything it printed (in UTF-8: pass <c>/uo</c>).</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string enginePath, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(enginePath)
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8, // /uo
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{enginePath}'.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The answer is no longer wanted: stop the engine, and wait so its working folder can go.
            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
