using GamingCenter.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;

namespace GamingCenter.App.ViewModels;

public sealed partial class ConfirmViewModel(string title, string message, string confirmText, bool danger) : DialogViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDanger { get; } = danger;

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed partial class PromptViewModel(string title, string label, string? initial, string confirmText, string? placeholder) : DialogViewModel
{
    public string Title { get; } = title;
    public string Label { get; } = label;
    public string ConfirmText { get; } = confirmText;
    public string Placeholder { get; } = placeholder ?? "";

    [ObservableProperty] private string _value = initial ?? "";

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Value)) { Error = L.F("{0} is required.", Label); return; }
        Close(Value.Trim());
    }
}

public sealed record ReserveResult(string? Name, DateTime? At);

public sealed partial class ReserveViewModel(string stationName) : DialogViewModel
{
    public string Title => L.F("Reserve {0}", stationName);

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _time = DateTime.Now.AddMinutes(30 - DateTime.Now.Minute % 30).ToString("HH:mm");

    [RelayCommand]
    private void Confirm()
    {
        DateTime? at = null;
        if (!string.IsNullOrWhiteSpace(Time))
        {
            if (!TimeSpan.TryParse(Time, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) { Error = L.T("Enter a time like 20:00."); return; }
            at = DateTime.Today + t;
            if (at < DateTime.Now.AddMinutes(-5)) at = at.Value.AddDays(1);
        }
        Close(new ReserveResult(string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(), at));
    }
}

/// <summary>New customer, created inline from the start-session or bill dialogs.</summary>
public sealed partial class QuickCustomerViewModel(ICustomerService customers, string? initialName) : DialogViewModel
{
    [ObservableProperty] private string _name = LooksLikePhone(initialName) ? "" : initialName ?? "";
    [ObservableProperty] private string _phone = LooksLikePhone(initialName) ? initialName! : "";
    [ObservableProperty] private string _notes = "";

    private static bool LooksLikePhone(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().All(c => char.IsDigit(c) || c is ' ' or '+');

    [RelayCommand]
    private async Task Save()
    {
        CustomerDto? saved = null;
        if (await RunAsync(async () => saved = await customers.SaveAsync(new SaveCustomerRequest(null, Name, Phone, Notes))))
            Close(saved);
    }
}

/// <summary>Optional customer search-and-select used in several dialogs. Walk-in when nothing is selected.</summary>
public sealed partial class CustomerPickerViewModel(ICustomerService customers, DialogService dialogs) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<CustomerDto> Suggestions { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedLabel), nameof(DebtLabel))]
    private CustomerDto? _selected;

    public bool HasSelection => Selected is not null;
    public string SelectedLabel => Selected is null ? L.T("Walk-in") : string.IsNullOrWhiteSpace(Selected.Phone) ? Selected.Name : $"{Selected.Name} · {Selected.Phone}";
    public string? DebtLabel => Selected is { Balance: > 0 } c ? L.F("owes {0}", Money.Format(c.Balance)) : null;

    public int? SelectedId => Selected?.Id;

    async partial void OnSearchTextChanged(string value)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(value)) { Suggestions.Clear(); IsOpen = false; return; }
        try
        {
            await Task.Delay(200, cts.Token);
            var found = await customers.GetAllAsync(value, cts.Token);
            if (cts.IsCancellationRequested) return;
            Suggestions.Clear();
            foreach (var c in found.Take(8)) Suggestions.Add(c);
            IsOpen = true;
        }
        catch (OperationCanceledException) { }
    }

    public async Task SelectByIdAsync(int? id)
    {
        if (id is null) { Selected = null; return; }
        Selected = (await customers.GetAllAsync()).FirstOrDefault(c => c.Id == id);
    }

    [RelayCommand]
    private void Select(CustomerDto c)
    {
        Selected = c;
        IsOpen = false;
        _cts?.Cancel();
        SearchText = "";
    }

    [RelayCommand]
    private void Clear() => Selected = null;

    [RelayCommand]
    private async Task CreateNew()
    {
        IsOpen = false;
        var created = await dialogs.ShowAsync<CustomerDto>(new QuickCustomerViewModel(customers, SearchText));
        if (created is not null) Select(created);
    }
}

public sealed partial class ProductTileViewModel(ProductDto product) : ObservableObject
{
    public ProductDto Product { get; set; } = product;
    public int Id => Product.Id;
    public string Name => Product.Name;
    public string Category => Product.CategoryName;
    public string CategoryTag => Product.CategoryName.ToUpperInvariant();
    public string PriceText => Money.Format(Product.SellingPrice);
    public string? ImagePath => Product.ImagePath;
    public bool IsLow => Product.IsLowStock;
    public bool IsOut => Product.Stock <= 0;
    public string StockLabel => Product.Stock <= 0 ? L.T("Out of stock") : IsLow ? L.F("Low · {0} left", Product.Stock) : L.F("{0} in stock", Product.Stock);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InCart))]
    private int _quantity;

    public bool InCart => Quantity > 0;

    public void Replace(ProductDto p)
    {
        Product = p;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Product grid with category chips and search, used by the session drawer and counter sale.</summary>
public sealed partial class ProductCatalogViewModel(IProductService products) : ObservableObject
{
    private List<ProductTileViewModel> _all = [];
    private bool _rebuilding;

    public ObservableCollection<string> Categories { get; } = ["All"];
    public ObservableCollection<ProductTileViewModel> Visible { get; } = [];

    [ObservableProperty] private string _category = "All";
    [ObservableProperty] private string _search = "";

    public event EventHandler<ProductTileViewModel>? Picked;

    public async Task LoadAsync()
    {
        var list = await products.GetAllAsync(includeInactive: false);
        var keep = _all.ToDictionary(t => t.Id);
        _all = list.Select(p =>
        {
            if (keep.TryGetValue(p.Id, out var t)) { t.Replace(p); return t; }
            return new ProductTileViewModel(p);
        }).ToList();
        var cats = list.Select(p => p.CategoryName).Distinct().ToList();
        if (!Categories.Skip(1).SequenceEqual(cats))
        {
            var keepCategory = Category;
            _rebuilding = true;
            Categories.Clear();
            Categories.Add("All");
            foreach (var c in cats) Categories.Add(c);
            _rebuilding = false;
            Category = Categories.Contains(keepCategory) ? keepCategory : "All";
            OnPropertyChanged(nameof(Category)); // re-select the chip
        }
        Apply();
    }

    public void SetQuantities(IReadOnlyDictionary<int, int> qty)
    {
        foreach (var t in _all) t.Quantity = qty.GetValueOrDefault(t.Id);
    }

    public ProductTileViewModel? Find(int productId) => _all.FirstOrDefault(t => t.Id == productId);

    partial void OnCategoryChanged(string value)
    {
        // A chip list briefly reports null while its items are rebuilt.
        if (_rebuilding) return;
        if (value is null) { Category = "All"; return; }
        Apply();
    }
    partial void OnSearchChanged(string value) => Apply();

    private void Apply()
    {
        Visible.Clear();
        foreach (var t in _all.Where(t => (Category == "All" || t.Category == Category)
                                         && (string.IsNullOrWhiteSpace(Search) || t.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))))
            Visible.Add(t);
    }

    [RelayCommand]
    private void Pick(ProductTileViewModel tile)
    {
        if (tile.IsOut) return;
        Picked?.Invoke(this, tile);
    }
}
