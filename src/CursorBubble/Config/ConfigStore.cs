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
                string json = File.ReadAllText(ConfigPath);
                AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
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
        string json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(ConfigPath, json);
    }
}
