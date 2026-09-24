using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace GamingCenter.App.ViewModels;

public sealed record SessionDrawerArgs(int SessionId, bool FocusCatalog);

public sealed record PauseRow(string Range, string Duration);

public sealed record SessionLineRow(int Id, string Name, int Quantity, string Total);

/// <summary>Active session drawer (design 1f): live timer, pause history, items and product catalog.</summary>
public sealed partial class SessionDrawerViewModel : DialogViewModel
{
    private readonly int _sessionId;
    private readonly LiveSessionStore _store;
    private readonly ISessionService _sessions;
    private readonly TickService _ticks;
    private readonly DialogService _dialogs;
    private readonly NotificationCenter _notifications;
    private readonly IServiceProvider _services;
    private readonly ToastService _toasts;

    public SessionDrawerViewModel(SessionDrawerArgs args, LiveSessionStore store, ISessionService sessions, IProductService products,
        TickService ticks, DialogService dialogs, NotificationCenter notifications, IServiceProvider services, ToastService toasts)
    {
        _sessionId = args.SessionId;
        FocusCatalog = args.FocusCatalog;
        _store = store;
        _sessions = sessions;
        _ticks = ticks;
        _dialogs = dialogs;
        _notifications = notifications;
        _services = services;
        _toasts = toasts;
        Catalog = new ProductCatalogViewModel(products);
        Catalog.Picked += async (_, tile) => await AddProductAsync(tile);

        _session = store.ById(_sessionId)!;
        _store.Changed += OnStoreChanged;
        _ticks.Tick += OnTick;
        Rebuild();
        _ = LoadCatalogAsync();
    }

    public override DialogPlacement Placement => DialogPlacement.Right;
    public bool FocusCatalog { get; }
    public ProductCatalogViewModel Catalog { get; }

    [ObservableProperty] private GamingSession _session;

    public ObservableCollection<PauseRow> Pauses { get; } = [];
    public ObservableCollection<SessionLineRow> Lines { get; } = [];

    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _statusBrush = "Status.Occupied";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _timerLabel = "Play time";
    [ObservableProperty] private string _timerText = "";
    [ObservableProperty] private string _timerBrush = "Text";
    [ObservableProperty] private string _gamingText = "";
    [ObservableProperty] private string _gamingLabel = "Gaming cost so far";
    [ObservableProperty] private string _consumptionText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _pauseSummary = "";
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _modeDetail = "";

