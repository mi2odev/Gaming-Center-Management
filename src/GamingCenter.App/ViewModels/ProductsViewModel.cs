using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GamingCenter.App.Services;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;

namespace GamingCenter.App.ViewModels;

public sealed record ProductRow(ProductDto Product)
{
    public string Name => Product.Name;
    public string Category => Product.CategoryName;
    public string CategoryTag => Product.CategoryName.ToUpperInvariant();
    public string BuyText => Money.Format(Product.PurchasePrice);
    public string SellText => Money.Format(Product.SellingPrice);
    public string MarginText => (Product.Margin >= 0 ? "+" : "") + Money.Number(Product.Margin);
    public double StockFraction => Product.MinStock <= 0 ? (Product.Stock > 0 ? 1 : 0) : Math.Min(1.0, Product.Stock / (Product.MinStock * 4.0));
    public string StockText => $"{Product.Stock} / {Product.MinStock}";
    public string StockKey => Product.IsLowStock ? "Warning" : "Text.Muted";
    public string ToggleLabel => Product.IsActive ? "Disable" : "Enable";
    public bool IsInactive => !Product.IsActive;
}

/// <summary>Products &amp; stock (design 1i).</summary>
public sealed partial class ProductsViewModel : PageViewModel, INavigationTarget
{
    private readonly IProductService _products;
    private readonly IReportService _reports;
    private readonly IImageStore _images;
    private readonly FileDialogService _files;
    private readonly DialogService _dialogs;
    private List<ProductRow> _all = [];
    private bool _rebuilding;

    public ProductsViewModel(IProductService products, IReportService reports, IImageStore images, FileDialogService files,
        DialogService dialogs, ToastService toasts) : base(toasts)
    {
        _products = products;
        _reports = reports;
        _images = images;
        _files = files;
        _dialogs = dialogs;
    }

    public override string Title => "Products";

