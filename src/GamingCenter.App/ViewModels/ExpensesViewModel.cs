using GamingCenter.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.ViewModels;

public sealed record ExpenseCategoryOption(ExpenseCategory Value)
{
    public string Name => L.T(Value.ToString());
    public static IReadOnlyList<ExpenseCategoryOption> All { get; } = Enum.GetValues<ExpenseCategory>().Select(c => new ExpenseCategoryOption(c)).ToList();
}

public sealed record ExpenseRow(ExpenseDto Dto)
{
    public string Date => Dto.Date.ToString("dd/MM/yyyy");
    public string Description => Dto.Description;
    public string Note => Dto.Note ?? "";
    public string Category => L.T(Dto.Category.ToString());
    public ExpenseCategory CategoryValue => Dto.Category;
    public string User => Dto.User ?? "—";
    /// <summary>"Added by Karim · Shop name, warranty until 2028"</summary>
    public string Meta => L.F("Added by {0}", User) + (string.IsNullOrWhiteSpace(Dto.Note) ? "" : " · " + Dto.Note);
    public string Amount => "−" + Money.Format(Dto.Amount);
}

/// <summary>One category in the breakdown: its total, share of all spending and number of expenses.</summary>
public sealed record ExpenseCategoryShare(ExpenseCategory Category, string Value, double Fraction, string Sub)
{
    public string Name => L.T(Category.ToString());
}

/// <summary>Expenses page: what the owner spent on the center (a new TV, rent, bills…) and the profit left after it.</summary>
public sealed partial class ExpensesViewModel : PageViewModel
{
    private readonly IExpenseService _expenses;
    private readonly IReportService _reports;
    private readonly DialogService _dialogs;
    private readonly FileDialogService _files;
    private List<ExpenseDto> _all = [];

    public ExpensesViewModel(IExpenseService expenses, IReportService reports, DialogService dialogs, FileDialogService files, ToastService toasts) : base(toasts)
    {
        _expenses = expenses;
        _reports = reports;
        _dialogs = dialogs;
        _files = files;
    }

    public override string Title => L.T("Expenses");

    public ObservableCollection<ExpenseRow> Rows { get; } = [];
    public ObservableCollection<ExpenseCategoryShare> Categories { get; } = [];

    [ObservableProperty] private string _period = UiState.Get("Expenses.Period", "Month");
    [ObservableProperty] private DateTime? _customFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? _customTo = DateTime.Today;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private bool _netNegative;
    [ObservableProperty] private double _spentShare;
    [ObservableProperty] private string _spentShareText = "";
    [ObservableProperty] private bool _isEmpty;

    public bool IsCustom => Period == "Custom";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    partial void OnPeriodChanged(string value)
    {
        UiState.Set("Expenses.Period", value);
        OnPropertyChanged(nameof(IsCustom));
        _ = LoadAsync(ReloadAsync);
    }
    partial void OnCustomFromChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnCustomToChanged(DateTime? value) { if (IsCustom) _ = LoadAsync(ReloadAsync); }
    partial void OnSearchChanged(string value) => ApplyFilter();

