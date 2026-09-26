using GamingCenter.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed record CustomerRow(CustomerDto Customer)
{
    public string Name => Customer.Name;
    public string Phone => Customer.Phone ?? "—";
    public string Sessions => Customer.TotalSessions.ToString();
    public string Spent => Money.Format(Customer.TotalSpent);
    public string LastVisit => Customer.LastVisit?.ToString("dd/MM/yyyy") ?? "—";
    public string Owes => Customer.Balance > 0 ? Money.Format(Customer.Balance) : "—";
    public bool HasDebt => Customer.Balance > 0;
    public string Initials => string.Concat(Customer.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
}

public sealed partial class CustomersViewModel : PageViewModel, INavigationTarget
{
    private readonly ICustomerService _customers;
    private readonly DialogService _dialogs;
    private readonly CurrentUserService _user;
    private readonly FileDialogService _files;
    private List<CustomerDto> _all = [];

    private readonly ShellNavigator _nav;
    private readonly SessionWorkflow _workflow;
    private readonly ICreditService _credits;

    public CustomersViewModel(ICustomerService customers, DialogService dialogs, CurrentUserService user, FileDialogService files, ShellNavigator nav,
        SessionWorkflow workflow, ICreditService credits, ToastService toasts) : base(toasts)
    {
        _workflow = workflow;
        _credits = credits;
        _nav = nav;
        _customers = customers;
        _dialogs = dialogs;
        _user = user;
        _files = files;
    }

    public override string Title => L.T("Customers");
    public bool ShowDelete => _user.IsAdmin && EditId is not null;

    public ObservableCollection<CustomerRow> Rows { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CustomerRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDelete))]
    private int? _editId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editPhone = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private string? _editError;
    [ObservableProperty] private string _editStats = "";

    public string EditorTitle => EditId is null ? "New customer" : "Edit customer";

    private int? _pendingSelect;

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    public void Apply(object parameter)
    {
        if (parameter is int id)
        {
            _pendingSelect = id;
            Selected = Rows.FirstOrDefault(r => r.Customer.Id == id);
        }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private async Task ReloadAsync()
    {
        _all = (await _customers.GetAllAsync()).ToList();
        Summary = $"{_all.Count} customers · {Money.Format(_all.Sum(c => c.TotalSpent))} total spent";
        ApplyFilter();
        if (_pendingSelect is { } id) { Selected = Rows.FirstOrDefault(r => r.Customer.Id == id); _pendingSelect = null; }
    }

    private void ApplyFilter()
    {
        var t = Search.Trim();
        Rows.Clear();
        foreach (var c in _all.Where(c => t.Length == 0 || c.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) || (c.Phone?.Contains(t) ?? false)))
            Rows.Add(new CustomerRow(c));
    }

    partial void OnSelectedChanged(CustomerRow? value)
    {
        if (value is null) return;
        var c = value.Customer;
        EditId = c.Id;
        EditName = c.Name;
        EditPhone = c.Phone ?? "";
        EditNotes = c.Notes ?? "";
        EditStats = $"{c.TotalSessions} visits · {Money.Format(c.TotalSpent)} spent · customer since {c.CreatedAt:dd/MM/yyyy}"
            + (c.Balance > 0 ? $" · owes {Money.Format(c.Balance)}" : "");
        EditError = null;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
    }

    [RelayCommand]
    private void Add()
    {
        Selected = null;
        EditId = null;
        EditName = "";
        EditPhone = "";
        EditNotes = "";
        EditStats = "";
        EditError = null;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    [RelayCommand]
    private void OpenCredit()
    {
        if (EditId is { } id) _nav.Navigate(Page.Credits, id);
    }

    /// <summary>Customer eats or drinks without playing: sell products on their account.</summary>
    [RelayCommand]
    private async Task ChargeProducts()
    {
        if (EditId is not { } id) return;
        _pendingSelect = id;
        await _workflow.CounterSaleAsync(id);
        await LoadAsync(ReloadAsync);
    }

    /// <summary>Charge a plain amount (something not in the product list).</summary>
    [RelayCommand]
    private async Task ChargeAmount()
    {
        if (EditId is not { } id) return;
        var dlg = new ManualCreditViewModel(id, _user.IsAdmin, _credits, _customers, _dialogs);
        if (await _dialogs.ShowAsync<CreditEntryDto>(dlg) is { } entry)
        {
            Toasts.Success(L.F("{0} added to {1}'s credit", Money.Format(entry.Amount), EditName), L.F("Now owes {0}", Money.Format(entry.BalanceAfter)));
            _pendingSelect = id;
            await LoadAsync(ReloadAsync);
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        EditError = null;
        try
        {
            var saved = await _customers.SaveAsync(new SaveCustomerRequest(EditId, EditName, EditPhone, EditNotes));
            Toasts.Success($"{saved.Name} saved");
            _pendingSelect = saved.Id;
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Customers));
            await ReloadAsync();
        }
        catch (Exception ex) { EditError = ErrorText.For(ex); }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (EditId is not { } id) return;
        if (!await _dialogs.ConfirmAsync($"Delete {EditName}?", "The customer is hidden from lists. Their past sessions and receipts are kept.", "Delete", true)) return;
        if (await TryAsync(() => _customers.DeleteAsync(id), $"{EditName} deleted"))
        {
            CancelEdit();
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = _files.SaveCsv($"customers-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _all, [
            new("Id", c => c.Id), new("Name", c => c.Name), new("Phone", c => c.Phone), new("Notes", c => c.Notes),
            new("Sessions", c => c.TotalSessions), new("Total spent", c => c.TotalSpent), new("Owes", c => c.Balance), new("Last visit", c => c.LastVisit),
        ]), "Export complete", path);
    }
}

