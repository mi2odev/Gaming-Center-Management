using GamingCenter.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed record StartSessionArgs(StationDto? Station);

public sealed record Preset(int Value, string Label);

public sealed record ControllerOption(int Count, string Label, string PriceNote);

/// <summary>Start-session dialog (design 1e): customer, pricing mode, duration or budget.</summary>
public sealed partial class StartSessionViewModel : DialogViewModel
{
    private readonly ISessionService _sessions;
    private readonly ISettingsService _settings;

    public StartSessionViewModel(StartSessionArgs args, IStationService stations, ISessionService sessions, ISettingsService settings,
        ICustomerService customers, DialogService dialogs, LiveSessionStore store)
    {
        _sessions = sessions;
        _settings = settings;
        Customer = new CustomerPickerViewModel(customers, dialogs);
        ChooseStation = args.Station is null;
        _station = args.Station;
        if (ChooseStation) _ = LoadStationsAsync(stations, store);
        OnStationChanged(_station);
    }

    public CustomerPickerViewModel Customer { get; }
    public bool ChooseStation { get; }
    public ObservableCollection<StationDto> AvailableStations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStation), nameof(StationName), nameof(StationSub), nameof(StationTag), nameof(RateNumber), nameof(ImagePath))]
    private StationDto? _station;

    public bool HasStation => Station is not null;
    public string StationName => Station?.Name ?? L.T("Choose a station");
    public string StationSub => Station is null ? "" : string.Join(" · ", new[]
    {
        Station.TypeName, Station.Location,
        Station.ControllerCount is { } c and > 0 ? L.F(c > 1 ? "{0} controllers" : "{0} controller", c) : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string StationTag => Station?.Tag ?? "";
    public string? ImagePath => Station?.ImagePath;
    public string RateNumber => Station is null ? "—" : $"{Money.Number(EffectiveRate)} {Money.CurrencySymbol}";

    /// <summary>Hourly rate for the chosen number of controllers.</summary>
    public decimal EffectiveRate => Station?.RateFor(Station.HasControllerPricing ? Controllers : null) ?? 0;

    public bool HasControllerPricing => Station?.HasControllerPricing == true;
    public ObservableCollection<ControllerOption> ControllerOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateNumber), nameof(EffectiveRate))]
    private int _controllers = 2;

    partial void OnControllersChanged(int value) => Recalculate();
    public string BillingLabel => L.F("billed {0}", L.T(_settings.Current.BillingRules.UnitLabel));
    public string StartsNow => L.F("Starts now · {0}", DateTime.Now.ToString("HH:mm"));
    public string WarningHint
    {
        get
        {
            var cfg = _settings.Current;
            var warn = cfg.WarnBeforeEndMinutes > 0 ? L.F("Warning at {0} min remaining", cfg.WarnBeforeEndMinutes) : L.T("No warning before end");
            return warn + " · " + (cfg.AutoEndWhenTimeExpires ? L.T("session will stop automatically") : L.T("session will not auto-stop"));
        }
    }

    [ObservableProperty] private SessionMode _mode = SessionMode.Open;
    [ObservableProperty] private string _durationText = "60";
    [ObservableProperty] private string _budgetText = "500";

    [ObservableProperty] private string _fixedPriceText = "";
    [ObservableProperty] private string _fixedEndText = "";
    [ObservableProperty] private string _maxTimeText = "";
    [ObservableProperty] private string _maxTimeHint = "";

    public IReadOnlyList<Preset> DurationPresets { get; } = new[] { 30, 60, 90, 120, 180 }.Select(m => new Preset(m, Durations.Minutes(m))).ToList();
    public IReadOnlyList<Preset> BudgetPresets { get; } = new[] { 200, 300, 500, 1000 }.Select(a => new Preset(a, a.ToString())).ToList();

    private async Task LoadStationsAsync(IStationService stations, LiveSessionStore store)
    {
        var all = await stations.GetAllAsync(includeInactive: false);
        foreach (var s in all.Where(s => s.State != StationState.Maintenance && store.ForStation(s.Id) is null))
            AvailableStations.Add(s);
        Station ??= AvailableStations.FirstOrDefault(s => s.State == StationState.Available) ?? AvailableStations.FirstOrDefault();
        if (AvailableStations.Count == 0) Error = L.T("No station is available right now.");
    }

    partial void OnStationChanged(StationDto? value)
    {
        ControllerOptions.Clear();
        if (value is { HasControllerPricing: true })
        {
            var plan = value.Plan!;
            // Show the included count and each extra option: "2 · included", "3 · +100", "4 · +200".
            for (int n = plan.Included; n <= plan.Max; n++)
            {
                var extra = value.RateFor(n) - value.HourlyRate;
                ControllerOptions.Add(new ControllerOption(n, $"{n}", extra <= 0 ? L.T("included") : $"+{Money.Number(extra)}/h"));
            }
            Controllers = plan.Included;
        }
        OnPropertyChanged(nameof(HasControllerPricing));
        OnPropertyChanged(nameof(RateNumber));
        Recalculate();
    }
    partial void OnModeChanged(SessionMode value) => Recalculate();
    partial void OnDurationTextChanged(string value) => Recalculate();
    partial void OnBudgetTextChanged(string value) => Recalculate();

    private void Recalculate()
    {
        Error = null;
        decimal rate = EffectiveRate;
        var rules = _settings.Current.BillingRules;
        if (int.TryParse(DurationText, out var minutes) && minutes > 0)
        {
            FixedPriceText = Money.Format(BillingCalculator.Cost(TimeSpan.FromMinutes(minutes), rate, rules));
            FixedEndText = Durations.Minutes(minutes) + " · " + L.F("ends at {0}", DateTime.Now.AddMinutes(minutes).ToString("HH:mm"));
        }
        else { FixedPriceText = "—"; FixedEndText = L.T("Enter minutes"); }

        if (Money.TryParse(BudgetText, out var budget) && budget > 0 && rate > 0)
        {
            MaxTimeText = Durations.Clock(BillingCalculator.TimeForBudget(budget, rate));
            MaxTimeHint = $"{Money.Number(budget)} ÷ {Money.Number(rate)}/h";
        }
        else { MaxTimeText = "—"; MaxTimeHint = rate <= 0 ? L.T("Free station") : L.T("Enter an amount"); }
    }

    [RelayCommand]
    private void SetDuration(int minutes) => DurationText = minutes.ToString();

    [RelayCommand]
    private void SetBudget(int amount) => BudgetText = amount.ToString();

    [RelayCommand]
    private async Task Start()
    {
        if (Station is null) { Error = L.T("Choose a station."); return; }
        int? minutes = null;
        decimal? budget = null;
        if (Mode == SessionMode.FixedDuration)
        {
            if (!int.TryParse(DurationText, out var m) || m <= 0) { Error = L.T("Enter the duration in minutes."); return; }
            minutes = m;
        }
        else if (Mode == SessionMode.FixedBudget)
        {
            if (!Money.TryParse(BudgetText, out var b) || b <= 0) { Error = L.T("Enter the customer's budget."); return; }
            budget = b;
        }

        GamingSession? started = null;
        if (await RunAsync(async () => started = await _sessions.StartAsync(new StartSessionRequest(Station.Id, Customer.SelectedId, Mode, minutes, budget, HasControllerPricing ? Controllers : null))))
            Close(started);
    }
}
