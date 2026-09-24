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

/// <summary>Paper-style receipt preview shared by Sessions and Sales.</summary>
public sealed class ReceiptPreview(ReceiptDto r)
{
    public ReceiptDto Receipt => r;
    public string CenterName => r.CenterName;
    public string Contact => string.Join(" · ", new[] { r.Address, r.Phone }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Stamp => $"{r.IssuedAt:dd/MM/yyyy HH:mm} · #{r.ReceiptNumber}";
    public bool IsGaming => r.Mode != SessionMode.CounterSale;
    public string Station => r.StationName;
    public string SessionRange => $"{r.StartTime:HH:mm} → {r.EndTime:HH:mm}";
    public string PlayTime => Durations.Long(TimeSpan.FromSeconds(r.PlayedSeconds));
    public string GamingLabel => $"Gaming @{Money.Number(r.HourlyRate)}/h";
    public string Gaming => Money.Number(r.GamingTotal);
    public IEnumerable<(string Name, string Total)> Lines => r.Lines.Select(l => ($"{l.Name} x{l.Quantity}", Money.Number(l.LineTotal)));
    public IReadOnlyList<ReceiptLine> Items => r.Lines;
    public bool HasItems => r.Lines.Count > 0;
    public string Total => Money.Format(r.Total);
    public string Method => r.Method.ToString();
    public string Received => Money.Number(r.AmountReceived);
    public bool HasChange => r.Change > 0;
    public string Change => Money.Number(r.Change);
    public string? Customer => r.CustomerName;
    public string Footer => r.Footer;
    public string Note
    {
        get
        {
            var parts = new List<string>();
            foreach (var (start, end) in r.Pauses)
                parts.Add($"Paused {start:HH:mm} → {end:HH:mm} ({Durations.Short((end ?? start) - start)})");
            if (!string.IsNullOrWhiteSpace(r.Operator)) parts.Add($"Operator {r.Operator}");
            if (IsGaming) parts.Add($"Rate locked at session start · billed {r.BillingLabel}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record HistoryRowView(HistoryRow Row)
{
    public int SessionId => Row.SessionId;
    public string Receipt => Row.ReceiptNumber ?? "—";
    public string Station => Row.StationName;
    public string Customer => Row.CustomerName;
    public string Time => Row.Mode == SessionMode.CounterSale ? $"{Row.StartTime:HH:mm}" : $"{Row.StartTime:HH:mm} → {Row.EndTime:HH:mm}";
    public string Date => Row.StartTime.ToString("dd/MM");
    public string Duration => Row.Mode == SessionMode.CounterSale ? "—" : Durations.Short(TimeSpan.FromSeconds(Row.PlayedSeconds));
    public string Gaming => Row.Mode == SessionMode.CounterSale ? "—" : Money.Format(Row.GamingTotal);
    public string Products => Row.ProductsTotal > 0 ? Money.Format(Row.ProductsTotal) : "—";
    public string Total => Money.Format(Row.Total);
    public string Paid => Row.Status == SessionStatus.Cancelled ? "Cancelled" : Row.Method?.ToString() ?? "—";
    public bool IsCancelled => Row.Status == SessionStatus.Cancelled;
}

/// <summary>Session history (design 1j): period filters, table and receipt preview.</summary>
public sealed partial class SessionsViewModel : PageViewModel, INavigationTarget
{
    private readonly IReportService _reports;
    private readonly PrintService _print;
    private readonly FileDialogService _files;
    private List<HistoryRow> _rows = [];

    public SessionsViewModel(IReportService reports, PrintService print, FileDialogService files, ToastService toasts) : base(toasts)
    {
        _reports = reports;
        _print = print;
        _files = files;
    }

    public override string Title => "Sessions";

    public ObservableCollection<HistoryRowView> Rows { get; } = [];

    [ObservableProperty] private string _period = "Today";
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private HistoryRowView? _selected;
    [ObservableProperty] private ReceiptPreview? _receipt;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _gamingText = "";
    [ObservableProperty] private string _productsText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _avgText = "";
    [ObservableProperty] private bool _isEmpty;

    public bool IsCustom => Period == "Custom";

    private int? _pendingSelect;

    public override Task OnNavigatedToAsync()
    {
        WeakReferenceMessenger.Default.Register<SessionsViewModel, DataChangedMessage>(this, (r, m) =>
        {
            if (m.Area == DataArea.Sessions) _ = r.LoadAsync(r.ReloadAsync);
        });
        return LoadAsync(ReloadAsync);
    }

    public override void OnNavigatedFrom() => WeakReferenceMessenger.Default.UnregisterAll(this);

    public void Apply(object parameter)
    {
        if (parameter is SearchResult { Kind: SearchKind.Session } r)
        {
            Period = "Custom";
            CustomFrom = DateTime.Today.AddYears(-5);
            CustomTo = DateTime.Today;
            _pendingSelect = r.Id;
            Search = r.Title.Replace("Receipt #", "");
        }
    }

    partial void OnPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        _ = LoadAsync(ReloadAsync);
    }
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnSearchChanged(string value) => _ = LoadAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        var (from, to) = Periods.Range(Period, DateTime.Today, CustomFrom, CustomTo);
        _rows = (await _reports.GetHistoryAsync(from, to, Search)).ToList();
        var target = _pendingSelect ?? Selected?.SessionId;
        _pendingSelect = null;
        Rows.Clear();
        foreach (var r in _rows) Rows.Add(new HistoryRowView(r));
        var paid = _rows.Where(r => r.Status == SessionStatus.Completed).ToList();
        var gaming = paid.Where(r => r.Mode != SessionMode.CounterSale).ToList();
        CountText = $"{gaming.Count} sessions" + (paid.Count > gaming.Count ? $" · {paid.Count - gaming.Count} counter sales" : "");
        GamingText = Money.Format(paid.Sum(r => r.GamingTotal));
        ProductsText = Money.Format(paid.Sum(r => r.ProductsTotal));
        TotalText = Money.Format(paid.Sum(r => r.Total));
        AvgText = gaming.Count == 0 ? "—" : Durations.Short(TimeSpan.FromSeconds(gaming.Average(r => r.PlayedSeconds)));
        IsEmpty = Rows.Count == 0;
        Selected = Rows.FirstOrDefault(r => r.SessionId == target) ?? Rows.FirstOrDefault(r => !r.IsCancelled);
    }

    async partial void OnSelectedChanged(HistoryRowView? value)
    {
        Receipt = null;
        if (value is null || value.IsCancelled) return;
        await TryAsync(async () =>
        {
            var r = await _reports.GetReceiptAsync(value.SessionId);
            Receipt = r is null ? null : new ReceiptPreview(r);
        });
    }

    [RelayCommand]
    private void PrintReceipt()
    {
        if (Receipt is null) return;
        try { _print.PrintReceipt(Receipt.Receipt); }
        catch (Exception ex) { Toasts.Error("Printing failed", ErrorText.For(ex)); }
    }

    [RelayCommand]
    private void PdfReceipt()
    {
        if (Receipt is null) return;
        try { _print.PrintReceipt(Receipt.Receipt, choosePrinter: true); }
        catch (Exception ex) { Toasts.Error("Printing failed", ErrorText.For(ex)); }
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = _files.SaveCsv($"sessions-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _rows, [
            new("Receipt", r => r.ReceiptNumber), new("Station", r => r.StationName), new("Customer", r => r.CustomerName),
            new("Mode", r => r.Mode), new("Status", r => r.Status), new("Start", r => r.StartTime), new("End", r => r.EndTime),
            new("Play minutes", r => Math.Round(r.PlayedSeconds / 60.0, 1)), new("Gaming", r => r.GamingTotal),
            new("Products", r => r.ProductsTotal), new("Total", r => r.Total), new("Payment", r => r.Method), new("Operator", r => r.Operator),
        ]), "Export complete", path);
    }
}

public sealed record PaymentRowView(PaymentRow Row)
{
    public string Receipt => Row.ReceiptNumber;
    public string Time => Row.PaidAt.ToString("dd/MM HH:mm");
    public string Station => Row.StationName;
    public string Customer => Row.CustomerName;
    public string Gaming => Row.GamingAmount > 0 ? Money.Format(Row.GamingAmount) : "—";
    public string Products => Row.ProductsAmount > 0 ? Money.Format(Row.ProductsAmount) : "—";
    public string Total => Money.Format(Row.TotalAmount);
    public string Method => Row.Method.ToString();
    public string Operator => Row.Operator ?? "—";
}

/// <summary>All payments (sessions and counter sales) with a quick counter-sale button.</summary>
public sealed partial class SalesViewModel : PageViewModel
{
    private readonly IReportService _reports;
    private readonly SessionWorkflow _workflow;
    private readonly PrintService _print;
    private readonly FileDialogService _files;
    private List<PaymentRow> _rows = [];

