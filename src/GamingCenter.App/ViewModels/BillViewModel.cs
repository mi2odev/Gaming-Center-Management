using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed record BillArgs(int SessionId);

public sealed record BillLine(string Name, int Quantity, string Total);

/// <summary>Shared payment panel: method, amount received, change, receipt and customer options.</summary>
public abstract partial class PaymentDialogViewModel : DialogViewModel
{
    protected PaymentDialogViewModel(ISettingsService settings, ICustomerService customers, DialogService dialogs)
    {
        PrintReceipt = settings.Current.PrintReceiptByDefault;
        Customer = new CustomerPickerViewModel(customers, dialogs);
    }

    public CustomerPickerViewModel Customer { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(ConfirmText))]
    private PaymentMethod _method = PaymentMethod.Cash;

    [ObservableProperty] private string _receivedText = "";
    [ObservableProperty] private string _changeText = "0";
    [ObservableProperty] private bool _changeIsNegative;
    [ObservableProperty] private bool _printReceipt;
    [ObservableProperty] private bool _attachCustomer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText), nameof(TotalText))]
    private decimal _total;

    public bool IsCash => Method == PaymentMethod.Cash;
    public string TotalText => Money.Format(Total);
    public string ConfirmText => $"Confirm payment · {Money.Format(Total)}";
    public ObservableCollection<Preset> CashPresets { get; } = [];

    partial void OnReceivedTextChanged(string value) => UpdateChange();
    partial void OnTotalChanged(decimal value)
    {
        CashPresets.Clear();
        var candidates = new[] { value, Ceil(value, 100), Ceil(value, 500), Ceil(value, 1000), Ceil(value, 2000) }
            .Where(v => v >= value).Distinct().Take(4);
        foreach (var c in candidates) CashPresets.Add(new Preset((int)c, Money.Number(c)));
        UpdateChange();
    }

    private static decimal Ceil(decimal v, decimal step) => Math.Ceiling(v / step) * step;

    [RelayCommand]
    private void SetReceived(int amount) => ReceivedText = amount.ToString();

    private void UpdateChange()
    {
        if (!Money.TryParse(ReceivedText, out var received)) received = string.IsNullOrWhiteSpace(ReceivedText) ? Total : 0;
        var change = received - Total;
        ChangeIsNegative = change < 0;
        ChangeText = ChangeIsNegative ? $"Missing {Money.Format(-change)}" : Money.Format(change);
    }

    protected decimal ReceivedAmount() =>
        Money.TryParse(ReceivedText, out var r) ? r : Total;

    protected int? ChosenCustomerId(int? existing) => AttachCustomer ? Customer.SelectedId : existing;
}

/// <summary>Final bill and payment for a gaming session (design 1g).</summary>
public sealed partial class BillViewModel : PaymentDialogViewModel
{
    private readonly GamingSession _session;
    private readonly DateTime _end;
    private readonly ISessionService _sessions;
    private readonly IReportService _reports;
    private readonly LiveSessionStore _store;
    private readonly PrintService _print;
    private readonly ToastService _toasts;

    public BillViewModel(BillArgs args, LiveSessionStore store, ISessionService sessions, IReportService reports, ISettingsService settings,
        ICustomerService customers, DialogService dialogs, PrintService print, ToastService toasts) : base(settings, customers, dialogs)
    {
        _store = store;
        _sessions = sessions;
        _reports = reports;
        _print = print;
        _toasts = toasts;
        _session = store.ById(args.SessionId) ?? throw new InvalidOperationException("Session is not live.");
        // The bill is frozen at the moment it is opened.
        _end = _session.EndTime ?? DateTime.Now;

        var s = _session;
        var played = s.PlayedTime(_end);
        Header = $"CUSTOMER BILL · {DateTime.Now:yyyy-MMdd}";
        StationName = s.StationName;
        SessionLine = $"Session {s.StartTime:HH:mm} → {_end:HH:mm} · {s.Customer?.Name ?? "Walk-in"} · {ModeName(s)}";
        GamingDetail = Durations.Long(played);
        GamingRate = $"@ {Money.Rate(s.HourlyRate)} · {s.Rules.UnitLabel}" + s.Mode switch
        {
            SessionMode.FixedDuration => $" · {Durations.Minutes(s.PlannedMinutes ?? 0)} purchased",
            SessionMode.FixedBudget => $" · budget {Money.Format(s.Budget ?? 0)}",
            _ => "",
        };
        GamingTotal = s.GamingCost(_end);
        ProductsTotal = s.ProductsCost();
        foreach (var l in s.Products.OrderBy(p => p.AddedAt))
            Lines.Add(new BillLine(l.ProductName, l.Quantity, Money.Format(l.LineTotal)));
        PauseNote = s.Pauses.Count == 0 ? "" : $"Paused {Durations.Short(s.PausedTime(_end))} (not billed)";
        Total = GamingTotal + ProductsTotal;
        ReceivedText = Money.Number(Total).Replace(",", "");
        if (s.Customer is not null)
        {
            AttachCustomer = true;
            _ = Customer.SelectByIdAsync(s.CustomerId);
        }
    }

    private static string ModeName(GamingSession s) => s.Mode switch
    {
        SessionMode.FixedDuration => "Fixed duration",
        SessionMode.FixedBudget => "Fixed budget",
        _ => "Open mode",
    };

