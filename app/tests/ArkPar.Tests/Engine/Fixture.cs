// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Engine;

namespace ArkPar.Tests.Engine;

/// <summary>Real engine output captured under Engine/Fixtures (see the README there).</summary>
internal static class Fixture
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Engine", "Fixtures", name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

    public static string Read(string name) => File.ReadAllText(PathOf(name));
}

/// <summary>Collects progress reports synchronously, in order.</summary>
internal sealed class RecordedProgress : IProgress<EngineProgress>
{
    private readonly List<EngineProgress> _reports = [];

    public IReadOnlyList<EngineProgress> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(EngineProgress value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }
    }
}
