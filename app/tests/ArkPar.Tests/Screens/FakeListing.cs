// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using ArkPar.Engine;

namespace ArkPar.Tests.Screens;

/// <summary>
/// Stands in for the engine's list of a set on disk. By default a set lists every file in its folder and below,
/// at its size now; a test can say which files a set covers, or that it cannot be read.
/// </summary>
internal sealed partial class FakeListing
{
    private readonly Dictionary<string, Func<SetListing>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _asked = [];

    /// <summary>The recovery files listed, in order.</summary>
    public IReadOnlyList<string> Asked => _asked;

    /// <summary>The set whose index is <paramref name="recoveryFile"/> covers <paramref name="files"/>, at their size now.</summary>
    public void Lists(string recoveryFile, params string[] files) =>
        _answers[SetOf(recoveryFile)] = () => new SetListing(
            Path.GetDirectoryName(recoveryFile)!,
            files.ToDictionary(file => file, file => new FileInfo(file).Length, StringComparer.OrdinalIgnoreCase));

    public void Fails(string recoveryFile, string output) =>
        _answers[SetOf(recoveryFile)] = () => throw new EngineListingException(1, output);

    /// <summary>Every list waits for it: a test holds the lists with a task it completes later.</summary>
    public Task Gate { get; set; } = Task.CompletedTask;

    public async Task<SetListing> ListAsync(string recoveryFile, CancellationToken stop)
    {
        _asked.Add(recoveryFile);
        await Gate.WaitAsync(stop);
        if (_answers.TryGetValue(SetOf(recoveryFile), out var answer))
        {
            return answer();
        }

        var folder = Path.GetDirectoryName(recoveryFile)!;
        return new SetListing(
            folder,
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(file => file, file => new FileInfo(file).Length, StringComparer.OrdinalIgnoreCase));
    }

    // "D:\Fotos\Viagem" for "D:\Fotos\Viagem.par2" and "D:\Fotos\Viagem.vol0+19.par2".
    private static string SetOf(string recoveryFile) => RecoveryFileEnd().Replace(recoveryFile, "");

    [GeneratedRegex(@"(\.vol\d+\+\d+)?\.par2$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecoveryFileEnd();
}
