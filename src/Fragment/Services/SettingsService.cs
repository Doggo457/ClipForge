using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Fragment.Models;

namespace Fragment.Services;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> to a JSON file under
/// <c>%AppData%\Fragment\settings.json</c> using System.Text.Json.
/// </summary>
public static class SettingsService
{
    private const string AppFolderName = "Fragment";
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The directory holding Fragment configuration (created on demand).</summary>
    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>Full path to the settings JSON file: <c>%AppData%\Fragment\settings.json</c>.</summary>
    public static string SettingsPath => Path.Combine(SettingsFolder, SettingsFileName);

    /// <summary>
    /// Loads settings from disk. If the file is missing or invalid, returns a fresh
    /// <see cref="AppSettings"/> instance with sensible defaults (and persists it).
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            EnsureFolderExists();

            if (!File.Exists(SettingsPath))
            {
                var defaults = new AppSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings should never crash startup; fall back to defaults.
            return new AppSettings();
        }
    }

    /// <summary>Serializes <paramref name="settings"/> as indented JSON and writes it atomically.</summary>
    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        EnsureFolderExists();

        var json = JsonSerializer.Serialize(settings, SerializerOptions);

        // Write to a temp file first, then move into place to avoid partial/corrupt writes.
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(SettingsPath))
            File.Replace(tempPath, SettingsPath, destinationBackupFileName: null);
        else
            File.Move(tempPath, SettingsPath);
    }

    private static void EnsureFolderExists() => Directory.CreateDirectory(SettingsFolder);
}
