using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

public sealed class ProductService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), IProductService
{
    private static ProductDto ToDto(Product p) => new(
        p.Id, p.Name, p.CategoryId, p.Category?.Name ?? "", p.PurchasePrice, p.SellingPrice, p.Stock, p.MinStock, p.ImagePath, p.IsActive);

    public async Task<IReadOnlyList<ProductDto>> GetAllAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Products.AsNoTracking().Include(p => p.Category).Where(p => !p.IsDeleted);
        if (!includeInactive) q = q.Where(p => p.IsActive);
        var list = await q.ToListAsync(ct);
        return list.OrderBy(p => p.Category?.SortOrder ?? 99).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ProductDto>> GetLowStockAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var list = await db.Products.AsNoTracking().Include(p => p.Category)
            .Where(p => !p.IsDeleted && p.IsActive && p.Stock <= p.MinStock).ToListAsync(ct);
        return list.OrderBy(p => p.Stock).Select(ToDto).ToList();
    }

    public async Task<ProductDto> SaveAsync(SaveProductRequest r, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = Required(r.Name, "Product name", 128);
        if (r.PurchasePrice < 0 || r.SellingPrice < 0) throw new BusinessException("Prices cannot be negative.");
        if (r.SellingPrice > 10_000_000) throw new BusinessException("Selling price is too large.");
        if (r.Stock < 0) throw new BusinessException("Stock cannot be negative.");
        if (r.MinStock < 0) throw new BusinessException("Minimum stock cannot be negative.");

        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ProductCategories.AnyAsync(c => c.Id == r.CategoryId, ct)) throw new BusinessException("Choose a category.");
        if (await db.Products.AnyAsync(p => !p.IsDeleted && p.Id != (r.Id ?? 0) && p.Name.ToLower() == name.ToLower(), ct))
            throw new BusinessException($"A product named \"{name}\" already exists.");

        var now = Clock.Now;
        Product p;
        int stockChange;
        if (r.Id is { } id)
        {
            p = await db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Product not found.");
            stockChange = r.Stock - p.Stock;
        }
        else
        {
            p = new Product { CreatedAt = now };
            db.Products.Add(p);
            stockChange = r.Stock;
        }

        p.Name = name;
        p.CategoryId = r.CategoryId;
        p.PurchasePrice = r.PurchasePrice;
        p.SellingPrice = r.SellingPrice;
        p.Stock = r.Stock;
        p.MinStock = r.MinStock;
        p.ImagePath = Optional(r.ImagePath, 260);
        p.IsActive = r.IsActive;
        p.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        if (stockChange != 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                ProductId = p.Id, Change = stockChange, StockAfter = p.Stock,
                Reason = r.Id is null ? StockMovementReason.Initial : StockMovementReason.Adjustment,
                UserId = CurrentUserId, At = now, Note = r.Id is null ? null : "Edited in product form",
            });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        var saved = await db.Products.AsNoTracking().Include(x => x.Category).FirstAsync(x => x.Id == p.Id, ct);
        return ToDto(saved);
    }

    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Product not found.");
        p.IsActive = active;
        p.UpdatedAt = Clock.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Product not found.");
        p.IsDeleted = true;
        p.IsActive = false;
        p.UpdatedAt = Clock.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReceiveStockAsync(int productId, int quantity, string? note, CancellationToken ct = default)
    {
        RequireAdmin();
        if (quantity <= 0) throw new BusinessException("Quantity must be at least 1.");
        if (quantity > 100_000) throw new BusinessException("Quantity is too large.");
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted, ct) ?? throw new BusinessException("Product not found.");
        p.Stock += quantity;
        p.UpdatedAt = Clock.Now;
        db.StockMovements.Add(new StockMovement
        {
            ProductId = p.Id, Change = quantity, StockAfter = p.Stock, Reason = StockMovementReason.Restock,
            UserId = CurrentUserId, At = Clock.Now, Note = Optional(note, 256),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task AdjustStockAsync(int productId, int newStock, string? note, CancellationToken ct = default)
    {
        RequireAdmin();
        if (newStock < 0) throw new BusinessException("Stock cannot be negative.");
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted, ct) ?? throw new BusinessException("Product not found.");
        int change = newStock - p.Stock;
        if (change == 0) return;
        p.Stock = newStock;
        p.UpdatedAt = Clock.Now;
        db.StockMovements.Add(new StockMovement
        {
            ProductId = p.Id, Change = change, StockAfter = p.Stock, Reason = StockMovementReason.Adjustment,
            UserId = CurrentUserId, At = Clock.Now, Note = Optional(note, 256),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ProductCategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var counts = await db.Products.Where(p => !p.IsDeleted).GroupBy(p => p.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var cats = await db.ProductCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        return cats.Select(c => new ProductCategoryDto(c.Id, c.Name, c.SortOrder, c.IsActive, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<ProductCategoryDto> SaveCategoryAsync(int? id, string name, bool isActive, CancellationToken ct = default)
    {
        RequireAdmin();
        name = Required(name, "Category name", 64);
        await using var db = await OpenAsync(ct);
        if (await db.ProductCategories.AnyAsync(c => c.Id != (id ?? 0) && c.Name.ToLower() == name.ToLower(), ct))
            throw new BusinessException($"A category named \"{name}\" already exists.");
        ProductCategory cat;
        if (id is { } existing)
            cat = await db.ProductCategories.FirstOrDefaultAsync(c => c.Id == existing, ct) ?? throw new BusinessException("Category not found.");
        else
        {
            cat = new ProductCategory { SortOrder = (await db.ProductCategories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 1 };
            db.ProductCategories.Add(cat);
        }
        cat.Name = name;
        cat.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        return (await GetCategoriesAsync(ct)).First(c => c.Id == cat.Id);
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var cat = await db.ProductCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new BusinessException("Category not found.");
        if (await db.Products.AnyAsync(p => p.CategoryId == id, ct))
            throw new BusinessException("This category has products (including deleted ones). Deactivate it instead.");
        db.ProductCategories.Remove(cat);
        await db.SaveChangesAsync(ct);
    }
}