    public string StationName => Session.StationName;
    public bool IsPaused => Session.Status == SessionStatus.Paused;
    public bool IsStopped => Session.Status == SessionStatus.AwaitingPayment;
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    protected override void OnClosed()
    {
        _store.Changed -= OnStoreChanged;
        _ticks.Tick -= OnTick;
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            await Catalog.LoadAsync();
            SyncQuantities();
        }
        catch (Exception ex) { Error = ErrorText.For(ex); }
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        var s = _store.ById(_sessionId);
        if (s is null) { Close(); return; }
        Session = s;
        Rebuild();
    }

    private void OnTick(object? sender, DateTime now) => RefreshClock(now);

    private void Rebuild()
    {
        var s = Session;
        OnPropertyChanged(nameof(StationName));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(PauseButtonText));

        var mode = s.Mode switch
        {
            SessionMode.FixedDuration => "FIXED DURATION",
            SessionMode.FixedBudget => "FIXED BUDGET",
            _ => "OPEN SESSION",
        };
        var state = s.Status switch
        {
            SessionStatus.Paused => "PAUSED",
            SessionStatus.AwaitingPayment => "TIME UP · AWAITING PAYMENT",
            _ => "OCCUPIED",
        };
        StatusLine = $"{state} · {mode}";
        StatusBrush = s.Status switch
        {
            SessionStatus.Paused => "Status.Paused",
            SessionStatus.AwaitingPayment => "Status.TimeUp",
            _ => "Status.Occupied",
        };
        Subtitle = $"{s.Customer?.Name ?? "Walk-in"} · started {s.StartTime:HH:mm} · {Money.Rate(s.HourlyRate)} locked";
        ModeDetail = s.Mode switch
        {
            SessionMode.FixedDuration => $"Purchased {Durations.Minutes(s.PlannedMinutes ?? 0)}",
            SessionMode.FixedBudget => $"Budget {Money.Format(s.Budget ?? 0)} · max {Durations.Clock(s.AllowedTime ?? TimeSpan.Zero)}",
            _ => $"Billed {s.Rules.UnitLabel}",
        };

        Lines.Clear();
        foreach (var l in s.Products.OrderBy(p => p.AddedAt))
            Lines.Add(new SessionLineRow(l.Id, l.ProductName, l.Quantity, Money.Format(l.LineTotal)));
        SyncQuantities();
        RefreshClock(DateTime.Now);
    }

    private void SyncQuantities() =>
        Catalog.SetQuantities(Session.Products.GroupBy(p => p.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity)));

    private void RefreshClock(DateTime now)
    {
        var s = Session;
        var remaining = s.RemainingTime(now);
        bool timeUp = s.IsTimeUp(now) || s.Status == SessionStatus.AwaitingPayment;
        if (remaining is { } r && !timeUp)
        {
            TimerLabel = "Remaining";
            TimerText = Durations.Clock(r);
            TimerBrush = s.Status == SessionStatus.Paused ? "Info" : r <= TimeSpan.FromMinutes(10) ? "Warning" : "Text";
        }
        else
        {
            TimerLabel = timeUp && s.AllowedTime is not null ? $"Time up · overtime {Durations.Clock(s.Overtime(now))}" : "Play time";
            TimerText = Durations.Clock(s.PlayedTime(now));
            TimerBrush = timeUp ? "Danger" : s.Status == SessionStatus.Paused ? "Info" : "Text";
        }
        HasProgress = s.AllowedTime is not null;
        Progress = s.Progress(now);
        GamingLabel = s.Mode switch
        {
            SessionMode.FixedDuration => "Gaming (purchased time)",
            SessionMode.FixedBudget => "Gaming used of budget",
            _ => "Gaming cost so far",
        };
        GamingText = Money.Format(s.GamingCost(now));
        ConsumptionText = Money.Format(s.ProductsCost());
        TotalText = Money.Format(s.TotalCost(now));

        if (Pauses.Count != s.Pauses.Count || s.OpenPause is not null)
        {
            Pauses.Clear();
            foreach (var p in s.Pauses.OrderBy(p => p.StartTime))
                Pauses.Add(new PauseRow($"{p.StartTime:HH:mm} → {(p.EndTime is { } e ? e.ToString("HH:mm") : "now")}", Durations.Long(p.DurationAt(s.ClockAt(now)))));
        }
        PauseSummary = $"Wall clock {Durations.Clock(s.WallTime(now))} · paused {Durations.Short(s.PausedTime(now))} · play {Durations.Clock(s.PlayedTime(now))}";
    }

    private async Task AddProductAsync(ProductTileViewModel tile)
    {
        await RunAsync(async () =>
        {
            var updated = await _sessions.AddProductAsync(_sessionId, tile.Id);
            _store.Upsert(updated);
            await Catalog.LoadAsync();
            SyncQuantities();
            var p = Catalog.Find(tile.Id)?.Product;
            if (p is not null) _notifications.CheckStock(p.Name, p.Stock, p.MinStock);
        });
    }

    [RelayCommand]
    private Task Increase(SessionLineRow row) => ChangeQuantityAsync(row, +1);

    [RelayCommand]
    private Task Decrease(SessionLineRow row) => ChangeQuantityAsync(row, -1);

    private async Task ChangeQuantityAsync(SessionLineRow row, int delta)
    {
        await RunAsync(async () =>
        {
            _store.Upsert(await _sessions.SetProductQuantityAsync(row.Id, Math.Max(0, row.Quantity + delta)));
            await Catalog.LoadAsync();
            SyncQuantities();
        });
    }

    [RelayCommand]
    private async Task TogglePause() =>
        await RunAsync(async () =>
            _store.Upsert(IsPaused ? await _sessions.ResumeAsync(_sessionId) : await _sessions.PauseAsync(_sessionId)));

    [RelayCommand]
    private async Task Extend()
    {
        var dlg = ActivatorUtilities.CreateInstance<ExtendSessionViewModel>(_services, Session);
        if (await _dialogs.ShowAsync<GamingSession>(dlg) is { } updated)
        {
            _store.Upsert(updated);
            _toasts.Success("Session updated", updated.StationName);
        }
    }

    [RelayCommand]
    private async Task EndAndBill()
    {
        var paid = await _services.GetRequiredService<SessionWorkflow>().BillAsync(_sessionId);
        if (paid) Close(true);
    }

    [RelayCommand]
    private async Task CancelSession()
    {
        var reason = await _dialogs.PromptAsync("Cancel session",
            "Reason", null, "Cancel session", "e.g. started by mistake");
        if (reason is null) return;
        if (await RunAsync(() => _sessions.CancelAsync(_sessionId, reason)))
        {
            _store.Remove(_sessionId);
            _toasts.Info($"{StationName} session cancelled", "Products were returned to stock. Nothing was charged.");
            Close();
        }
    }
}

