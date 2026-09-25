using System.IO;
using System.Text.Json;

namespace GamingCenter.App.Services;

/// <summary>
/// Remembers how each screen was left (sort, filters, grid/list, periods) in ui-state.json in the data folder,
/// so the app opens the same way next time. Failures are ignored: it is a convenience, never a blocker.
/// </summary>
public static class UiState
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _values = [];
    private static string? _path;

    public static void Init(string dataFolder)
    {
        _path = Path.Combine(dataFolder, "ui-state.json");
        try
        {
            if (File.Exists(_path))
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception) { _values = []; }
    }

    public static string Get(string key, string fallback)
    {
        lock (Gate) return _values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
    }

    public static bool GetBool(string key, bool fallback) => bool.TryParse(Get(key, ""), out var b) ? b : fallback;

    public static void Set(string key, object? value)
    {
        var text = value?.ToString() ?? "";
        lock (Gate)
        {
            if (_values.TryGetValue(key, out var old) && old == text) return;
            _values[key] = text;
            if (_path is null) return;
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception) { /* read-only folder etc.: keep working with memory only */ }
        }
    }
}