    public SalesViewModel(IReportService reports, SessionWorkflow workflow, PrintService print, FileDialogService files, ToastService toasts) : base(toasts)
    {
        _reports = reports;
        _workflow = workflow;
        _print = print;
        _files = files;
    }

    public override string Title => "Sales";

    public ObservableCollection<PaymentRowView> Rows { get; } = [];

    [ObservableProperty] private string _period = "Today";
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private PaymentRowView? _selected;
    [ObservableProperty] private ReceiptPreview? _receipt;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _cashText = "";
    [ObservableProperty] private string _cardText = "";
    [ObservableProperty] private string _otherText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isEmpty;

    public bool IsCustom => Period == "Custom";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    partial void OnPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        _ = LoadAsync(ReloadAsync);
    }
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }

    private async Task ReloadAsync()
    {
        var (from, to) = Periods.Range(Period, DateTime.Today, CustomFrom, CustomTo);
        _rows = (await _reports.GetPaymentsAsync(from, to)).ToList();
        Rows.Clear();
        foreach (var r in _rows) Rows.Add(new PaymentRowView(r));
        TotalText = Money.Format(_rows.Sum(r => r.TotalAmount));
        CashText = Money.Format(_rows.Where(r => r.Method == PaymentMethod.Cash).Sum(r => r.TotalAmount));
        CardText = Money.Format(_rows.Where(r => r.Method == PaymentMethod.Card).Sum(r => r.TotalAmount));
        OtherText = Money.Format(_rows.Where(r => r.Method == PaymentMethod.Other).Sum(r => r.TotalAmount));
        CountText = $"{_rows.Count} payments";
        IsEmpty = _rows.Count == 0;
        Selected = Rows.FirstOrDefault();
    }

    async partial void OnSelectedChanged(PaymentRowView? value)
    {
        Receipt = null;
        if (value is null) return;
        await TryAsync(async () =>
        {
            var r = await _reports.GetReceiptAsync(value.Row.SessionId);
            Receipt = r is null ? null : new ReceiptPreview(r);
        });
    }

    [RelayCommand]
    private async Task NewSale()
    {
        await _workflow.CounterSaleAsync();
        await LoadAsync(ReloadAsync);
    }

    [RelayCommand]
    private void PrintReceipt()
    {
        if (Receipt is null) return;
        try { _print.PrintReceipt(Receipt.Receipt, choosePrinter: true); }
        catch (Exception ex) { Toasts.Error("Printing failed", ErrorText.For(ex)); }
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = _files.SaveCsv($"sales-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _rows, [
            new("Receipt", r => r.ReceiptNumber), new("Paid at", r => r.PaidAt), new("Station", r => r.StationName),
            new("Customer", r => r.CustomerName), new("Gaming", r => r.GamingAmount), new("Products", r => r.ProductsAmount),
            new("Total", r => r.TotalAmount), new("Method", r => r.Method), new("Operator", r => r.Operator),
        ]), "Export complete", path);
    }
}
