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

public sealed record CreditRow(CreditBalanceDto Dto)
{
    public int CustomerId => Dto.CustomerId;
    public string Name => Dto.Name;
    public string Phone => Dto.Phone ?? "—";
    public string Balance => Money.Format(Dto.Balance);
    public bool Owes => Dto.Balance > 0;
    public string LastCredit => Dto.LastCreditAt?.ToString("dd/MM/yyyy") ?? "—";
    public string LastRepayment => Dto.LastRepaymentAt?.ToString("dd/MM/yyyy") ?? "—";
    public string Bills => Dto.OpenBills.ToString();
    public string Initials => string.Concat(Dto.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public string Age => Dto.LastCreditAt is { } d && Dto.Balance > 0 ? $"{(int)(DateTime.Today - d.Date).TotalDays} days" : "—";
}

public sealed record CreditEntryRow(CreditEntryDto Dto)
{
    public string Date => Dto.At.ToString("dd/MM/yyyy HH:mm");
    public string What => Dto.Kind switch
    {
        CreditKind.UnpaidBill => Dto.ReceiptNumber is { } r ? L.F("Unpaid bill #{0}", r) : L.T("Unpaid bill"),
        CreditKind.Repayment => L.F("Paid back · {0}", L.T(Dto.Method?.ToString() ?? "")),
        _ => Dto.Amount >= 0 ? L.T("Added by hand") : L.T("Reduced by hand"),
    };
    public string Note => Dto.Note ?? "";
    public string Amount => (Dto.Amount > 0 ? "+" : "−") + Money.Format(Math.Abs(Dto.Amount));
    public bool IsDebt => Dto.Amount > 0;
    public string BalanceAfter => Money.Format(Dto.BalanceAfter);
    public string User => Dto.User ?? "";
}

/// <summary>Credits page: customers who still owe money, their history, and repayments.</summary>
public sealed partial class CreditsViewModel : PageViewModel, INavigationTarget
{
    private readonly ICreditService _credits;
    private readonly ICustomerService _customers;
    private readonly DialogService _dialogs;
    private readonly CurrentUserService _user;
    private readonly FileDialogService _files;
    private readonly SessionWorkflow _workflow;
    private List<CreditBalanceDto> _all = [];
    private int? _pendingSelect;

    public CreditsViewModel(ICreditService credits, ICustomerService customers, DialogService dialogs, CurrentUserService user,
        FileDialogService files, SessionWorkflow workflow, ToastService toasts) : base(toasts)
    {
        _workflow = workflow;
        _credits = credits;
        _customers = customers;
        _dialogs = dialogs;
        _user = user;
        _files = files;
    }

    public override string Title => L.T("Credits");
    public bool IsAdmin => _user.IsAdmin;

    public ObservableCollection<CreditRow> Rows { get; } = [];
    public ObservableCollection<CreditEntryRow> History { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showSettled;
    [ObservableProperty] private CreditRow? _selected;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _oldestText = "";
    [ObservableProperty] private bool _isEmpty;

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    public void Apply(object parameter)
    {
        if (parameter is int customerId)
        {
            _pendingSelect = customerId;
            ShowSettled = true;
            Selected = Rows.FirstOrDefault(r => r.CustomerId == customerId);
        }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnShowSettledChanged(bool value) => _ = LoadAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        _all = (await _credits.GetBalancesAsync(ShowSettled)).ToList();
        var owing = _all.Where(b => b.Balance > 0).ToList();
        TotalText = Money.Format(owing.Sum(b => b.Balance));
        CountText = L.F(owing.Count == 1 ? "{0} customer owes money" : "{0} customers owe money", owing.Count);
        var oldest = owing.Where(b => b.LastCreditAt is not null).OrderBy(b => b.LastCreditAt).FirstOrDefault();
        OldestText = oldest is null ? "—" : oldest.Name + " · " + L.F("since {0}", oldest.LastCreditAt?.ToString("dd/MM"));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keep = _pendingSelect ?? Selected?.CustomerId;
        _pendingSelect = null;
        var t = Search.Trim();
        Rows.Clear();
        foreach (var b in _all.Where(b => t.Length == 0 || b.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) || (b.Phone?.Contains(t) ?? false)))
            Rows.Add(new CreditRow(b));
        IsEmpty = Rows.Count == 0;
        Selected = Rows.FirstOrDefault(r => r.CustomerId == keep) ?? Rows.FirstOrDefault();
    }