    private async Task ReloadAsync()
    {
        var (from, to) = Periods.Range(Period, DateTime.Today, CustomFrom, CustomTo);
        _all = (await _expenses.GetAsync(from, to)).ToList();
        var income = await _reports.GetIncomeAsync(from, to);
        var spent = _all.Sum(e => e.Amount);
        TotalText = Money.Format(spent);
        CountText = L.F(_all.Count == 1 ? "{0} expense" : "{0} expenses", _all.Count);
        IncomeText = Money.Format(income);
        NetText = Money.Format(income - spent);
        NetNegative = income - spent < 0;
        SpentShare = income > 0 ? (double)Math.Min(1, spent / income) : spent > 0 ? 1 : 0;
        SpentShareText = income > 0 ? L.F("{0} of money received spent", $"{spent / income:P0}") : spent > 0 ? L.T("No money received yet") : L.T("Nothing spent yet.");

        Categories.Clear();
        var groups = _all.GroupBy(e => e.Category).Select(g => (Category: g.Key, Amount: g.Sum(e => e.Amount), Count: g.Count()))
            .OrderByDescending(g => g.Amount).ToList();
        var max = groups.Count == 0 ? 0 : groups.Max(g => g.Amount);
        foreach (var g in groups)
            Categories.Add(new ExpenseCategoryShare(g.Category, Money.Number(g.Amount), max > 0 ? (double)(g.Amount / max) : 0,
                $"{(spent > 0 ? g.Amount / spent : 0):P0} · " + L.F(g.Count == 1 ? "{0} expense" : "{0} expenses", g.Count)));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var t = Search.Trim();
        Rows.Clear();
        foreach (var e in _all.Where(e => t.Length == 0 || e.Description.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                                          || (e.Note?.Contains(t, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                          || L.T(e.Category.ToString()).Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            Rows.Add(new ExpenseRow(e));
        IsEmpty = Rows.Count == 0;
    }

    [RelayCommand]
    private Task Add() => EditorAsync(null);

    [RelayCommand]
    private Task Edit(ExpenseRow row) => EditorAsync(row.Dto);

    private async Task EditorAsync(ExpenseDto? existing)
    {
        if (await _dialogs.ShowAsync<ExpenseDto>(new ExpenseEditorViewModel(existing, _expenses)) is { } saved)
        {
            Toasts.Success(existing is null ? L.T("Expense added") : L.T("Expense updated"), $"{saved.Description} · {Money.Format(saved.Amount)}");
            await LoadAsync(ReloadAsync);
        }
    }

    [RelayCommand]
    private async Task Delete(ExpenseRow row)
    {
        if (!await _dialogs.ConfirmAsync(L.F("Delete {0}?", row.Description),
                L.F("{0} will no longer be subtracted from your profit.", Money.Format(row.Dto.Amount)), L.T("Delete"), true)) return;
        if (await TryAsync(() => _expenses.DeleteAsync(row.Dto.Id), L.T("Expense deleted"))) await LoadAsync(ReloadAsync);
    }

    [RelayCommand]
    private async Task Export()
    {
        var path = _files.SaveCsv($"expenses-{DateTime.Today:yyyyMMdd}.csv");
        if (path is null) return;
        await TryAsync(() => CsvWriter.WriteAsync(path, _all, [
            new("Date", e => e.Date), new("Description", e => e.Description), new("Category", e => e.Category.ToString()),
            new("Amount", e => e.Amount), new("Note", e => e.Note), new("Added by", e => e.User),
        ]), "Export complete", path);
    }
}

/// <summary>Add or edit one expense: what was bought, the price paid, the category and the day.</summary>
public sealed partial class ExpenseEditorViewModel : DialogViewModel
{
    private readonly ExpenseDto? _existing;
    private readonly IExpenseService _expenses;

    public ExpenseEditorViewModel(ExpenseDto? existing, IExpenseService expenses)
    {
        _existing = existing;
        _expenses = expenses;
        _description = existing?.Description ?? "";
        _amountText = existing is null ? "" : Money.Number(existing.Amount).Replace(",", "");
        _category = ExpenseCategoryOption.All.First(c => c.Value == (existing?.Category ?? ExpenseCategory.Equipment));
        _date = existing?.Date.Date ?? DateTime.Today;
        _note = existing?.Note ?? "";
    }

    public string Title => _existing is null ? L.T("Add expense") : L.T("Edit expense");
    public IReadOnlyList<ExpenseCategoryOption> Categories => ExpenseCategoryOption.All;
    public DateTime Today => DateTime.Today;

    [ObservableProperty] private string _description;
    [ObservableProperty] private string _amountText;
    [ObservableProperty] private ExpenseCategoryOption _category;
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _note;

    [RelayCommand]
    private async Task Save()
    {
        if (string.IsNullOrWhiteSpace(Description)) { Error = L.T("Write what you bought, e.g. \"New TV for PS5 #02\"."); return; }
        if (!Money.TryParse(AmountText, out var amount) || amount <= 0) { Error = L.T("Enter the price paid."); return; }
        if (Date is not { } day) { Error = L.T("Choose the day of the expense."); return; }
        // Keep the original time when the day did not change, so the list order stays the same.
        var date = _existing is not null && _existing.Date.Date == day.Date ? _existing.Date : day.Date;
        ExpenseDto? saved = null;
        if (await RunAsync(async () => saved = await _expenses.SaveAsync(
                new SaveExpenseRequest(_existing?.Id, date, Description, Category.Value, amount, Note))))
            Close(saved);
    }
}
