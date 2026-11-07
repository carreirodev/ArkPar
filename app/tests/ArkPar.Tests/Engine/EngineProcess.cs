// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace ArkPar.Tests.Engine;

/// <summary>Runs the engine built by app/build-engine.ps1 and captures what it prints.</summary>
internal static class EngineProcess
{
    // Exit code bits documented in source/par2j/Command_par2j.txt.
    public const int FatalError = 1;
    public const int InputFilesIncomplete = 4;

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "engine", "par2j64.exe");

    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        if (!File.Exists(ExePath))
        {
            throw new FileNotFoundException(
                "The engine was not found next to the tests. Build it with 'pwsh -File app/build-engine.ps1', then run the tests again.",
                ExePath);
        }

        var startInfo = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{ExePath}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new Result(process.ExitCode, await stdout + await stderr);
    }
}