    async partial void OnSelectedChanged(CreditRow? value)
    {
        History.Clear();
        if (value is null) return;
        await TryAsync(async () =>
        {
            foreach (var e in await _credits.GetHistoryAsync(value.CustomerId)) History.Add(new CreditEntryRow(e));
        });
    }

    [RelayCommand]
    private async Task RecordPayment()
    {
        if (Selected is not { Owes: true } row) { Toasts.Info("Select a customer who owes money."); return; }
        var dlg = new RepaymentViewModel(row.Dto, _credits);
        if (await _dialogs.ShowAsync<CreditEntryDto>(dlg) is { } entry)
        {
            Toasts.Success($"{row.Name} paid {Money.Format(-entry.Amount)}",
                entry.BalanceAfter > 0 ? $"Still owes {Money.Format(entry.BalanceAfter)}" : "Account settled");
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Customers));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task AddDebt()
    {
        var dlg = new ManualCreditViewModel(Selected?.CustomerId, IsAdmin, _credits, _customers, _dialogs);
        if (await _dialogs.ShowAsync<CreditEntryDto>(dlg) is not null)
        {
            _pendingSelect = dlg.CustomerId;
            Toasts.Success("Credit updated");
            await ReloadAsync();
        }
    }

    /// <summary>Sell drinks/food to the selected customer and put it on their account (no session needed).</summary>
    [RelayCommand]
    private async Task ChargeProducts()
    {
        await _workflow.CounterSaleAsync(Selected?.CustomerId);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = _files.SaveCsv($"credits-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _all, [
            new("Customer", b => b.Name), new("Phone", b => b.Phone), new("Balance", b => b.Balance),
            new("Unpaid bills", b => b.OpenBills), new("Last credit", b => b.LastCreditAt), new("Last repayment", b => b.LastRepaymentAt),
        ]), "Export complete", path);
    }
}

/// <summary>Customer pays back all or part of what they owe.</summary>
public sealed partial class RepaymentViewModel(CreditBalanceDto customer, ICreditService credits) : DialogViewModel
{
    public string Title => $"{customer.Name} pays back";
    public string OwesText => $"Owes {Money.Format(customer.Balance)}";

    [ObservableProperty] private string _amountText = Money.Number(customer.Balance).Replace(",", "");
    [ObservableProperty] private PaymentMethod _method = PaymentMethod.Cash;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _afterText = "Account will be settled";

    partial void OnAmountTextChanged(string value)
    {
        AfterText = Money.TryParse(value, out var a) && a > 0
            ? a >= customer.Balance ? "Account will be settled" : $"Will still owe {Money.Format(customer.Balance - a)}"
            : "Enter an amount";
    }

    [RelayCommand]
    private void All() => AmountText = Money.Number(customer.Balance).Replace(",", "");

    [RelayCommand]
    private async Task Save()
    {
        if (!Money.TryParse(AmountText, out var amount) || amount <= 0) { Error = "Enter the amount paid back."; return; }
        CreditEntryDto? entry = null;
        if (await RunAsync(async () => entry = await credits.RecordRepaymentAsync(customer.CustomerId, amount, Method, Note)))
            Close(entry);
    }
}

/// <summary>Admin: add an old debt by hand, or reduce/forgive one.</summary>
public sealed partial class ManualCreditViewModel : DialogViewModel
{
    private readonly ICreditService _credits;

    public ManualCreditViewModel(int? customerId, bool canReduce, ICreditService credits, ICustomerService customers, DialogService dialogs)
    {
        _credits = credits;
        CanReduce = canReduce;
        Customer = new CustomerPickerViewModel(customers, dialogs);
        if (customerId is not null) _ = Customer.SelectByIdAsync(customerId);
    }

    /// <summary>Operators can only add a charge; reducing or forgiving a debt is for admins.</summary>
    public bool CanReduce { get; }

    public CustomerPickerViewModel Customer { get; }
    public int? CustomerId => Customer.SelectedId;

    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private bool _reduce;
    [ObservableProperty] private string _note = "";

    [RelayCommand]
    private async Task Save()
    {
        if (Customer.SelectedId is not { } id) { Error = "Choose or create the customer."; return; }
        if (!Money.TryParse(AmountText, out var amount) || amount <= 0) { Error = L.T("Enter an amount."); return; }
        if (string.IsNullOrWhiteSpace(Note)) { Error = L.T("Write what it is for, e.g. \"Sandwich and coffee\"."); return; }
        CreditEntryDto? entry = null;
        if (await RunAsync(async () => entry = await _credits.AddManualAsync(id, Reduce && CanReduce ? -amount : amount, Note)))
            Close(entry);
    }
}