    public ObservableCollection<ProductRow> Rows { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["All"];

    [ObservableProperty] private string _category = "All";
    [ObservableProperty] private bool _lowStockOnly;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _revenueToday = "";
    [ObservableProperty] private string _profitToday = "";
    [ObservableProperty] private string _stockValue = "";
    [ObservableProperty] private string _belowMinimum = "";

    public override Task OnNavigatedToAsync() => LoadAsync(ReloadAsync);

    public void Apply(object parameter)
    {
        if (parameter is "lowstock") LowStockOnly = true;
        else if (parameter is string s && s.StartsWith("product:")) Search = s["product:".Length..];
    }

    private async Task ReloadAsync()
    {
        var list = await _products.GetAllAsync();
        _all = list.Select(p => new ProductRow(p)).ToList();
        var cats = await _products.GetCategoriesAsync();
        var keep = Category;
        _rebuilding = true;
        Categories.Clear();
        Categories.Add("All");
        foreach (var c in cats.Where(c => c.IsActive || c.ProductCount > 0)) Categories.Add(c.Name);
        _rebuilding = false;
        Category = Categories.Contains(keep) ? keep : "All";
        OnPropertyChanged(nameof(Category));

        var stats = await _reports.GetDashboardStatsAsync(DateTime.Today);
        RevenueToday = Money.Format(stats.ProductRevenue);
        ProfitToday = Money.Format(stats.ProductProfit);
        StockValue = Money.Format(list.Where(p => p.IsActive).Sum(p => p.PurchasePrice * p.Stock));
        int low = list.Count(p => p.IsActive && p.IsLowStock);
        BelowMinimum = $"{low} product{(low == 1 ? "" : "s")}";
        Summary = $"{list.Count} products · {cats.Count} categories";
        ApplyFilter();
    }

    partial void OnCategoryChanged(string value)
    {
        // A chip list briefly reports null while its items are rebuilt.
        if (_rebuilding) return;
        if (value is null) { Category = "All"; return; }
        ApplyFilter();
    }
    partial void OnLowStockOnlyChanged(bool value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all.Where(r => (Category == "All" || r.Category == Category)
                                         && (!LowStockOnly || (r.Product.IsLowStock && r.Product.IsActive))
                                         && (string.IsNullOrWhiteSpace(Search) || r.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))))
            Rows.Add(r);
    }

    private async Task<bool> EditorAsync(ProductDto? p)
    {
        var cats = await _products.GetCategoriesAsync();
        var saved = await _dialogs.ShowAsync<ProductDto>(new ProductEditorViewModel(p, cats, _products, _images, _files));
        if (saved is null) return false;
        Toasts.Success(p is null ? $"{saved.Name} added" : $"{saved.Name} saved");
        WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Products));
        await ReloadAsync();
        return true;
    }

    [RelayCommand] private Task Add() => EditorAsync(null);
    [RelayCommand] private Task Edit(ProductRow row) => EditorAsync(row.Product);

    [RelayCommand]
    private async Task AddStock(ProductRow? row)
    {
        var all = _all.Where(r => r.Product.IsActive).Select(r => r.Product).ToList();
        var dlg = new ReceiveStockViewModel(all, row?.Product, _products);
        if (await _dialogs.ShowAsync<bool>(dlg))
        {
            Toasts.Success("Stock updated");
            WeakReferenceMessenger.Default.Send(new DataChangedMessage(DataArea.Products));
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleActive(ProductRow row)
    {
        bool enable = !row.Product.IsActive;
        if (await TryAsync(() => _products.SetActiveAsync(row.Product.Id, enable), enable ? $"{row.Name} enabled" : $"{row.Name} disabled"))
            await ReloadAsync();
    }

    [RelayCommand]
    private async Task Delete(ProductRow row)
    {
        if (!await _dialogs.ConfirmAsync($"Delete {row.Name}?", "It disappears from the catalog. Past sales keep the product name and price.", "Delete", true)) return;
        if (await TryAsync(() => _products.DeleteAsync(row.Product.Id), $"{row.Name} deleted")) await ReloadAsync();
    }

    [RelayCommand]
    private async Task ManageCategories()
    {
        await _dialogs.ShowAsync(new CategoriesViewModel(_products, _dialogs));
        await ReloadAsync();
    }
}

public sealed partial class ProductEditorViewModel : DialogViewModel
{
    private readonly ProductDto? _existing;
    private readonly IProductService _products;
    private readonly IImageStore _images;
    private readonly FileDialogService _files;

    public ProductEditorViewModel(ProductDto? existing, IReadOnlyList<ProductCategoryDto> categories, IProductService products, IImageStore images, FileDialogService files)
    {
        _existing = existing;
        _products = products;
        _images = images;
        _files = files;
        foreach (var c in categories.Where(c => c.IsActive || c.Id == existing?.CategoryId)) Categories.Add(c);
        Category = Categories.FirstOrDefault(c => c.Id == existing?.CategoryId) ?? Categories.FirstOrDefault();
        Name = existing?.Name ?? "";
        Purchase = existing is null ? "" : Money.Number(existing.PurchasePrice).Replace(",", "");
        Selling = existing is null ? "" : Money.Number(existing.SellingPrice).Replace(",", "");
        Stock = existing?.Stock.ToString() ?? "0";
        MinStock = existing?.MinStock.ToString() ?? "5";
        ImagePath = existing?.ImagePath;
        IsActive = existing?.IsActive ?? true;
    }

    public string Title => _existing is null ? "Add product" : $"Edit {_existing.Name}";
    public ObservableCollection<ProductCategoryDto> Categories { get; } = [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private ProductCategoryDto? _category;
    [ObservableProperty] private string _purchase;
    [ObservableProperty] private string _selling;
    [ObservableProperty] private string _stock;
    [ObservableProperty] private string _minStock;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _profitText = "";

    partial void OnPurchaseChanged(string value) => UpdateProfit();
    partial void OnSellingChanged(string value) => UpdateProfit();

    private void UpdateProfit()
    {
        if (Money.TryParse(Purchase, out var b) && Money.TryParse(Selling, out var s))
            ProfitText = $"Profit per unit {Money.Format(s - b)}" + (s > 0 ? $" ({(s - b) / s * 100:0}% margin)" : "");
        else ProfitText = "";
    }

    [RelayCommand]
    private async Task ChooseImage()
    {
        var file = _files.OpenImage();
        if (file is null) return;
        await RunAsync(async () => ImagePath = await _images.ImportAsync(file, "products"));
    }

    [RelayCommand]
    private void RemoveImage() => ImagePath = null;

    [RelayCommand]
    private async Task Save()
    {
        if (Category is null) { Error = "Choose a category."; return; }
        if (!Money.TryParse(string.IsNullOrWhiteSpace(Purchase) ? "0" : Purchase, out var buy)) { Error = "Enter a valid purchase price."; return; }
        if (!Money.TryParse(Selling, out var sell)) { Error = "Enter a valid selling price."; return; }
        if (!int.TryParse(Stock, out var stock)) { Error = "Stock must be a whole number."; return; }
        if (!int.TryParse(MinStock, out var min)) { Error = "Minimum stock must be a whole number."; return; }
        ProductDto? saved = null;
        if (await RunAsync(async () => saved = await _products.SaveAsync(new SaveProductRequest(_existing?.Id, Name, Category.Id, buy, sell, stock, min, ImagePath, IsActive))))
            Close(saved);
    }
}

public sealed partial class ReceiveStockViewModel : DialogViewModel
{
    private readonly IProductService _products;

    public ReceiveStockViewModel(IReadOnlyList<ProductDto> products, ProductDto? selected, IProductService service)
    {
        _products = service;
        foreach (var p in products) Products.Add(p);
        Product = Products.FirstOrDefault(p => p.Id == selected?.Id) ?? Products.FirstOrDefault();
    }

    public ObservableCollection<ProductDto> Products { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStock))]
    private ProductDto? _product;

    [ObservableProperty] private string _quantity = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _setExact;

    public string CurrentStock => Product is null ? "" : $"Current stock: {Product.Stock} (minimum {Product.MinStock})";

    [RelayCommand]
    private async Task Save()
    {
        if (Product is null) { Error = "Choose a product."; return; }
        if (!int.TryParse(Quantity, out var q)) { Error = "Enter a whole number."; return; }
        if (await RunAsync(() => SetExact ? _products.AdjustStockAsync(Product.Id, q, string.IsNullOrWhiteSpace(Note) ? "Stock count" : Note)
                                          : _products.ReceiveStockAsync(Product.Id, q, Note)))
            Close(true);
    }
}

