using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Data;

/// <summary>Applies migrations and inserts first-run data. Seed data is for demonstration and fully editable.</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(GamingCenterDbContext db, bool seedDemoData, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        // WAL lets the UI read while a write transaction is in progress.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        var now = DateTime.Now;

        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.Add(NewUser("admin", "Administrator", "admin123", UserRole.Admin, now));
            if (seedDemoData)
                db.Users.Add(NewUser("operator", "Operator", "operator123", UserRole.Operator, now));
        }

        if (!await db.StationTypes.AnyAsync(ct))
        {
            db.StationTypes.AddRange(
                new GamingStationType { Name = "PlayStation", Tag = "PS", DefaultHourlyRate = 300, SortOrder = 1 },
                new GamingStationType { Name = "PC", Tag = "PC", DefaultHourlyRate = 250, SortOrder = 2 },
                new GamingStationType { Name = "Xbox", Tag = "XBOX", DefaultHourlyRate = 250, SortOrder = 3 },
                new GamingStationType { Name = "Nintendo", Tag = "NSW", DefaultHourlyRate = 150, SortOrder = 4 },
                new GamingStationType { Name = "Simulator", Tag = "SIM", DefaultHourlyRate = 500, SortOrder = 5 },
                new GamingStationType { Name = "Other", Tag = "GAME", DefaultHourlyRate = 200, SortOrder = 6 });
        }

        if (!await db.ProductCategories.AnyAsync(ct))
        {
            db.ProductCategories.AddRange(
                new ProductCategory { Name = "Drinks", SortOrder = 1 },
                new ProductCategory { Name = "Energy Drinks", SortOrder = 2 },
                new ProductCategory { Name = "Snacks", SortOrder = 3 },
                new ProductCategory { Name = "Food", SortOrder = 4 },
                new ProductCategory { Name = "Other", SortOrder = 5 });
        }

        await db.SaveChangesAsync(ct);

        if (seedDemoData && !await db.Stations.IgnoreQueryFilters().AnyAsync(ct))
        {
            var types = await db.StationTypes.ToDictionaryAsync(t => t.Name, ct);
            GamingStation S(string name, int number, string type, string brand, string model, decimal rate, string room, int? controllers, string description) => new()
            {
                Name = name, Number = number, StationTypeId = types[type].Id, Brand = brand, Model = model,
                HourlyRate = rate, Location = room, ControllerCount = controllers, Description = description,
                MaxControllers = controllers is 2 ? 4 : controllers,
                ExtraControllerRate = controllers is 2 && type is "PlayStation" or "Xbox" ? 100 : 0,
                State = StationState.Available, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
            db.Stations.AddRange(
                S("PS5 #01", 1, "PlayStation", "Sony", "PS5", 300, "Room A", 2, "Standard PS5 station, 4K TV"),
                S("PS5 #02", 2, "PlayStation", "Sony", "PS5", 300, "Room A", 2, "Standard PS5 station, 4K TV"),
                S("PS5 #03", 3, "PlayStation", "Sony", "PS5", 300, "Room A", 2, "Standard PS5 station"),
                S("PS4 #01", 1, "PlayStation", "Sony", "PS4", 200, "Room A", 2, "PS4 Pro"),
                S("PS5 #04", 4, "PlayStation", "Sony", "PS5", 300, "Room B", 2, "Standard PS5 station"),
                S("PC #01", 1, "PC", "Custom", "RTX 4070", 250, "Room B", null, "Gaming PC, 240 Hz monitor"),
                S("PC #02", 2, "PC", "Custom", "RTX 4070", 250, "Room B", null, "Gaming PC, 240 Hz monitor"),
                S("PS4 #02", 2, "PlayStation", "Sony", "PS4", 200, "Room B", 2, "PS4 Slim"),
                S("PS5 VIP #01", 1, "PlayStation", "Sony", "PS5", 350, "VIP Room", 4, "Premium PS5 station, sofa, 65\" TV"),
                S("PS5 VIP #02", 2, "PlayStation", "Sony", "PS5", 350, "VIP Room", 4, "Premium PS5 station"),
                S("Xbox #01", 1, "Xbox", "Microsoft", "Series X", 250, "Room A", 2, "Xbox Series X"),
                S("Switch #01", 1, "Nintendo", "Nintendo", "OLED", 150, "Room B", 2, "Nintendo Switch OLED"),
                S("Sim #01", 1, "Simulator", "Custom", "Rig v2", 500, "VIP Room", null, "Racing simulator with wheel and pedals"));
        }

        if (seedDemoData && !await db.Products.AnyAsync(ct))
        {
            var cats = await db.ProductCategories.ToDictionaryAsync(c => c.Name, ct);
            Product P(string name, string cat, decimal buy, decimal sell, int stock, int min) => new()
            {
                Name = name, CategoryId = cats[cat].Id, PurchasePrice = buy, SellingPrice = sell,
                Stock = stock, MinStock = min, IsActive = true, CreatedAt = now, UpdatedAt = now,
            };
            var products = new[]
            {
                P("Coca-Cola 33cl", "Drinks", 80, 150, 45, 10), P("Water 50cl", "Drinks", 25, 50, 40, 15),
                P("Pepsi 33cl", "Drinks", 80, 150, 30, 10), P("Fanta 33cl", "Drinks", 80, 150, 22, 10),
                P("Red Bull 25cl", "Energy Drinks", 200, 300, 18, 6), P("Monster 50cl", "Energy Drinks", 220, 350, 12, 6),
                P("Chips", "Snacks", 60, 100, 30, 10), P("Chocolate bar", "Snacks", 70, 120, 26, 8),
                P("Biscuits", "Snacks", 50, 90, 31, 8), P("Coffee", "Food", 30, 80, 120, 20),
                P("Sandwich", "Food", 150, 250, 12, 5), P("Croissant", "Food", 40, 80, 15, 5),
            };
            db.Products.AddRange(products);
            await db.SaveChangesAsync(ct);
            foreach (var p in products)
                db.StockMovements.Add(new StockMovement { ProductId = p.Id, Change = p.Stock, StockAfter = p.Stock, Reason = StockMovementReason.Initial, At = now });
        }

        await db.SaveChangesAsync(ct);
    }

    private static User NewUser(string username, string displayName, string password, UserRole role, DateTime now)
    {
        var (hash, salt) = PasswordHasher.Hash(password);
        return new User { Username = username, DisplayName = displayName, PasswordHash = hash, PasswordSalt = salt, Role = role, IsActive = true, CreatedAt = now };
    }
}
