// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;

namespace ArkPar.Engine;

/// <summary>
/// Works out what a create would make, without making it: the engine's trial (<c>t</c>) reads only the
/// files' sizes, so it takes a fraction of a second even for thousands of photos.
/// </summary>
/// <remarks>
/// It runs as a plain child process, not in a hidden console like <see cref="EngineRun"/>: a trial reads no
/// keys, since it cannot be paused, and cancelling it simply ends it.
/// </remarks>
public static class EngineTrial
{
    /// <summary>The recovery files <paramref name="create"/> would make, with the very same settings.</summary>
    /// <param name="enginePath">The engine to run; null for the one shipped with the app.</param>
    /// <exception cref="EngineTrialException">The engine could not work them out (a file is gone, a setting is out of range...).</exception>
    public static async Task<RecoveryEstimate> RunAsync(EngineCommand.Create create, string? enginePath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        enginePath ??= EngineRun.DefaultEnginePath;
        EngineRun.EnsureEngine(enginePath);

        var runDirectory = RunDirectory.Create();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(runDirectory, EngineArguments.SourceListName), EngineArguments.SourceList(create), cancellationToken)
                .ConfigureAwait(false);
            var (exitCode, output) = await RunEngineAsync(enginePath, EngineArguments.ForTrial(create, runDirectory), runDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new EngineTrialException(exitCode, output);
            }

            try
            {
                return RecoveryEstimate.Parse(output);
            }
            catch (FormatException failure)
            {
                throw new EngineTrialException(exitCode, output, failure);
            }
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunEngineAsync(
        string enginePath, IReadOnlyList<string> arguments, string runDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(enginePath)
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8, // /uo
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = runDirectory,
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
            // The answer is no longer wanted (the settings changed): stop the engine, and wait so the run folder can go.
            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>The engine could not work out the recovery files; <see cref="Output"/> says why, in its words.</summary>
public sealed class EngineTrialException(int exitCode, string output, Exception? innerException = null)
    : Exception($"The engine's trial ended with exit code {exitCode}.", innerException)
{
    /// <summary>Everything the engine printed.</summary>
    public string Output { get; } = output;
}
