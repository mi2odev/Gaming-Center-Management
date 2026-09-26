using GamingCenter.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel, IRecipient<DataChangedMessage>
{
    private readonly IStationService _stations;
    private readonly IReportService _reports;
    private readonly ISettingsService _settings;
    private readonly LiveSessionStore _store;
    private readonly TickService _ticks;
    private readonly SessionWorkflow _workflow;
    private readonly DialogService _dialogs;
    private readonly ShellNavigator _nav;
    private readonly List<StationCardViewModel> _all = [];
    private DateTime _lastStats;

    public DashboardViewModel(IStationService stations, IReportService reports, ISettingsService settings, LiveSessionStore store,
        TickService ticks, SessionWorkflow workflow, DialogService dialogs, ShellNavigator nav, ToastService toasts) : base(toasts)
    {
        _stations = stations;
        _reports = reports;
        _settings = settings;
        _store = store;
        _ticks = ticks;
        _workflow = workflow;
        _dialogs = dialogs;
        _nav = nav;
    }

    public override string Title => L.T("Dashboard");

    public ObservableCollection<StationCardViewModel> Cards { get; } = [];

    [ObservableProperty] private string _filter = UiState.Get("Dashboard.Filter", "All");
    [ObservableProperty] private string _sort = UiState.Get("Dashboard.Sort", "Room");
    [ObservableProperty] private string _viewMode = UiState.Get("Dashboard.View", "Grid");

    [ObservableProperty] private int _countAll;
    [ObservableProperty] private int _countAvailable;
    [ObservableProperty] private int _countOccupied;
    [ObservableProperty] private int _countReserved;
    [ObservableProperty] private int _countOffline;

    [ObservableProperty] private DashboardStats? _stats;
    [ObservableProperty] private string _revenueText = "0";
    [ObservableProperty] private string _revenueTrend = "";
    [ObservableProperty] private bool _revenueTrendUp = true;
    [ObservableProperty] private string _gamingText = "0";
    [ObservableProperty] private string _gamingSub = "";
    [ObservableProperty] private string _revenueNote = "";
    [ObservableProperty] private string _productsText = "0";
    [ObservableProperty] private string _productsSub = "";
    [ObservableProperty] private string _activeSub = "";
    [ObservableProperty] private string _mostUsed = "—";
    [ObservableProperty] private string _mostUsedSub = "";
    [ObservableProperty] private int _lowStockCount;
    [ObservableProperty] private string _lowStockSub = "";
    [ObservableProperty] private bool _isEmpty;

    public string Currency => Money.CurrencySymbol;

    public override async Task OnNavigatedToAsync()
    {
        _store.Changed += OnStoreChanged;
        _ticks.Tick += OnTick;
        WeakReferenceMessenger.Default.RegisterAll(this);
        await LoadAsync(async () =>
        {
            await _store.RefreshAsync();
            await ReloadStationsAsync();
            await LoadStatsAsync();
        });
    }

    public override void OnNavigatedFrom()
    {
        _store.Changed -= OnStoreChanged;
        _ticks.Tick -= OnTick;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    public async void Receive(DataChangedMessage message)
    {
        if (message.Area is DataArea.Stations or DataArea.Settings) await TryAsync(ReloadStationsAsync);
        if (message.Area is DataArea.Sessions or DataArea.Products) await TryAsync(LoadStatsAsync);
    }

    private async Task ReloadStationsAsync()
    {
        var stations = await _stations.GetAllAsync();
        var warn = _settings.Current.WarnBeforeEndMinutes;
        var now = DateTime.Now;
        var byId = _all.ToDictionary(c => c.Id);
        _all.Clear();
        foreach (var st in stations)
        {
            var card = byId.GetValueOrDefault(st.Id) ?? new StationCardViewModel(st);
            card.Update(st, _store.ForStation(st.Id), now, warn);
            _all.Add(card);
        }
        ApplyFilter();
    }

    private async Task LoadStatsAsync()
    {
        _lastStats = DateTime.Now;
        var s = await _reports.GetDashboardStatsAsync(DateTime.Today);
        Stats = s;
        RevenueText = Money.Number(s.Revenue);
        if (s.RevenueYesterday > 0)
        {
            var pct = (s.Revenue - s.RevenueYesterday) / s.RevenueYesterday * 100m;
            RevenueTrendUp = pct >= 0;
            RevenueTrend = L.F("{0}% vs yesterday", $"{(pct >= 0 ? "+" : "")}{pct:0}");
        }
        else RevenueTrend = s.Revenue > 0 ? L.T("First sales today") : L.T("No sales yet today");
        var notes = new List<string>();
        if (s.CreditRepaid > 0) notes.Add(L.F("incl. {0} credit paid back", Money.Format(s.CreditRepaid)));
        if (s.CreditLeft > 0) notes.Add(L.F("{0} left on credit", Money.Format(s.CreditLeft)));
        RevenueNote = string.Join(" · ", notes);
        GamingText = Money.Number(s.GamingRevenue);
        GamingSub = s.Sessions == 0 ? L.T("No sessions yet") : L.F("{0} sessions · avg {1}", s.Sessions, Durations.Short(s.AverageSession));
        ProductsText = Money.Number(s.ProductRevenue);
        ProductsSub = L.F("Est. profit {0}", Money.Format(s.ProductProfit));
        if (s.Discounts > 0) GamingSub += " · " + L.F("discounts {0}", Money.Format(s.Discounts));
        MostUsed = s.MostUsedStation ?? "—";
        MostUsedSub = s.MostUsedStation is null ? L.T("No completed sessions") :
            $"{Durations.Short(s.MostUsedStationTime)}" + (s.MostSoldProduct is null ? "" : " · " + L.F("{0} top seller", s.MostSoldProduct));
        LowStockCount = s.LowStock.Count;
        LowStockSub = s.LowStock.Count == 0 ? L.T("All products stocked") : string.Join(" · ", s.LowStock.Take(3).Select(p => $"{p.Name} {p.Stock}"));
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        var warn = _settings.Current.WarnBeforeEndMinutes;
        var now = DateTime.Now;
        foreach (var c in _all) c.Update(c.Station, _store.ForStation(c.Id), now, warn);
        ApplyFilter();
    }

    private void OnTick(object? sender, DateTime now)
    {
        var warn = _settings.Current.WarnBeforeEndMinutes;
        foreach (var c in _all) if (c.IsActive) c.Refresh(now, warn);
        UpdateCounts();
        if (now - _lastStats > TimeSpan.FromMinutes(1)) _ = TryAsync(LoadStatsAsync);
    }

    partial void OnFilterChanged(string value) { UiState.Set("Dashboard.Filter", value); ApplyFilter(); }
    partial void OnSortChanged(string value) { UiState.Set("Dashboard.Sort", value); ApplyFilter(); }
    partial void OnViewModeChanged(string value) => UiState.Set("Dashboard.View", value);

    private void UpdateCounts()
    {
        CountAll = _all.Count;
        CountAvailable = _all.Count(c => c.Status == StationDisplayStatus.Available);
        CountOccupied = _all.Count(c => c.IsActive);
        CountReserved = _all.Count(c => c.Status == StationDisplayStatus.Reserved);
        CountOffline = _all.Count(c => c.Status == StationDisplayStatus.Offline);
        ActiveSub = L.F("{0} available · {1} reserved", CountAvailable, CountReserved);
    }

    private void ApplyFilter()
    {
        UpdateCounts();
        IEnumerable<StationCardViewModel> q = Filter switch
        {
            "Available" => _all.Where(c => c.Status == StationDisplayStatus.Available),
            "Occupied" => _all.Where(c => c.IsActive),
            "Reserved" => _all.Where(c => c.Status == StationDisplayStatus.Reserved),
            "Offline" => _all.Where(c => c.Status == StationDisplayStatus.Offline),
            _ => _all,
        };
        q = Sort switch
        {
            "Type" => q.OrderBy(c => c.Station.TypeName).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "Status" => q.OrderBy(c => c.StatusOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(c => c.Station.Location ?? "~").ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = q.ToList();
        if (!list.SequenceEqual(Cards))
        {
            Cards.Clear();
            foreach (var c in list) Cards.Add(c);
        }
        IsEmpty = _all.Count == 0;
    }

    // ---- actions ----

    [RelayCommand]
    private Task Open(StationCardViewModel card) => _workflow.OpenStationAsync(card.Id);

    [RelayCommand]
    private async Task Start(StationCardViewModel card)
    {
        var st = await _stations.GetAsync(card.Id);
        if (st is not null) await _workflow.StartSessionAsync(st);
    }

    [RelayCommand]
    private Task StartAny() => _workflow.StartSessionAsync();

    [RelayCommand]
    private Task Pause(StationCardViewModel card) => card.Session is { } s ? _workflow.TogglePauseAsync(s.Id) : Task.CompletedTask;

    [RelayCommand]
    private Task AddProduct(StationCardViewModel card) => card.Session is { } s ? _workflow.OpenSessionAsync(s.Id, focusCatalog: true) : Task.CompletedTask;

    [RelayCommand]
    private Task End(StationCardViewModel card) => card.Session is { } s ? _workflow.BillAsync(s.Id) : Task.CompletedTask;

    [RelayCommand]
    private async Task Reserve(StationCardViewModel card)
    {
        if (card.IsActive) return;
        var dlg = new ReserveViewModel(card.Name);
        if (await _dialogs.ShowAsync<ReserveResult>(dlg) is not { } r) return;
        await TryAsync(async () =>
        {
            await _stations.SetStateAsync(card.Id, StationState.Reserved, r.Name, r.At);
            await ReloadStationsAsync();
        }, L.F("{0} reserved", card.Name));
    }

    [RelayCommand]
    private Task ClearReservation(StationCardViewModel card) => SetStateAsync(card, StationState.Available, L.F("{0} is available", card.Name));

    [RelayCommand]
    private Task Maintenance(StationCardViewModel card) => SetStateAsync(card, StationState.Maintenance, L.F("{0} set to maintenance", card.Name));

    [RelayCommand]
    private Task BackInService(StationCardViewModel card) => SetStateAsync(card, StationState.Available, L.F("{0} is back in service", card.Name));

    private Task SetStateAsync(StationCardViewModel card, StationState state, string message) =>
        TryAsync(async () =>
        {
            await _stations.SetStateAsync(card.Id, state);
            await ReloadStationsAsync();
        }, message);

    [RelayCommand]
    private void GoLowStock() => _nav.Navigate(Page.Products, "lowstock");

    [RelayCommand]
    private void GoStations() => _nav.Navigate(Page.Stations);
}