public sealed record BarItem(string Label, string Value, double Height, double ProductShare);
public sealed record StationBar(string Name, string Value, double Fraction, string Sub = "");

public sealed record StationUsageRow(StationUsage U)
{
    public string Name => U.Name;
    public string Where => string.Join(" · ", new[] { U.Type, U.Room }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Sessions => U.Sessions.ToString();
    public string PlayTime => Durations.Short(U.PlayTime);
    public string Revenue => Money.Number(U.Revenue);
    public string Occupancy => $"{U.Occupancy:P0}";
    public double Fraction => U.Occupancy;
}

public sealed record CustomerSpendRow(CustomerSpend C)
{
    public string Name => C.Name;
    public string Visits => L.F(C.Visits == 1 ? "{0} visit" : "{0} visits", C.Visits);
    public string Spent => Money.Number(C.Spent);
    public string Owes => C.Owes > 0 ? L.F("owes {0}", Money.Format(C.Owes)) : "";
}

/// <summary>One user account on the Reports page: what they took in and what they gave away.</summary>
public sealed record OperatorRow(OperatorTotal O, decimal AllCollected, decimal MaxCollected, decimal MaxBucket, IReadOnlyList<string> BucketLabels)
{
    public string Name => O.Name;
    public string Initials => string.Concat(O.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public string Role => string.IsNullOrEmpty(O.Role) ? "" : L.T(O.Role);
    public string Receipts => L.F(O.Receipts == 1 ? "{0} receipt" : "{0} receipts", O.Receipts);
    public string Collected => Money.Number(O.Collected);
    public string Discounts => O.Discounts > 0 ? L.F("discounts {0}", Money.Format(O.Discounts)) : "";

    /// <summary>Share of all money taken in by every account.</summary>
    public string ShareText => AllCollected > 0 ? L.F("{0} of all money taken", (O.Collected / AllCollected).ToString("P0")) : "";
    public double Rank => MaxCollected > 0 ? (double)(O.Collected / MaxCollected) : 0;

    // Split of their money by method (fractions of their own total).
    public double CashFrac => O.Collected > 0 ? (double)(O.Cash / O.Collected) : 0;
    public double CardFrac => O.Collected > 0 ? (double)(O.Card / O.Collected) : 0;
    public double OtherFrac => O.Collected > 0 ? (double)(O.Other / O.Collected) : 0;
    public string CashText => Money.Number(O.Cash);
    public string CardText => Money.Number(O.Card);
    public string OtherText => Money.Number(O.Other);

    public string SalesText => Money.Format(O.Sales);
    public string SessionsText => O.SessionsStarted.ToString();
    public string ReceiptsCount => O.Receipts.ToString();
    public string RepaidText => Money.Format(O.Repaid);
    public string DiscountText => Money.Format(O.Discounts);
    public string CreditGivenText => Money.Format(O.CreditGiven);
    public bool GaveAway => O.Discounts > 0 || O.CreditGiven > 0;

    /// <summary>Money taken per day (or month), on the same scale for every account so they can be compared.</summary>
    public IReadOnlyList<BarItem> Bars { get; } = (O.PerBucket ?? []).Select((v, i) => new BarItem(
        i < BucketLabels.Count ? BucketLabels[i] : "", v == 0 ? "" : Money.Format(v), MaxBucket > 0 ? (double)(v / MaxBucket) : 0, 0)).ToList();
}

/// <summary>Revenue reports (design 1k) for day, week, month, year or a custom range.</summary>
public sealed partial class ReportsViewModel : PageViewModel
{
    private readonly IReportService _reports;
    private readonly IProductService _products;
    private readonly PrintService _print;
    private readonly FileDialogService _files;
    private readonly ISettingsService _settings;
    private ReportData? _data;

    public ReportsViewModel(IReportService reports, IProductService products, PrintService print, FileDialogService files,
        ISettingsService settings, ToastService toasts) : base(toasts)
    {
        _reports = reports;
        _products = products;
        _print = print;
        _files = files;
        _settings = settings;
    }

    public override string Title => L.T("Reports");

