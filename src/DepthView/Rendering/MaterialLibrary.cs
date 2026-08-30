using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DepthView.Rendering;

/// <summary>
/// The material list is data, not code, so a texture you point at once stays pointed at.
/// If materials.json sits next to the executable it replaces the built-in set entirely;
/// delete it to fall back to the built-ins.
/// </summary>
public static class MaterialLibrary
{
    private static List<MaterialPreset>? _presets;

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "materials.json");

    public static string? LoadError { get; private set; }

    public static List<MaterialPreset> Presets => _presets ??= Load();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static List<MaterialPreset> Load(string? path = null)
    {
        path ??= DefaultPath;
        LoadError = null;

        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<MaterialPreset>>(text, Json);
                if (list is { Count: > 0 })
                {
                    _presets = list;
                    return list;
                }
                LoadError = $"{Path.GetFileName(path)} contained no materials; using built-ins.";
            }
        }
        catch (Exception ex)
        {
            LoadError = $"Could not read {Path.GetFileName(path)}: {ex.Message}. Using built-ins.";
        }

        _presets = MaterialPreset.Builtins();
        return _presets;
    }

    public static void Save(IEnumerable<MaterialPreset> presets, string? path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(presets, Json));
    }

    public static void ResetToBuiltins()
    {
        _presets = MaterialPreset.Builtins();
        LoadError = null;
    }
}
