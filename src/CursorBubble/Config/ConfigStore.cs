using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CursorBubble.Storage;

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
    /// Set by <see cref="Load"/> when it had to fall back to defaults, describing
    /// what happened and where the unreadable file was moved to. Null on a normal
    /// load. The app surfaces it once at startup — silently replacing someone's
    /// settings and saying nothing is how a corrupt file turns into "the app
    /// forgot everything and nobody knows why".
    /// </summary>
    public static string? LastLoadFailure { get; private set; }

    /// <summary>
    /// Load the config, or create (and persist) a default one on first run or
    /// if the file is missing/corrupt.
    /// </summary>
    public static AppConfig Load()
    {
        LastLoadFailure = null;

        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg is not null)
                    return cfg;

                Quarantine("het bestand bevat geen instellingen");
            }
        }
        catch (Exception ex)
        {
            Quarantine(ex.Message);
        }

        AppConfig fresh = AppConfig.CreateDefault();
        Save(fresh);
        return fresh;
    }

    /// <summary>
    /// Move a config we could not read aside instead of overwriting it.
    ///
    /// <see cref="Load"/> falls back to defaults and immediately saves them, so
    /// without this step the write lands on top of the file that failed to
    /// parse — and every segment, colour and layout value the user had is gone
    /// with no copy anywhere. A file that survives can be repaired by hand; one
    /// that has been overwritten cannot.
    /// </summary>
    private static void Quarantine(string reason)
    {
        string kept = Path.Combine(Dir, $"config.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Move(ConfigPath, kept, overwrite: true);
            LastLoadFailure =
                $"Je instellingen konden niet gelezen worden ({reason}). " +
                $"CursorBubble is opnieuw begonnen met de standaardinstellingen. " +
                $"Het oude bestand is bewaard als {kept}.";
        }
        catch (Exception ex)
        {
            // Could not even move it — say so rather than pretend, and let the
            // caller overwrite. Losing the file is bad; starting with no working
            // config at all is worse.
            LastLoadFailure =
                $"Je instellingen konden niet gelezen worden ({reason}) en het oude bestand " +
                $"kon niet bewaard worden ({ex.Message}). CursorBubble gebruikt de " +
                $"standaardinstellingen.";
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(config, Options);
        AtomicFile.WriteAllText(ConfigPath, json);
    }
}