    [ObservableProperty] private string _period = UiState.Get("Reports.Period", "Week");
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _trendText = "";
    [ObservableProperty] private bool _trendUp = true;
    [ObservableProperty] private string _gamingText = "";
    [ObservableProperty] private string _gamingShare = "";
    [ObservableProperty] private string _productText = "";
    [ObservableProperty] private string _profitText = "";
    [ObservableProperty] private string _sessionsText = "";
    [ObservableProperty] private string _sessionsSub = "";
    [ObservableProperty] private string _chartTitle = "Revenue per day";
    [ObservableProperty] private double _openShare;
    [ObservableProperty] private double _fixedShare;
    [ObservableProperty] private double _budgetShare;
    [ObservableProperty] private string _modesLegend = "";
    [ObservableProperty] private string _insights = "";
    [ObservableProperty] private bool _isEmpty;

    // Second KPI row
    [ObservableProperty] private string _avgTicketText = "—";
    [ObservableProperty] private string _avgTicketSub = "";
    [ObservableProperty] private string _playHoursText = "0";
    [ObservableProperty] private string _occupancyText = "0%";
    [ObservableProperty] private double _occupancy;
    [ObservableProperty] private string _customersText = "0";
    [ObservableProperty] private string _customersSub = "";
    [ObservableProperty] private string _counterText = "0";
    [ObservableProperty] private string _counterSub = "";
    [ObservableProperty] private string _discountsText = "0";
    [ObservableProperty] private string _creditText = "0";
    [ObservableProperty] private string _creditSub = "";
    [ObservableProperty] private string _busiestText = "";
    [ObservableProperty] private string _bestDayText = "";
    [ObservableProperty] private string _accountsSummary = "";

    public ObservableCollection<BarItem> HourBars { get; } = [];
    public ObservableCollection<BarItem> WeekdayBars { get; } = [];
    public ObservableCollection<StationBar> Methods { get; } = [];
    public ObservableCollection<StationBar> Rooms { get; } = [];
    public ObservableCollection<StationBar> Types { get; } = [];
    public ObservableCollection<StationBar> Categories { get; } = [];
    public ObservableCollection<StationUsageRow> StationUsage { get; } = [];
    public ObservableCollection<CustomerSpendRow> TopCustomers { get; } = [];
    public ObservableCollection<OperatorRow> Operators { get; } = [];

    public ObservableCollection<BarItem> Bars { get; } = [];
    public ObservableCollection<StationBar> Stations { get; } = [];
    public ObservableCollection<ProductSales> TopProducts { get; } = [];
    public ObservableCollection<string> ModeItems { get; } = [];
    public bool IsCustom => Period == "Custom";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    partial void OnPeriodChanged(string value)
    {
        UiState.Set("Reports.Period", value);
        Anchor = DateTime.Today;
        OnPropertyChanged(nameof(IsCustom));
        _ = LoadAsync(ReloadAsync);
    }
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }

    [RelayCommand]
    private async Task Previous() { Shift(-1); await LoadAsync(ReloadAsync); }

    [RelayCommand]
    private async Task Next() { Shift(1); await LoadAsync(ReloadAsync); }

    private void Shift(int dir) => Anchor = Period switch
    {
        "Day" => Anchor.AddDays(dir),
        "Month" => Anchor.AddMonths(dir),
        "Year" => Anchor.AddYears(dir),
        _ => Anchor.AddDays(7 * dir),
    };

    private (DateTime From, DateTime To, bool ByMonth) Range()
    {
        var a = Anchor.Date;
        return Period switch
        {
            "Day" => (a, a.AddDays(1), false),
            "Month" => (new DateTime(a.Year, a.Month, 1), new DateTime(a.Year, a.Month, 1).AddMonths(1), false),
            "Year" => (new DateTime(a.Year, 1, 1), new DateTime(a.Year + 1, 1, 1), true),
            "Custom" => ((CustomFrom ?? a).Date, (CustomTo ?? a).Date.AddDays(1), ((CustomTo ?? a) - (CustomFrom ?? a)).TotalDays > 62),
            _ => (a.AddDays(-6), a.AddDays(1), false),
        };
    }

