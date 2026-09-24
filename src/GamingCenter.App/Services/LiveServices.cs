using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GamingCenter.Application.Common;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace GamingCenter.App.Services;

/// <summary>
/// In-memory cache of running sessions, keyed by station. All screens read from it;
/// every mutation goes through <see cref="ISessionService"/> and the returned session is stored here.
/// </summary>
public sealed class LiveSessionStore(ISessionService sessions)
{
    private readonly Dictionary<int, GamingSession> _byStation = [];

    public event EventHandler? Changed;

    public IReadOnlyCollection<GamingSession> All => _byStation.Values;

    public GamingSession? ForStation(int stationId) => _byStation.GetValueOrDefault(stationId);

    public GamingSession? ById(int sessionId) => _byStation.Values.FirstOrDefault(s => s.Id == sessionId);

    public async Task RefreshAsync()
    {
        var live = await sessions.GetLiveSessionsAsync();
        _byStation.Clear();
        foreach (var s in live)
            if (s.StationId is { } id) _byStation[id] = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Upsert(GamingSession s)
    {
        if (s.StationId is not { } id) return;
        if (s.IsLive) _byStation[id] = s;
        else _byStation.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(int sessionId)
    {
        var entry = _byStation.FirstOrDefault(kv => kv.Value.Id == sessionId);
        if (entry.Value is not null) _byStation.Remove(entry.Key);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class NotificationItem(DateTime at, ToastKind kind, string title, string message) : ObservableObject
{
    public DateTime At { get; } = at;
    public ToastKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string TimeLabel => At.ToString("HH:mm");
}

/// <summary>
/// Watches live sessions each second and raises operator alerts once per event:
/// ending soon, time up (optionally auto-stopping play), long sessions and low stock.
/// </summary>
public sealed partial class NotificationCenter : ObservableObject
{
    private readonly LiveSessionStore _store;
    private readonly ISessionService _sessions;
    private readonly ISettingsService _settings;
    private readonly ToastService _toasts;
    private readonly ILogger<NotificationCenter> _logger;
    private readonly HashSet<string> _fired = [];
    private bool _stopping;

    public ObservableCollection<NotificationItem> Items { get; } = [];

    [ObservableProperty]
    private int _unread;

    public NotificationCenter(LiveSessionStore store, ISessionService sessions, ISettingsService settings, ToastService toasts, TickService ticks, ILogger<NotificationCenter> logger)
    {
        _store = store;
        _sessions = sessions;
        _settings = settings;
        _toasts = toasts;
        _logger = logger;
        ticks.Tick += OnTick;
    }

    public void Add(ToastKind kind, string title, string message, bool toast = true)
    {
        Items.Insert(0, new NotificationItem(DateTime.Now, kind, title, message));
        while (Items.Count > 100) Items.RemoveAt(Items.Count - 1);
        Unread++;
        if (toast) _toasts.Show(kind, title, message, kind == ToastKind.Warning ? 10 : 6);
    }

    public void MarkAllRead() => Unread = 0;

    /// <summary>Called after a product is sold; warns once per product until restocked.</summary>
    public void CheckStock(string productName, int stock, int minStock)
    {
        if (!_settings.Current.LowStockNotifications) return;
        var key = $"stock:{productName}";
        if (stock > minStock) { _fired.Remove(key); return; }
        if (!_fired.Add(key)) return;
        Add(ToastKind.Warning, "Low stock", stock <= 0 ? $"{productName} is out of stock." : $"{productName}: {stock} remaining.");
    }

    private async void OnTick(object? sender, DateTime now)
    {
        if (_store.All.Count == 0) return;
        var cfg = _settings.Current;
        foreach (var s in _store.All.ToList())
        {
            if (s.Status is not (SessionStatus.Running or SessionStatus.Paused)) continue;

            if (s.RemainingTime(now) is { } left)
            {
                if (cfg.WarnBeforeEndMinutes > 0 && left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(cfg.WarnBeforeEndMinutes)
                    && _fired.Add($"warn:{s.Id}:{s.AllowedTime}"))
                {
                    Add(ToastKind.Warning, "Ending soon", $"{s.StationName} session ending in {Math.Ceiling(left.TotalMinutes)} minutes.");
                }
                if (left <= TimeSpan.Zero && _fired.Add($"up:{s.Id}:{s.AllowedTime}"))
                {
                    Add(ToastKind.Warning, "Time is up", s.Mode == SessionMode.FixedBudget
                        ? $"{s.StationName}: budget of {Money.Format(s.Budget ?? 0)} used up."
                        : $"{s.StationName}: {Durations.Minutes(s.PlannedMinutes ?? 0)} finished.");
                    if (cfg.AutoEndWhenTimeExpires) await AutoStopAsync(s);
                }
            }

            if (cfg.LongSessionAlertHours > 0 && s.PlayedTime(now) >= TimeSpan.FromHours(cfg.LongSessionAlertHours)
                && _fired.Add($"long:{s.Id}"))
            {
                Add(ToastKind.Info, "Long session", $"{s.StationName} has been running for {Durations.Short(s.PlayedTime(now))}.");
            }
        }
    }

    private async Task AutoStopAsync(GamingSession s)
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            var end = s.StartTime + (s.AllowedTime ?? TimeSpan.Zero) + s.PausedTime(DateTime.Now);
            if (end > DateTime.Now) end = DateTime.Now;
            _store.Upsert(await _sessions.StopPlayAsync(s.Id, end));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-stop failed for session {Id}", s.Id);
        }
        finally
        {
            _stopping = false;
        }
    }
}

/// <summary>Periodic background jobs: automatic daily backup.</summary>
public sealed class MaintenanceService(IBackupService backup, NotificationCenter notifications, ILogger<MaintenanceService> logger)
{
    private System.Windows.Threading.DispatcherTimer? _timer;

    public void Start()
    {
        _ = RunAsync();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _timer.Tick += async (_, _) => await RunAsync();
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private async Task RunAsync()
    {
        try
        {
            var file = await backup.RunAutomaticBackupIfDueAsync();
            if (file is not null)
                notifications.Add(ToastKind.Success, "Backup completed", System.IO.Path.GetFileName(file), toast: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic backup failed");
            notifications.Add(ToastKind.Error, "Automatic backup failed", ErrorText.For(ex));
        }
    }
}
