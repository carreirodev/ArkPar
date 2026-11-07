// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using ArkPar.Engine;

namespace ArkPar.Screens;

/// <summary>What the Settings screen keeps between sessions.</summary>
/// <param name="UseGpu">Calculate on the GPU when there is one.</param>
/// <param name="Threads">Processor threads, from 1 to <see cref="EngineProcessing.MaxThreads"/>; null for automatic.</param>
/// <param name="OpenPar2Files">Whether a double click on a .par2 opens ArkPar; null until the user has been asked.</param>
public sealed record AppSettings(bool UseGpu = true, int? Threads = null, bool? OpenPar2Files = null)
{
    /// <summary>What the engine may use, for every task started from now on.</summary>
    [JsonIgnore]
    public EngineProcessing Processing => new(UseGpu, Threads);
}

/// <summary>
/// The settings, as a JSON file in the user's local app data. The app is not packaged, so Windows keeps no
/// settings store for it (ADR-0002).
/// </summary>
public sealed class SettingsFile(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary><c>%LOCALAPPDATA%\ArkPar\settings.json</c>.</summary>
    public static string DefaultPath { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkPar", "settings.json");

    private readonly string _folder = System.IO.Path.GetDirectoryName(path) ?? throw new ArgumentException("The settings file needs a folder.", nameof(path));

    public string Path { get; } = path;

    /// <summary>The saved settings, or the defaults when none were saved yet.</summary>
    /// <exception cref="InvalidDataException">The file is there but cannot be read as settings, or cannot be read at all.</exception>
    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(Path), Json)
                ?? throw new JsonException("The file holds null.");

            // Out of range, the engine would refuse every task.
            if (settings.Threads is < 1 or > EngineProcessing.MaxThreads)
            {
                throw new JsonException($"{settings.Threads} threads is out of range.");
            }

            return settings;
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The settings file {Path} cannot be read.", failure);
        }
    }

    /// <summary>Saves <paramref name="settings"/> whole, so a failure halfway leaves the earlier file as it was.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_folder);
        var draft = Path + ".new";
        File.WriteAllBytes(draft, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
        File.Move(draft, Path, overwrite: true);
    }
}