    private async Task ReloadAsync()
    {
        var (from, to, byMonth) = Range();
        RangeText = to - from <= TimeSpan.FromDays(1) ? $"{from:ddd dd MMM yyyy}" : $"{from:dd MMM} – {to.AddDays(-1):dd MMM yyyy}";
        var d = _data = await _reports.GetReportAsync(from, to, byMonth);

        TotalText = Money.Number(d.TotalRevenue);
        if (d.PreviousTotalRevenue > 0)
        {
            var pct = (d.TotalRevenue - d.PreviousTotalRevenue) / d.PreviousTotalRevenue * 100m;
            TrendUp = pct >= 0;
            TrendText = L.F("{0}% vs previous period", $"{(pct >= 0 ? "+" : "")}{pct:0}");
        }
        else { TrendUp = true; TrendText = L.T("No data for previous period"); }
        GamingText = Money.Number(d.GamingRevenue);
        var sold = d.GamingRevenue + d.ProductRevenue;
        GamingShare = sold > 0 ? L.F("{0}% of sales", $"{d.GamingRevenue / sold * 100:0}") : "—";
        if (d.CreditCollected > 0) TrendText += " · " + L.F("incl. {0} credit paid back", Money.Format(d.CreditCollected));
        ProductText = Money.Number(d.ProductRevenue);
        ProfitText = L.F("Est. profit {0}", Money.Format(d.ProductProfit));
        SessionsText = d.Sessions.ToString();
        var days = Math.Max(1, (to - from).TotalDays);
        SessionsSub = d.Sessions == 0 ? L.T("No sessions") : L.F("avg {0} · {1} per day", Durations.Short(d.AverageSession), (d.Sessions / days).ToString("0.#"));
        ChartTitle = L.T(byMonth ? "Revenue per month" : "Revenue per day");

        Bars.Clear();
        var max = d.PerDay.Count == 0 ? 0 : d.PerDay.Max(x => x.Total);
        foreach (var x in d.PerDay)
            Bars.Add(new BarItem(x.Label, x.Total >= 1000 ? $"{x.Total / 1000m:0.#}k" : Money.Number(x.Total),
                max > 0 ? (double)(Math.Max(0, x.Total) / max) : 0, x.Gaming + x.Products > 0 ? (double)(x.Products / (x.Gaming + x.Products)) : 0));

        Stations.Clear();
        var smax = d.PerStation.Count == 0 ? 0 : d.PerStation.Max(x => x.Revenue);
        foreach (var s in d.PerStation.Take(8))
            Stations.Add(new StationBar(s.Name, Money.Number(s.Revenue), smax > 0 ? (double)(s.Revenue / smax) : 0));

        TopProducts.Clear();
        foreach (var p in d.TopProducts.Take(6)) TopProducts.Add(p);

        int total = d.ModeCounts.Values.Sum();
        OpenShare = total == 0 ? 0 : d.ModeCounts.GetValueOrDefault(SessionMode.Open) / (double)total;
        FixedShare = total == 0 ? 0 : d.ModeCounts.GetValueOrDefault(SessionMode.FixedDuration) / (double)total;
        BudgetShare = total == 0 ? 0 : d.ModeCounts.GetValueOrDefault(SessionMode.FixedBudget) / (double)total;
        ModesLegend = total == 0 ? "No sessions" : L.F("Open {0} · Fixed duration {1} · Fixed budget {2}", OpenShare.ToString("P0"), FixedShare.ToString("P0"), BudgetShare.ToString("P0"));

        var parts = new List<string>();
        if (d.BusiestHour is { } h) parts.Add(L.F("Busiest hour {0}:00–{1}:00", h.ToString("00"), ((h + 1) % 24).ToString("00")));
        if (d.PerStation.OrderByDescending(s => s.PlayTime).FirstOrDefault() is { } top)
            parts.Add(L.F("Most-used station {0} ({1})", top.Name, Durations.Short(top.PlayTime)));
        if (d.TotalRevenue > 0)
            parts.Add(string.Join(" · ", d.MethodTotals.OrderByDescending(kv => kv.Value).Select(kv => $"{L.T(kv.Key.ToString())} {kv.Value / d.TotalRevenue:P0}")));
        if (d.Discounts > 0) parts.Add(L.F("Discounts given {0}", Money.Format(d.Discounts)));
        if (d.CreditGiven > 0 || d.CreditCollected > 0)
            parts.Add(L.F("Credit given {0} · paid back {1}", Money.Format(d.CreditGiven), Money.Format(d.CreditCollected)));
        Insights = string.Join(" · ", parts);
        IsEmpty = d.TotalRevenue == 0 && d.Sessions == 0;
        if (d.Extras is { } extras) ShowExtras(d, extras);
    }

    private static string Short(decimal v) => v >= 10000 ? $"{v / 1000m:0}k" : v >= 1000 ? $"{v / 1000m:0.#}k" : Money.Number(v);

    private static void Fill(ObservableCollection<StationBar> target, IEnumerable<NamedAmount> items, Func<NamedAmount, string> sub, Func<string, string>? name = null)
    {
        target.Clear();
        var list = items.ToList();
        var max = list.Count == 0 ? 0 : list.Max(i => i.Amount);
        foreach (var i in list)
            target.Add(new StationBar(name?.Invoke(i.Name) ?? i.Name, Money.Number(i.Amount), max > 0 ? (double)(i.Amount / max) : 0, sub(i)));
    }

