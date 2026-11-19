using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

namespace GamingCenter.Application.Licensing;

/// <summary>
/// License checks used all over the app, not only at start: starting a session, a counter sale,
/// building a report, printing a receipt and a timer all ask here. Each check re-reads license.key
/// and verifies its signature again, so removing the activation window alone does not unlock the app.
/// This assembly is obfuscated in release builds (scripts/build-installer.ps1).
/// </summary>
public static class LicenseRuntime
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);
    private static readonly object Gate = new();
    private static string? _dataFolder;
    private static LicenseCheck? _last;
    private static DateTime _lastAt;

    public const string FileName = "license.key";

    /// <summary>
    /// Only the shipped app (mi2oGamingCenter.exe built in Release) is checked; tests, tools and
    /// debug runs are not. Read from the program itself, so there is no switch to turn off.
    /// </summary>
    public static bool Enforced { get; } = ComputeEnforced();

    /// <summary>The license in use, once a check has passed.</summary>
    public static LicenseInfo? Current => _last is { IsValid: true } c ? c.Info : null;

    public static string DataFolder => _dataFolder ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mi2oGamingCenter");

    public static string LicensePath => Path.Combine(DataFolder, FileName);

    public static void UseDataFolder(string folder)
    {
        lock (Gate) { _dataFolder = folder; _last = null; }
    }

    /// <summary>Checks the saved license (cached for a few minutes).</summary>
    public static LicenseCheck Check(bool fresh = false)
    {
        lock (Gate)
        {
            if (!fresh && _last is not null && DateTime.UtcNow - _lastAt < CacheFor) return _last;
            string? key = null;
            try { if (File.Exists(LicensePath)) key = File.ReadAllText(LicensePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _last = Verify(key);
            _lastAt = DateTime.UtcNow;
            return _last;
        }
    }

    /// <summary>Checks a key for this PC without saving it.</summary>
    public static LicenseCheck Verify(string? key)
    {
        var today = Today();
        var result = LicenseKey.Verify(key, LicensePublicKey.Value, MachineFingerprint.Code, today);
        // A key with an end date can't be stretched by moving the Windows clock back.
        if (result.IsValid && result.Info!.ExpiresOn is not null && ClockWasMovedBack(today))
            return new LicenseCheck(LicenseStatus.Expired, result.Info);
        return result;
    }

    /// <summary>Saves a key that is valid for this PC.</summary>
    public static LicenseCheck Activate(string key)
    {
        var result = Verify(key);
        if (!result.IsValid) return result;
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(LicensePath, key.Trim());
        lock (Gate) { _last = result; _lastAt = DateTime.UtcNow; }
        return result;
    }

    /// <summary>True when the app may run: always outside the shipped build, else a valid license.</summary>
    public static bool IsAllowed() => !Enforced || Check().IsValid;

    /// <summary>Throws when the shipped app runs without a valid license for this PC.</summary>
    public static void Require()
    {
        if (!IsAllowed())
            throw new InvalidOperationException("This copy of mi2o Gaming Center is not activated for this PC.");
    }

    private static bool ComputeEnforced()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null) return false;
        var name = entry.GetName().Name;
        var config = entry.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        return string.Equals(name, "mi2oGamingCenter", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(config, "Debug", StringComparison.OrdinalIgnoreCase);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    // ---- clock guard: remembers the latest date seen, in the data folder and in the registry ----

    private const string RegPath = @"Software\mi2o\GamingCenter";
    private const string RegValue = "s";
    private static string SeenFile => Path.Combine(DataFolder, ".seen");

    private static bool ClockWasMovedBack(DateOnly today)
    {
        var seen = Max(ReadSeenFile(), ReadSeenRegistry());
        if (seen is { } s && today < s.AddDays(-1)) return true;
        if (seen is null || today > seen) WriteSeen(today);
        return false;
    }

    private static DateOnly? Max(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    private static DateOnly? Parse(string? s) =>
        DateOnly.TryParseExact(s?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateOnly? ReadSeenFile()
    {
        try { return File.Exists(SeenFile) ? Parse(File.ReadAllText(SeenFile)) : null; }
        catch (Exception) { return null; }
    }

    private static DateOnly? ReadSeenRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { using var k = Registry.CurrentUser.OpenSubKey(RegPath); return Parse(k?.GetValue(RegValue) as string); }
        catch (Exception) { return null; }
    }

    private static void WriteSeen(DateOnly d)
    {
        var text = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        try { Directory.CreateDirectory(DataFolder); File.WriteAllText(SeenFile, text); } catch (Exception) { }
        if (!OperatingSystem.IsWindows()) return;
        try { using var k = Registry.CurrentUser.CreateSubKey(RegPath); k.SetValue(RegValue, text); } catch (Exception) { }
    }
}
