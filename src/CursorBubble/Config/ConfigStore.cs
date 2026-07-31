using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CursorBubble.Config;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON in
/// <c>%APPDATA%\CursorBubble\config.json</c>.
/// </summary>
public static class ConfigStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorBubble");

    public static string ConfigPath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Load the config, or create (and persist) a default one on first run or
    /// if the file is missing/corrupt.
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                AppConfig? cfg = Deserialize(File.ReadAllText(ConfigPath));
                if (cfg is not null)
                    return cfg;
            }
        }
        catch
        {
            // Fall through to defaults on any read/parse error.
        }

        AppConfig fresh = AppConfig.CreateDefault();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigPath, Serialize(config));
    }

    /// <summary>
    /// Internal for tests. Unknown properties are ignored by design, so a config
    /// written by an older build (with settings that have since been removed)
    /// still loads instead of resetting the user back to defaults.
    /// </summary>
    internal static AppConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, Options);

    internal static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config, Options);
}