    private void ShowExtras(ReportData d, ReportExtras x)
    {
        AvgTicketText = x.Receipts == 0 ? "—" : Money.Number(d.Sales / x.Receipts);
        AvgTicketSub = L.F(x.Receipts == 1 ? "{0} receipt" : "{0} receipts", x.Receipts)
            + (d.Sessions > 0 ? " · " + L.F("{0} per session", Money.Format(d.GamingRevenue / d.Sessions)) : "");
        PlayHoursText = $"{x.PlayTime.TotalHours:0.#}";
        Occupancy = x.Occupancy;
        OccupancyText = $"{x.Occupancy:P0}";
        CustomersText = x.Customers.ToString();
        CustomersSub = L.F("{0} new · {1} walk-in sessions", x.NewCustomers, x.WalkInSessions);
        CounterText = Money.Number(x.CounterSalesRevenue);
        CounterSub = L.F(x.CounterSales == 1 ? "{0} sale without session" : "{0} sales without session", x.CounterSales);
        DiscountsText = Money.Number(d.Discounts);
        CreditText = Money.Number(x.UnpaidOnCredit);
        CreditSub = L.F("paid back {0}", Money.Format(d.CreditCollected));

        HourBars.Clear();
        int hmax = x.SessionsPerHour.Max();
        for (int h = 0; h < 24; h++)
        {
            int n = x.SessionsPerHour[h];
            HourBars.Add(new BarItem(h % 3 == 0 ? $"{h:00}" : "", n == 0 ? "" : n.ToString(), hmax > 0 ? n / (double)hmax : 0, 0));
        }
        BusiestText = hmax == 0 ? L.T("No sessions") : L.F("Busiest {0}:00–{1}:00", Array.IndexOf(x.SessionsPerHour.ToArray(), hmax).ToString("00"), ((Array.IndexOf(x.SessionsPerHour.ToArray(), hmax) + 1) % 24).ToString("00"));

        WeekdayBars.Clear();
        var wmax = x.RevenuePerWeekday.Max();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + 1) % 7);
            var v = x.RevenuePerWeekday[i];
            WeekdayBars.Add(new BarItem(culture.DateTimeFormat.GetAbbreviatedDayName(day), v == 0 ? "" : Short(v), wmax > 0 ? (double)(v / wmax) : 0, 0));
        }
        BestDayText = wmax == 0 ? "" : L.F("Best day: {0}", culture.DateTimeFormat.GetDayName((DayOfWeek)((x.RevenuePerWeekday.ToList().IndexOf(wmax) + 1) % 7)));

        var total = x.Methods.Sum(m => m.Amount);
        Fill(Methods, x.Methods, m => $"{(total > 0 ? m.Amount / total : 0):P0} · " + L.F(m.Count == 1 ? "{0} payment" : "{0} payments", m.Count), n => L.T(n));
        Fill(Rooms, x.PerRoom, r => L.F("{0} sessions · {1} h", r.Count, r.Extra.ToString("0.#")), n => L.T(n));
        Fill(Types, x.PerType, r => L.F("{0} sessions · {1} h", r.Count, r.Extra.ToString("0.#")), n => L.T(n));
        Fill(Categories, x.PerCategory, c => L.F("{0} units · profit {1}", c.Count, Money.Format(c.Extra)), n => L.T(n));

        StationUsage.Clear();
        foreach (var s in x.Stations) StationUsage.Add(new StationUsageRow(s));
        TopCustomers.Clear();
        foreach (var c in x.TopCustomers) TopCustomers.Add(new CustomerSpendRow(c));
        Operators.Clear();
        var allCollected = x.Operators.Sum(o => o.Collected);
        var maxCollected = x.Operators.Count == 0 ? 0 : x.Operators.Max(o => o.Collected);
        var maxBucket = x.Operators.SelectMany(o => o.PerBucket ?? []).DefaultIfEmpty(0).Max();
        // Label only a few bars when there are many (a month has 30).
        int step = Math.Max(1, d.PerDay.Count / 8);
        var labels = d.PerDay.Select((b, i) => i % step == 0 ? b.Label : "").ToList();
        foreach (var o in x.Operators) Operators.Add(new OperatorRow(o, allCollected, maxCollected, maxBucket, labels));
        AccountsSummary = x.Operators.Count == 0 ? "" : L.F(x.Operators.Count == 1 ? "{0} account · {1} taken in" : "{0} accounts · {1} taken in", x.Operators.Count, Money.Format(allCollected));
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_data is null) return;
        var path = _files.SaveCsv($"report-{_data.From:yyyyMMdd}-{_data.To.AddDays(-1):yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _data.PerDay, [
            new("Date", x => x.Day.ToString("yyyy-MM-dd")), new("Gaming", x => x.Gaming), new("Products", x => x.Products), new("Discounts", x => x.Discounts),
            new("Left on credit", x => x.Credit), new("Credit paid back", x => x.Repaid), new("Money received", x => x.Total),
        ]), "Export complete", path);
    }

    [RelayCommand]
    private async Task ExportStations()
    {
        if (_data?.Extras is not { } x) return;
        var path = _files.SaveCsv($"stations-{_data.From:yyyyMMdd}-{_data.To.AddDays(-1):yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, x.Stations, [
            new("Station", s => s.Name), new("Type", s => s.Type), new("Room", s => s.Room), new("Sessions", s => s.Sessions),
            new("Play hours", s => Math.Round(s.PlayTime.TotalHours, 2)), new("Revenue", s => s.Revenue), new("Occupancy %", s => Math.Round(s.Occupancy * 100, 1)),
        ]), "Export complete", path);
    }

    [RelayCommand]
    private async Task ExportProducts()
    {
        var path = _files.SaveCsv($"products-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        var list = await _products.GetAllAsync();
        await TryAsync(() => CsvWriter.WriteAsync(path, list, [
            new("Name", p => p.Name), new("Category", p => p.CategoryName), new("Purchase", p => p.PurchasePrice), new("Selling", p => p.SellingPrice),
            new("Stock", p => p.Stock), new("Minimum", p => p.MinStock), new("Active", p => p.IsActive),
        ]), "Export complete", path);
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (_data is null) return;
        try { _print.PrintDocument(_print.BuildReport(_data, $"{Period} report", _settings.Current.CenterName), "Revenue report"); }
        catch (Exception ex) { Toasts.Error("Printing failed", ErrorText.For(ex)); }
    }
}