/// <summary>Add time, add money or change mode of a running session.</summary>
public sealed partial class ExtendSessionViewModel : DialogViewModel
{
    private readonly ISessionService _sessions;
    private readonly GamingSession _session;

    public ExtendSessionViewModel(GamingSession session, ISessionService sessions)
    {
        _session = session;
        _sessions = sessions;
        Tab = session.Mode switch
        {
            SessionMode.FixedDuration => "Time",
            SessionMode.FixedBudget => "Money",
            _ => "Mode",
        };
        _newMode = session.Mode == SessionMode.Open ? SessionMode.FixedDuration : SessionMode.Open;
        _modeMinutes = ((int)Math.Ceiling(session.PlayedTime(DateTime.Now).TotalMinutes / 30.0) * 30 + 30).ToString();
        _modeBudget = "500";
    }

    public string Title => $"Extend {_session.StationName}";
    public string CurrentMode => _session.Mode switch
    {
        SessionMode.FixedDuration => $"Fixed duration · {Durations.Minutes(_session.PlannedMinutes ?? 0)} purchased",
        SessionMode.FixedBudget => $"Fixed budget · {Money.Format(_session.Budget ?? 0)}",
        _ => "Open session · pays actual time",
    };
    public bool CanAddTime => _session.Mode == SessionMode.FixedDuration;
    public bool CanAddMoney => _session.Mode == SessionMode.FixedBudget;
    public string PlayedText => $"Played so far {Durations.Clock(_session.PlayedTime(DateTime.Now))}";

    [ObservableProperty] private string _tab;
    [ObservableProperty] private string _minutesText = "30";
    [ObservableProperty] private string _amountText = "200";
    [ObservableProperty] private SessionMode _newMode;
    [ObservableProperty] private string _modeMinutes;
    [ObservableProperty] private string _modeBudget;

    public IReadOnlyList<Preset> MinutePresets { get; } = new[] { 15, 30, 60, 120 }.Select(m => new Preset(m, "+" + Durations.Minutes(m))).ToList();
    public IReadOnlyList<Preset> AmountPresets { get; } = new[] { 100, 200, 500, 1000 }.Select(a => new Preset(a, "+" + a)).ToList();

    [RelayCommand] private void SetMinutes(int m) => MinutesText = m.ToString();
    [RelayCommand] private void SetAmount(int a) => AmountText = a.ToString();

    [RelayCommand]
    private async Task Apply()
    {
        GamingSession? updated = null;
        bool ok = await RunAsync(async () =>
        {
            switch (Tab)
            {
                case "Time":
                    if (!int.TryParse(MinutesText, out var m) || m <= 0) throw new BusinessException("Enter minutes to add.");
                    updated = await _sessions.ExtendTimeAsync(_session.Id, m);
                    break;
                case "Money":
                    if (!Money.TryParse(AmountText, out var a) || a <= 0) throw new BusinessException("Enter an amount to add.");
                    updated = await _sessions.AddBudgetAsync(_session.Id, a);
                    break;
                default:
                    int? minutes = null;
                    decimal? budget = null;
                    if (NewMode == SessionMode.FixedDuration)
                    {
                        if (!int.TryParse(ModeMinutes, out var mm) || mm <= 0) throw new BusinessException("Enter the total duration in minutes.");
                        minutes = mm;
                    }
                    else if (NewMode == SessionMode.FixedBudget)
                    {
                        if (!Money.TryParse(ModeBudget, out var b) || b <= 0) throw new BusinessException("Enter the total budget.");
                        budget = b;
                    }
                    updated = await _sessions.ChangeModeAsync(_session.Id, NewMode, minutes, budget);
                    break;
            }
        });
        if (ok) Close(updated);
    }
}
