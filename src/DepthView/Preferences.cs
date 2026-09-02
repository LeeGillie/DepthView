using System;
using System.IO;
using System.Text.Json;

namespace DepthView;

/// <summary>
/// Settings that belong to the person rather than to any one file.
///
/// Deliberately not stored beside the executable the way materials.json is. Materials are
/// configuration - a shared machine can reasonably want one set for everybody, and a portable
/// copy should carry them along. Preferences are the opposite: they are one operator's habits,
/// and an executable in Program Files is not writable by the person whose habits they are.
///
/// Every read and write is best-effort. A preference that cannot be loaded falls back to the
/// built-in default, and one that cannot be saved is not worth interrupting anybody over; the
/// program still works, it just forgets.
/// </summary>
public sealed class Preferences
{
    /// <summary>
    /// Pass count the tuning dialog opens at, and the count every figure in it is quoted
    /// against.
    ///
    /// 256 because that is the number people actually run. It was briefly derived from the
    /// levels in the file, on the reasoning that a file's own ceiling is the honest answer to
    /// "how many passes can this use" - but the honest answer to a question nobody asked is
    /// still the wrong default. A 16-bit map would have opened at a four-figure pass count,
    /// and nobody cuts a coin at a thousand layers.
    /// </summary>
    public int DefaultPasses { get; set; } = 256;

    // ------------------------------------------------------------------ storage

    private const int MinPasses = 2;
    private const int MaxPasses = 65535;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DepthView", "preferences.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static Preferences? _current;

    public static Preferences Current => _current ??= Load();

    public static Preferences Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var loaded = JsonSerializer.Deserialize<Preferences>(
                    File.ReadAllText(DefaultPath), Json);
                if (loaded is not null)
                {
                    // Clamp on the way in rather than trusting the file. It is hand-editable by
                    // design, and a pass count of zero would divide by nothing several screens
                    // away from the typo that caused it.
                    loaded.DefaultPasses = Math.Clamp(loaded.DefaultPasses, MinPasses, MaxPasses);
                    return loaded;
                }
            }
        }
        catch
        {
            // Unreadable or corrupt: fall through to the defaults rather than refusing to start.
        }

        return new Preferences();
    }

    /// <summary>Writes the current preferences. Returns false if it could not, which callers
    /// are free to ignore - nothing here is worth failing a task over.</summary>
    public bool Save()
    {
        try
        {
            DefaultPasses = Math.Clamp(DefaultPasses, MinPasses, MaxPasses);

            var dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