public sealed partial class UsersViewModel : PageViewModel
{
    private readonly IUserService _users;

    public UsersViewModel(IUserService users, ToastService toasts) : base(toasts) => _users = users;

    public override string Title => L.T("Users");

    public ObservableCollection<UserDto> Rows { get; } = [];
    public UserRole[] Roles { get; } = [UserRole.Operator, UserRole.Admin];

    [ObservableProperty] private UserDto? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private int? _editId;
    [ObservableProperty] private string _editUsername = "";
    [ObservableProperty] private string _editDisplayName = "";
    [ObservableProperty] private UserRole _editRole = UserRole.Operator;
    [ObservableProperty] private bool _editActive = true;
    [ObservableProperty] private string _editPassword = "";
    [ObservableProperty] private string? _editError;

    public string EditorTitle => EditId is null ? "New user" : "Edit user";
    public string PasswordHint => EditId is null ? "At least 6 characters" : "Leave empty to keep the current password";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        Rows.Clear();
        foreach (var u in await _users.GetAllAsync()) Rows.Add(u);
    }

    partial void OnSelectedChanged(UserDto? value)
    {
        if (value is null) return;
        EditId = value.Id;
        EditUsername = value.Username;
        EditDisplayName = value.DisplayName;
        EditRole = value.Role;
        EditActive = value.IsActive;
        EditPassword = "";
        EditError = null;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PasswordHint));
    }

    [RelayCommand]
    private void Add()
    {
        Selected = null;
        EditId = null;
        EditUsername = "";
        EditDisplayName = "";
        EditRole = UserRole.Operator;
        EditActive = true;
        EditPassword = "";
        EditError = null;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PasswordHint));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    [RelayCommand]
    private async Task Save()
    {
        EditError = null;
        try
        {
            var saved = await _users.SaveAsync(new SaveUserRequest(EditId, EditUsername, EditDisplayName, EditRole, EditActive, EditPassword));
            Toasts.Success($"{saved.DisplayName} saved");
            await ReloadAsync();
            Selected = Rows.FirstOrDefault(u => u.Id == saved.Id);
        }
        catch (Exception ex) { EditError = ErrorText.For(ex); }
    }
}

public sealed record LanguageOption(string Code, string Name);

public sealed record BillingUnitOption(int Seconds, string Title, string Example);

