// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Engine;

/// <summary>
/// Lists the files a recovery set protects, with the sizes it recorded, without reading them: the engine's
/// list (<c>l</c>) reads only the set's own files, so it takes a fraction of a second.
/// </summary>
public static class EngineListing
{
    /// <param name="recoveryFile">The set's index; when it is gone, the engine finds the set through its other recovery files.</param>
    /// <param name="enginePath">The engine to run; null for the one shipped with the app.</param>
    /// <exception cref="EngineListingException">The engine could not read the set (no recovery file left, all damaged...).</exception>
    public static async Task<SetListing> RunAsync(string recoveryFile, string? enginePath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        enginePath ??= EngineRun.DefaultEnginePath;
        EngineRun.EnsureEngine(enginePath);

        // The list writes nothing, so it needs no run folder of its own.
        var (exitCode, output) = await PlainEngineProcess.RunAsync(
                enginePath, EngineArguments.For(new EngineCommand.List(recoveryFile), runDirectory: ""), Path.GetTempPath(), cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new EngineListingException(exitCode, output);
        }

        try
        {
            return SetListing.Parse(output);
        }
        catch (FormatException failure)
        {
            throw new EngineListingException(exitCode, output, failure);
        }
    }
}

/// <summary>The engine could not list a set; <see cref="Output"/> says why, in its words.</summary>
public sealed class EngineListingException(int exitCode, string output, Exception? innerException = null)
    : Exception($"The engine's list ended with exit code {exitCode}.", innerException)
{
    /// <summary>Everything the engine printed.</summary>
    public string Output { get; } = output;
}
