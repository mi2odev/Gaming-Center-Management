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

    public CustomersViewModel(ICustomerService customers, DialogService dialogs, CurrentUserService user, FileDialogService files, ShellNavigator nav, ToastService toasts) : base(toasts)
    {
        _nav = nav;
        _customers = customers;
        _dialogs = dialogs;
        _user = user;
        _files = files;
    }

    public override string Title => "Customers";
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
public sealed record StationBar(string Name, string Value, double Fraction);

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

    public override string Title => "Reports";

    [ObservableProperty] private string _period = "Week";
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

    public ObservableCollection<BarItem> Bars { get; } = [];
    public ObservableCollection<StationBar> Stations { get; } = [];
    public ObservableCollection<ProductSales> TopProducts { get; } = [];
    public ObservableCollection<string> ModeItems { get; } = [];
    public bool IsCustom => Period == "Custom";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    partial void OnPeriodChanged(string value)
    {
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
            TrendText = $"{(pct >= 0 ? "+" : "")}{pct:0}% vs previous period";
        }
        else { TrendUp = true; TrendText = "No data for previous period"; }
        GamingText = Money.Number(d.GamingRevenue);
        GamingShare = d.TotalRevenue > 0 ? $"{d.GamingRevenue / d.TotalRevenue * 100:0}% of total" : "—";
        ProductText = Money.Number(d.ProductRevenue);
        ProfitText = $"Est. profit {Money.Format(d.ProductProfit)}";
        SessionsText = d.Sessions.ToString();
        var days = Math.Max(1, (to - from).TotalDays);
        SessionsSub = d.Sessions == 0 ? "No sessions" : $"avg {Durations.Short(d.AverageSession)} · {d.Sessions / days:0.#} per day";
        ChartTitle = byMonth ? "Revenue per month" : "Revenue per day";

        Bars.Clear();
        var max = d.PerDay.Count == 0 ? 0 : d.PerDay.Max(x => x.Total);
        foreach (var x in d.PerDay)
            Bars.Add(new BarItem(x.Label, x.Total >= 1000 ? $"{x.Total / 1000m:0.#}k" : Money.Number(x.Total),
                max > 0 ? (double)(x.Total / max) : 0, x.Total > 0 ? (double)(x.Products / x.Total) : 0));

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
        ModesLegend = total == 0 ? "No sessions" : $"Open {OpenShare:P0} · Fixed duration {FixedShare:P0} · Fixed budget {BudgetShare:P0}";

        var parts = new List<string>();
        if (d.BusiestHour is { } h) parts.Add($"Busiest hour {h:00}:00–{(h + 1) % 24:00}:00");
        if (d.PerStation.OrderByDescending(s => s.PlayTime).FirstOrDefault() is { } top)
            parts.Add($"Most-used station {top.Name} ({Durations.Short(top.PlayTime)})");
        if (d.TotalRevenue > 0)
            parts.Add(string.Join(" · ", d.MethodTotals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / d.TotalRevenue:P0}")));
        if (d.CreditGiven > 0 || d.CreditCollected > 0)
            parts.Add($"Credit given {Money.Format(d.CreditGiven)} · paid back {Money.Format(d.CreditCollected)}");
        Insights = string.Join(" · ", parts);
        IsEmpty = d.TotalRevenue == 0 && d.Sessions == 0;
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (_data is null) return;
        var path = _files.SaveCsv($"report-{_data.From:yyyyMMdd}-{_data.To.AddDays(-1):yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _data.PerDay, [
            new("Date", x => x.Day.ToString("yyyy-MM-dd")), new("Gaming", x => x.Gaming), new("Products", x => x.Products), new("Total", x => x.Total),
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

    public override string Title => "Users";

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

    public override string Title => "Settings";

    [ObservableProperty] private string _section = "General";
    [ObservableProperty] private AppSettings _model;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _backupInfo = "";
    [ObservableProperty] private string _minimumChargeText = "0";
    [ObservableProperty] private string _roundingStepText = "1";
    [ObservableProperty] private string _defaultRateText = "";
    [ObservableProperty] private string _warnText = "";
    [ObservableProperty] private string _longText = "";
    [ObservableProperty] private string _backupHourText = "";
    [ObservableProperty] private string _keepText = "";
    [ObservableProperty] private string _receiptWidthText = "";
    [ObservableProperty] private int _billingUnit;
    [ObservableProperty] private RoundingMode _rounding;
    [ObservableProperty] private string _themeName = "Dark";

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
    public string[] Languages { get; } = ["en"];
    public string DataFolder => System.IO.Path.GetDirectoryName(_backup.DatabasePath) ?? "";

    public override Task OnNavigatedToAsync()
    {
        LoadFields();
        return Task.CompletedTask;
    }

    private void LoadFields()
    {
        var m = Model = _settings.Current.Clone();
        BillingUnit = m.BillingUnitSeconds;
        Rounding = m.Rounding;
        MinimumChargeText = m.MinimumChargeMinutes.ToString();
        RoundingStepText = Money.Number(m.MoneyRoundingStep);
        DefaultRateText = Money.Number(m.DefaultHourlyRate).Replace(",", "");
        WarnText = m.WarnBeforeEndMinutes.ToString();
        LongText = m.LongSessionAlertHours.ToString();
        BackupHourText = m.AutoBackupHour.ToString();
        KeepText = m.BackupsToKeep.ToString();
        ReceiptWidthText = m.ReceiptWidthMm.ToString();
        ThemeName = m.Theme;
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
        m.Theme = ThemeName;
        if (!int.TryParse(MinimumChargeText, out var min)) { Toasts.Error("Minimum charge must be a whole number of minutes."); return; }
        if (!Money.TryParse(RoundingStepText, out var step)) { Toasts.Error("Money rounding step is not a number."); return; }
        if (!Money.TryParse(DefaultRateText, out var rate)) { Toasts.Error("Default hourly price is not a number."); return; }
        if (!int.TryParse(WarnText, out var warn) || !int.TryParse(LongText, out var lng)) { Toasts.Error("Warning thresholds must be whole numbers."); return; }
        if (!int.TryParse(BackupHourText, out var hour) || !int.TryParse(KeepText, out var keep)) { Toasts.Error("Backup hour and count must be whole numbers."); return; }
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

        if (await TryAsync(() => _settings.SaveAsync(m), "Settings saved", "Billing changes apply to new sessions."))
        {
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Settings));
            if (!string.Equals(_theme.Current, m.Theme, StringComparison.OrdinalIgnoreCase)) _theme.Apply(m.Theme);
            else LoadFields();
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