/// <summary>Settings (design 1l). Edits a copy; nothing changes until Save.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IBackupService _backup;
    private readonly FileDialogService _files;
    private readonly DialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly IImageStore _images;
    private readonly ShellNavigator _nav;

    public SettingsViewModel(ISettingsService settings, IBackupService backup, FileDialogService files, DialogService dialogs,
        ThemeService theme, IImageStore images, ShellNavigator nav, ToastService toasts) : base(toasts)
    {
        _settings = settings;
        _backup = backup;
        _files = files;
        _dialogs = dialogs;
        _theme = theme;
        _images = images;
        _nav = nav;
        Model = settings.Current.Clone();
    }

    public override string Title => L.T("Settings");

    [ObservableProperty] private string _section = UiState.Get("Settings.Section", "General");
    partial void OnSectionChanged(string value) => UiState.Set("Settings.Section", value);
    [ObservableProperty] private AppSettings _model;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _backupInfo = "";
    [ObservableProperty] private string _minimumChargeText = "0";
    [ObservableProperty] private string _roundingStepText = "1";
    [ObservableProperty] private string _defaultRateText = "";
    [ObservableProperty] private string _extraControllerText = "";
    [ObservableProperty] private string _maxExtraControllersText = "";
    [ObservableProperty] private string _warnText = "";
    [ObservableProperty] private string _longText = "";
    [ObservableProperty] private string _backupHourText = "";
    [ObservableProperty] private string _keepText = "";
    [ObservableProperty] private string _receiptWidthText = "";
    [ObservableProperty] private int _billingUnit;
    [ObservableProperty] private RoundingMode _rounding;
    [ObservableProperty] private string _themeName = "Dark";
    [ObservableProperty] private string _languageCode = L.Language;
    private bool _loading;

    public string? LanguageError => L.LoadError;

    /// <summary>Theme buttons apply at once (no Save needed) and are remembered.</summary>
    partial void OnThemeNameChanged(string value)
    {
        if (_loading || string.Equals(value, _theme.Current, StringComparison.OrdinalIgnoreCase)) return;
        _ = ApplyThemeAsync(value);
    }

    private async Task ApplyThemeAsync(string theme)
    {
        try { await _settings.SavePreferencesAsync(theme, null); }
        catch (Exception ex) { Toasts.Error(L.T("Could not save"), ErrorText.For(ex)); }
        _theme.Apply(theme);
    }

    /// <summary>Called by the shell when the top-bar button switched the theme while this page is open.</summary>
    public void SyncTheme()
    {
        _loading = true;
        ThemeName = _theme.Current;
        _loading = false;
    }

    /// <summary>Picking a language saves it and restarts the app to redraw every screen.</summary>
    partial void OnLanguageCodeChanged(string value)
    {
        if (_loading || string.Equals(value, L.Language, StringComparison.OrdinalIgnoreCase)) return;
        _ = ChangeLanguageAsync(value);
    }

    private async Task ChangeLanguageAsync(string code)
    {
        if (await _dialogs.ConfirmAsync(L.T("Restart to change the language?"),
                L.T("The app restarts now. Running sessions keep going; their timers are saved."), L.T("Restart now")))
        {
            try
            {
                await _settings.SavePreferencesAsync(null, code);
                App.Restart();
                return;
            }
            catch (Exception ex) { Toasts.Error(L.T("Could not save"), ErrorText.For(ex)); }
        }
        _loading = true;
        LanguageCode = L.Language;
        _loading = false;
    }

    public IReadOnlyList<BillingUnitOption> Units { get; } =
    [
        new(0, "Exact time", "1h 53m 42s → exact"),
        new(60, "Per minute", "1h 53m 42s → 1h 54m"),
        new(300, "Per 5 minutes", "→ 1h 55m"),
        new(900, "Per 15 minutes", "→ 2h 00m"),
        new(3600, "Per hour", "→ 2h 00m"),
    ];
    public RoundingMode[] RoundingModes { get; } = [RoundingMode.Up, RoundingMode.Nearest, RoundingMode.Down];
    public IReadOnlyList<string> Printers { get; } = PrintService.InstalledPrinters();
    public IReadOnlyList<LanguageOption> Languages { get; } = L.Languages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();
    public string DataFolder => System.IO.Path.GetDirectoryName(_backup.DatabasePath) ?? "";

    public override Task OnNavigatedToAsync()
    {
        LoadFields();
        return Task.CompletedTask;
    }

    private void LoadFields()
    {
        _loading = true;
        var m = Model = _settings.Current.Clone();
        BillingUnit = m.BillingUnitSeconds;
        Rounding = m.Rounding;
        MinimumChargeText = m.MinimumChargeMinutes.ToString();
        RoundingStepText = Money.Number(m.MoneyRoundingStep);
        DefaultRateText = Money.Number(m.DefaultHourlyRate).Replace(",", "");
        ExtraControllerText = Money.Number(m.DefaultExtraControllerRate).Replace(",", "");
        MaxExtraControllersText = m.DefaultMaxExtraControllers.ToString();
        WarnText = m.WarnBeforeEndMinutes.ToString();
        LongText = m.LongSessionAlertHours.ToString();
        BackupHourText = m.AutoBackupHour.ToString();
        KeepText = m.BackupsToKeep.ToString();
        ReceiptWidthText = m.ReceiptWidthMm.ToString();
        ThemeName = _theme.Current;
        LanguageCode = L.Language;
        _loading = false;
        UpdatePreview();
        UpdateBackupInfo();
    }

    partial void OnBillingUnitChanged(int value) => UpdatePreview();
    partial void OnRoundingChanged(RoundingMode value) => UpdatePreview();
    partial void OnMinimumChargeTextChanged(string value) => UpdatePreview();
    partial void OnRoundingStepTextChanged(string value) => UpdatePreview();

    private void UpdatePreview()
    {
        int.TryParse(MinimumChargeText, out var min);
        Money.TryParse(RoundingStepText, out var step);
        var rules = new BillingRules(BillingUnit, Rounding, Math.Max(0, min), Math.Max(0, step));
        var sample = new TimeSpan(1, 53, 42);
        var billed = BillingCalculator.BillableSeconds(sample, rules);
        Preview = $"Preview at 300 {Money.CurrencySymbol}/h: 1h 53m 42s → {Money.Format(BillingCalculator.Cost(sample, 300m, rules))}"
            + $" (billed {Durations.Long(TimeSpan.FromSeconds(billed))})";
    }

    private void UpdateBackupInfo()
    {
        var folder = string.IsNullOrWhiteSpace(Model.BackupFolder) ? _backup.DefaultBackupFolder : Model.BackupFolder;
        var last = _backup.ListBackups(folder).FirstOrDefault();
        BackupInfo = last is null ? $"{folder} · no backup yet"
            : $"{folder} · last: {last.LastWriteTime:dd MMM HH:mm} · {last.Length / 1024.0 / 1024.0:0.0} MB";
    }

    [RelayCommand]
    private async Task Save()
    {
        var m = Model.Clone();
        m.BillingUnitSeconds = BillingUnit;
        m.Rounding = Rounding;
        m.Theme = _settings.Current.Theme;
        m.Language = _settings.Current.Language;
        if (!int.TryParse(MinimumChargeText, out var min)) { Toasts.Error("Minimum charge must be a whole number of minutes."); return; }
        if (!Money.TryParse(RoundingStepText, out var step)) { Toasts.Error("Money rounding step is not a number."); return; }
        if (!Money.TryParse(DefaultRateText, out var rate)) { Toasts.Error("Default hourly price is not a number."); return; }
        if (!int.TryParse(WarnText, out var warn) || !int.TryParse(LongText, out var lng)) { Toasts.Error("Warning thresholds must be whole numbers."); return; }
        if (!int.TryParse(BackupHourText, out var hour) || !int.TryParse(KeepText, out var keep)) { Toasts.Error("Backup hour and count must be whole numbers."); return; }
        if (!Money.TryParse(ExtraControllerText, out var extraCtrl)) { Toasts.Error("Extra controller price is not a number."); return; }
        if (!int.TryParse(MaxExtraControllersText, out var maxExtra)) { Toasts.Error("Extra controllers allowed must be a whole number."); return; }
        m.DefaultExtraControllerRate = extraCtrl;
        m.DefaultMaxExtraControllers = maxExtra;
        if (!int.TryParse(ReceiptWidthText, out var width)) { Toasts.Error("Receipt width must be a whole number of millimetres."); return; }
        m.MinimumChargeMinutes = min;
        m.MoneyRoundingStep = step;
        m.DefaultHourlyRate = rate;
        m.WarnBeforeEndMinutes = warn;
        m.LongSessionAlertHours = lng;
        m.AutoBackupHour = hour;
        m.BackupsToKeep = keep;
        m.ReceiptWidthMm = width;
        m.LastBackupAt = _settings.Current.LastBackupAt;

        if (await TryAsync(() => _settings.SaveAsync(m), L.T("Settings saved"), L.T("Billing changes apply to new sessions.")))
        {
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Settings));
            LoadFields();
        }
    }

    [RelayCommand]
    private void Revert() => LoadFields();

    [RelayCommand]
    private async Task ChooseLogo()
    {
        var file = _files.OpenImage();
        if (file is null) return;
        await TryAsync(async () =>
        {
            Model.LogoPath = await _images.ImportAsync(file, "branding");
            OnPropertyChanged(nameof(Model));
        });
    }

    [RelayCommand]
    private void ClearPrinter()
    {
        Model.ReceiptPrinterName = null;
        OnPropertyChanged(nameof(Model));
    }

    [RelayCommand]
    private void ResetLogo()
    {
        Model.LogoPath = null;
        OnPropertyChanged(nameof(Model));
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        string? file = null;
        if (await TryAsync(async () => file = await _backup.BackupNowAsync(Model.BackupFolder), "Backup completed"))
            UpdateBackupInfo();
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var folder = _files.PickFolder(string.IsNullOrWhiteSpace(Model.BackupFolder) ? _backup.DefaultBackupFolder : Model.BackupFolder);
        if (folder is null) return;
        Model.BackupFolder = folder;
        OnPropertyChanged(nameof(Model));
        UpdateBackupInfo();
        Toasts.Info("Backup folder changed", "Click Save to keep this folder.");
    }

    [RelayCommand]
    private async Task Restore()
    {
        var file = _files.OpenBackup(string.IsNullOrWhiteSpace(Model.BackupFolder) ? _backup.DefaultBackupFolder : Model.BackupFolder);
        if (file is null) return;
        var info = new System.IO.FileInfo(file);
        if (!await _dialogs.ConfirmAsync("Restore this backup?",
                $"{info.Name} ({info.LastWriteTime:dd MMM yyyy HH:mm}) will replace ALL current data. A safety copy of the current database is saved first in backups\\before-restore. The app restarts afterwards.",
                "Restore and restart", danger: true)) return;
        if (!await TryAsync(() => _backup.RestoreAsync(file))) return;

        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Toasts.Error("Could not open folder", ex.Message); }
    }

    [RelayCommand]
    private void GoUsers() => _nav.Navigate(Page.Users);
}
