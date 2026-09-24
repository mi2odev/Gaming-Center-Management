using GamingCenter.Application.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GamingCenter.Infrastructure.Services;

/// <summary>
/// Uses SQLite's online backup API, so backups are consistent even while the app is writing.
/// </summary>
public sealed class BackupService(IDataPaths paths, ISettingsService settings, IClock clock, ILogger<BackupService> logger) : IBackupService
{
    private const string Prefix = "gamingcenter-";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string DatabasePath => paths.DatabasePath;
    public string DefaultBackupFolder => paths.BackupsFolder;

    private string ResolveFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) ? folder
        : !string.IsNullOrWhiteSpace(settings.Current.BackupFolder) ? settings.Current.BackupFolder!
        : paths.BackupsFolder;

    public async Task<string> BackupNowAsync(string? folder = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var target = ResolveFolder(folder);
            Directory.CreateDirectory(target);
            var file = Path.Combine(target, $"{Prefix}{clock.Now:yyyyMMdd-HHmmss}.db");
            await Task.Run(() => Copy(paths.DatabasePath, file), ct);
            await RecordBackupAsync(ct);
            Prune(target);
            logger.LogInformation("Backup written to {File}", file);
            return file;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RestoreAsync(string backupFile, CancellationToken ct = default)
    {
        if (!File.Exists(backupFile)) throw new BusinessException("Backup file not found.");
        Validate(backupFile);

        await Gate.WaitAsync(ct);
        try
        {
            // Safety copy of the current data before it is replaced.
            var safetyFolder = Path.Combine(paths.BackupsFolder, "before-restore");
            Directory.CreateDirectory(safetyFolder);
            var safety = Path.Combine(safetyFolder, $"{Prefix}{clock.Now:yyyyMMdd-HHmmss}.db");
            await Task.Run(() =>
            {
                Copy(paths.DatabasePath, safety);
                SqliteConnection.ClearAllPools();
                Copy(backupFile, paths.DatabasePath);
                SqliteConnection.ClearAllPools();
            }, ct);
            logger.LogWarning("Database restored from {File}; previous data saved to {Safety}", backupFile, safety);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<string?> RunAutomaticBackupIfDueAsync(CancellationToken ct = default)
    {
        var cfg = settings.Current;
        if (!cfg.AutoBackupEnabled) return null;
        var now = clock.Now;
        var dueToday = now.Date.AddHours(cfg.AutoBackupHour);
        // Due once per day after the configured hour; if the laptop was off at that hour, runs at next start.
        if (now < dueToday || cfg.LastBackupAt >= dueToday) return null;
        return await BackupNowAsync(null, ct);
    }

    public IReadOnlyList<FileInfo> ListBackups(string? folder = null)
    {
        var dir = new DirectoryInfo(ResolveFolder(folder));
        if (!dir.Exists) return [];
        return dir.GetFiles($"{Prefix}*.db").OrderByDescending(f => f.LastWriteTime).ToList();
    }

    private async Task RecordBackupAsync(CancellationToken ct)
    {
        var updated = settings.Current.Clone();
        updated.LastBackupAt = clock.Now;
        if (settings is SettingsService s) await s.PersistAsync(updated, ct);
    }

    private void Prune(string folder)
    {
        int keep = Math.Max(1, settings.Current.BackupsToKeep);
        foreach (var old in ListBackups(folder).Skip(keep))
        {
            try { old.Delete(); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not delete old backup {File}", old.FullName); }
        }
    }

    private static void Copy(string sourceFile, string targetFile)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetFile, Pooling = false }.ToString());
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private static void Validate(string file)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Sessions','Stations','__EFMigrationsHistory')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) < 3)
                throw new BusinessException("This file is not a mi2o Gaming Center backup.");
        }
        catch (SqliteException)
        {
            throw new BusinessException("This file is not a valid database backup.");
        }
    }
}
