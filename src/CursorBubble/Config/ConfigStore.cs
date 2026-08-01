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
                AppConfig? cfg = Deserialize(File.ReadAllText(ConfigPath));
                if (cfg is not null)
                    return cfg;

                Quarantine("the file contained no settings");
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
                $"Your settings could not be read ({reason}). CursorBubble has started " +
                $"from the defaults. The old file has been kept as {kept}.";
        }
        catch (Exception ex)
        {
            // Could not even move it — say so rather than pretend, and let the
            // caller overwrite. Losing the file is bad; starting with no working
            // config at all is worse.
            LastLoadFailure =
                $"Your settings could not be read ({reason}), and the old file could not " +
                $"be kept either ({ex.Message}). CursorBubble is using the defaults.";
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        AtomicFile.WriteAllText(ConfigPath, Serialize(config));
    }

    /// <summary>
    /// Internal for tests. Unknown properties are ignored by design, so a config
    /// written by an older build (with settings that have since been removed)
    /// still loads instead of resetting the user back to defaults.
    /// </summary>
    internal static AppConfig? Deserialize(string json)
    {
        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json, Options);
        if (config is null)
            return null;

        Migrate(config);
        return config;
    }

    /// <summary>
    /// Bring a config written by an older build up to the current layout.
    ///
    /// Empty today, because every change so far has been additive and the
    /// serializer already handles that: a property the file does not mention
    /// keeps its default. It exists so the first change that <em>cannot</em> be
    /// expressed that way — a rename, a restructure — has an obvious place to
    /// go, rather than being discovered when somebody's settings reset
    /// themselves.
    ///
    /// Migrations are written as a chain: handle version 1 → 2, then 2 → 3, so
    /// a file from any age arrives at the current one.
    /// </summary>
    private static void Migrate(AppConfig config)
    {
        // Nothing to do yet. A file written before SchemaVersion existed
        // deserialises to the property's default, which is 1 — the version those
        // files in fact are — so they need no special case either.
        //
        // The first step goes here, written as a chain (1 → 2, then 2 → 3) so a
        // file of any age arrives at the current layout.
        //
        // A file from a *newer* build is left alone on purpose: its unknown
        // properties are already ignored, and stamping the version down would
        // discard what that build knows.
        if (config.SchemaVersion < AppConfig.CurrentSchemaVersion)
            config.SchemaVersion = AppConfig.CurrentSchemaVersion;
    }

    internal static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config, Options);
}