    public string Header { get; }
    public string StationName { get; }
    public string SessionLine { get; }
    public string GamingDetail { get; }
    public string GamingRate { get; }
    public decimal GamingTotal { get; }
    public decimal ProductsTotal { get; }
    public string GamingText => Money.Format(GamingTotal);
    public string ProductsText => Money.Format(ProductsTotal);
    public string PauseNote { get; }
    public ObservableCollection<BillLine> Lines { get; } = [];
    public bool HasLines => Lines.Count > 0;
    public string FooterNote => $"On confirm: payment recorded, stock already deducted, session closed, {StationName} becomes Available. Runs in one transaction.";

    [RelayCommand]
    private async Task Confirm()
    {
        Payment? payment = null;
        bool ok = await RunAsync(async () =>
        {
            payment = await _sessions.CompleteAsync(new CompleteSessionRequest(
                _session.Id, _end, Method, Method == PaymentMethod.Cash ? ReceivedAmount() : Total, ChosenCustomerId(null)));
        });
        if (!ok || payment is null) return;

        _store.Remove(_session.Id);
        _toasts.Success($"Payment confirmed · {Money.Format(payment.TotalAmount)}",
            payment.ChangeGiven > 0 ? $"Give change: {Money.Format(payment.ChangeGiven)} · {StationName} is available" : $"{StationName} is available");
        if (PrintReceipt) await PrintAsync(_session.Id);
        Close(true);
    }

    private async Task PrintAsync(int sessionId)
    {
        try
        {
            var receipt = await _reports.GetReceiptAsync(sessionId);
            if (receipt is not null) _print.PrintReceipt(receipt);
        }
        catch (Exception ex)
        {
            _toasts.Warning("Receipt not printed", ErrorText.For(ex) + " Payment is saved; reprint it from Sessions.");
        }
    }

    [RelayCommand]
    private void Back() => Close(false);
}

public sealed partial class CartLineViewModel(ProductDto product) : ObservableObject
{
    public ProductDto Product { get; } = product;
    public string Name => Product.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    private int _quantity = 1;

    public decimal Total => Product.SellingPrice * Quantity;
    public string TotalText => Money.Format(Total);
}

/// <summary>Product-only sale at the counter (Sales page).</summary>
public sealed partial class CounterSaleViewModel : PaymentDialogViewModel
{
    private readonly ISessionService _sessions;
    private readonly IReportService _reports;
    private readonly PrintService _print;
    private readonly ToastService _toasts;
    private readonly NotificationCenter _notifications;

    public CounterSaleViewModel(ISessionService sessions, IProductService products, IReportService reports, ISettingsService settings,
        ICustomerService customers, DialogService dialogs, PrintService print, ToastService toasts, NotificationCenter notifications)
        : base(settings, customers, dialogs)
    {
        _sessions = sessions;
        _reports = reports;
        _print = print;
        _toasts = toasts;
        _notifications = notifications;
        Catalog = new ProductCatalogViewModel(products);
        Catalog.Picked += (_, tile) => Add(tile.Product);
        _ = LoadAsync();
    }

    public ProductCatalogViewModel Catalog { get; }
    public ObservableCollection<CartLineViewModel> Cart { get; } = [];
    public bool IsCartEmpty => Cart.Count == 0;

    private async Task LoadAsync()
    {
        try { await Catalog.LoadAsync(); }
        catch (Exception ex) { Error = ErrorText.For(ex); }
    }

    private void Add(ProductDto p)
    {
        var line = Cart.FirstOrDefault(l => l.Product.Id == p.Id);
        int want = (line?.Quantity ?? 0) + 1;
        if (want > p.Stock) { Error = $"Only {p.Stock} × {p.Name} in stock."; return; }
        Error = null;
        if (line is null) Cart.Add(new CartLineViewModel(p));
        else line.Quantity = want;
        Sync();
    }

    [RelayCommand]
    private void Increase(CartLineViewModel line) => Add(line.Product);

    [RelayCommand]
    private void Decrease(CartLineViewModel line)
    {
        if (--line.Quantity <= 0) Cart.Remove(line);
        Sync();
    }

    private void Sync()
    {
        Total = Cart.Sum(l => l.Total);
        ReceivedText = Money.Number(Total).Replace(",", "");
        Catalog.SetQuantities(Cart.ToDictionary(l => l.Product.Id, l => l.Quantity));
        OnPropertyChanged(nameof(IsCartEmpty));
    }

    [RelayCommand]
    private async Task Confirm()
    {
        if (Cart.Count == 0) { Error = "Add at least one product."; return; }
        Payment? payment = null;
        bool ok = await RunAsync(async () =>
            payment = await _sessions.CounterSaleAsync(new CounterSaleRequest(
                Cart.Select(l => new CartLine(l.Product.Id, l.Quantity)).ToList(),
                Method, Method == PaymentMethod.Cash ? ReceivedAmount() : Total, ChosenCustomerId(null))));
        if (!ok || payment is null) return;

        _toasts.Success($"Sale recorded · {Money.Format(payment.TotalAmount)}",
            payment.ChangeGiven > 0 ? $"Give change: {Money.Format(payment.ChangeGiven)}" : $"Receipt #{payment.ReceiptNumber}");
        foreach (var l in Cart)
            _notifications.CheckStock(l.Product.Name, l.Product.Stock - l.Quantity, l.Product.MinStock);
        if (PrintReceipt)
        {
            try
            {
                var receipt = await _reports.GetReceiptAsync(payment.SessionId);
                if (receipt is not null) _print.PrintReceipt(receipt);
            }
            catch (Exception ex) { _toasts.Warning("Receipt not printed", ErrorText.For(ex)); }
        }
        Close(true);
    }
}