public sealed partial class CategoriesViewModel : DialogViewModel
{
    private readonly IProductService _products;
    private readonly DialogService _dialogs;

    public CategoriesViewModel(IProductService products, DialogService dialogs)
    {
        _products = products;
        _dialogs = dialogs;
        _ = LoadAsync();
    }

    public ObservableCollection<ProductCategoryDto> Categories { get; } = [];

    [ObservableProperty] private int? _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isActive = true;

    private Task LoadAsync() => RunAsync(async () =>
    {
        Categories.Clear();
        foreach (var c in await _products.GetCategoriesAsync()) Categories.Add(c);
    });

    [RelayCommand]
    private void Edit(ProductCategoryDto c)
    {
        EditId = c.Id;
        Name = c.Name;
        IsActive = c.IsActive;
    }

    [RelayCommand]
    private void New()
    {
        EditId = null;
        Name = "";
        IsActive = true;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (await RunAsync(() => _products.SaveCategoryAsync(EditId, Name, IsActive)))
        {
            New();
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task Delete(ProductCategoryDto c)
    {
        if (!await _dialogs.ConfirmAsync($"Delete category {c.Name}?", "Only empty categories can be deleted.", "Delete", true)) return;
        if (await RunAsync(() => _products.DeleteCategoryAsync(c.Id))) await LoadAsync();
    }
}
