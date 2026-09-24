using System.Globalization;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

/// <summary>
/// Read-only reporting. Rows are filtered by indexed date columns in SQL and aggregated in memory,
/// because SQLite stores decimals as TEXT and cannot sum them exactly.
/// </summary>
public sealed class ReportService(
    IDbContextFactory<GamingCenterDbContext> dbFactory,
    IClock clock,
    ICurrentUser currentUser,
    ISettingsService settings,
    IProductService products) : ServiceBase(dbFactory, clock, currentUser), IReportService
{
    private async Task<List<Payment>> LoadPaymentsAsync(GamingCenterDbContext db, DateTime from, DateTime to, CancellationToken ct) =>
        await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= from && p.PaidAt < to)
            .Include(p => p.Session!).ThenInclude(s => s.Products)
            .Include(p => p.Session!).ThenInclude(s => s.Customer)
            .Include(p => p.User)
            .AsSplitQuery()
            .ToListAsync(ct);

    public async Task<DashboardStats> GetDashboardStatsAsync(DateTime day, CancellationToken ct = default)
    {
        var from = day.Date;
        await using var db = await OpenAsync(ct);
        var today = await LoadPaymentsAsync(db, from, from.AddDays(1), ct);
        var yesterday = await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= from.AddDays(-1) && p.PaidAt < from)
            .Select(p => p.TotalAmount).ToListAsync(ct);

        var gamingSessions = today.Where(p => p.Session!.Mode != SessionMode.CounterSale).Select(p => p.Session!).ToList();
        var lines = today.SelectMany(p => p.Session!.Products).ToList();

        var topStation = gamingSessions.GroupBy(s => s.StationName)
            .Select(g => (Name: g.Key, Seconds: g.Sum(s => s.PlayedSeconds)))
            .OrderByDescending(x => x.Seconds).FirstOrDefault();
        var topProduct = lines.GroupBy(l => l.ProductName)
            .Select(g => (Name: g.Key, Units: g.Sum(l => l.Quantity)))
            .OrderByDescending(x => x.Units).FirstOrDefault();

        return new DashboardStats(
            Revenue: today.Sum(p => p.TotalAmount),
            RevenueYesterday: yesterday.Sum(),
            GamingRevenue: today.Sum(p => p.GamingAmount),
            ProductRevenue: today.Sum(p => p.ProductsAmount),
            ProductProfit: lines.Sum(l => l.LineProfit),
            Sessions: gamingSessions.Count,
            AverageSession: gamingSessions.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(gamingSessions.Average(s => s.PlayedSeconds)),
            MostUsedStation: topStation.Name,
            MostUsedStationTime: TimeSpan.FromSeconds(topStation.Seconds),
            MostSoldProduct: topProduct.Name,
            MostSoldProductUnits: topProduct.Units,
            LowStock: await products.GetLowStockAsync(ct));
    }

    public async Task<IReadOnlyList<HistoryRow>> GetHistoryAsync(DateTime from, DateTime to, string? search = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => (s.Status == SessionStatus.Completed || s.Status == SessionStatus.Cancelled)
                        && (s.EndTime ?? s.StartTime) >= from && (s.EndTime ?? s.StartTime) < to)
            .Include(s => s.Customer)
            .Include(s => s.Payment!).ThenInclude(p => p.User)
            .Include(s => s.StartedBy)
            .OrderByDescending(s => s.EndTime ?? s.StartTime)
            .ToListAsync(ct);

        IEnumerable<GamingSession> filtered = sessions;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            filtered = sessions.Where(s =>
                s.StationName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (s.Customer?.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Customer?.Phone?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Payment?.ReceiptNumber.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered.Select(s => new HistoryRow(
            s.Id, s.Payment?.ReceiptNumber, s.StationName, s.Customer?.Name ?? "Walk-in", s.Mode, s.Status,
            s.StartTime, s.EndTime, s.PlayedSeconds, s.GamingTotal, s.ProductsTotal, s.Total,
            s.Payment?.Method, s.Payment?.PaidAt, s.Payment?.User?.DisplayName ?? s.StartedBy?.DisplayName)).ToList();
    }

    public async Task<ReceiptDto?> GetReceiptAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var s = await db.Sessions.AsNoTracking()
            .Include(x => x.Products).Include(x => x.Pauses).Include(x => x.Customer)
            .Include(x => x.Payment!).ThenInclude(p => p.User)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s?.Payment is not { } pay) return null;

        var cfg = settings.Current;
        return new ReceiptDto(
            s.Id, pay.ReceiptNumber, pay.PaidAt, cfg.CenterName, cfg.Address, cfg.Phone,
            s.StationName, cfg.ShowCustomerOnReceipt ? s.Customer?.Name : null, s.Mode,
            s.StartTime, s.EndTime, s.PlayedSeconds, s.HourlyRate, s.Rules.UnitLabel,
            s.GamingTotal,
            s.Products.OrderBy(p => p.AddedAt).Select(p => new ReceiptLine(p.ProductName, p.Quantity, p.UnitPrice, p.LineTotal)).ToList(),
            s.ProductsTotal, s.Total, pay.Method, pay.AmountReceived, pay.ChangeGiven, pay.User?.DisplayName,
            s.Pauses.OrderBy(p => p.StartTime).Select(p => (p.StartTime, p.EndTime)).ToList(),
            cfg.ReceiptFooter);
    }

    public async Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var list = await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= from && p.PaidAt < to)
            .Include(p => p.Session!).ThenInclude(s => s.Customer)
            .Include(p => p.User)
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync(ct);
        return list.Select(p => new PaymentRow(p.Id, p.SessionId, p.ReceiptNumber, p.PaidAt, p.Session!.StationName,
            p.Session.Customer?.Name ?? "Walk-in", p.GamingAmount, p.ProductsAmount, p.TotalAmount, p.Method, p.User?.DisplayName)).ToList();
    }

    public async Task<ReportData> GetReportAsync(DateTime from, DateTime to, bool groupByMonth, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var payments = await LoadPaymentsAsync(db, from, to, ct);
        var span = to - from;
        var previous = await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= from - span && p.PaidAt < from)
            .Select(p => p.TotalAmount).ToListAsync(ct);

        var sessions = payments.Select(p => p.Session!).ToList();
        var gaming = sessions.Where(s => s.Mode != SessionMode.CounterSale).ToList();
        var lines = sessions.SelectMany(s => s.Products).ToList();

        var perDay = new List<DayRevenue>();
        var culture = CultureInfo.GetCultureInfo("en-US");
        if (groupByMonth)
        {
            for (var m = new DateTime(from.Year, from.Month, 1); m < to; m = m.AddMonths(1))
            {
                var bucket = payments.Where(p => p.PaidAt.Year == m.Year && p.PaidAt.Month == m.Month).ToList();
                perDay.Add(new DayRevenue(m, m.ToString("MMM", culture), bucket.Sum(p => p.GamingAmount), bucket.Sum(p => p.ProductsAmount)));
            }
        }
        else
        {
            var days = (int)Math.Ceiling(span.TotalDays);
            for (var d = from.Date; d < to; d = d.AddDays(1))
            {
                var bucket = payments.Where(p => p.PaidAt.Date == d).ToList();
                var label = days <= 7 ? d.ToString("ddd", culture) : d.ToString("dd", culture);
                perDay.Add(new DayRevenue(d, label, bucket.Sum(p => p.GamingAmount), bucket.Sum(p => p.ProductsAmount)));
            }
        }

        var perStation = gaming.GroupBy(s => s.StationName)
            .Select(g => new StationRevenue(g.Key, g.Sum(s => s.Total), TimeSpan.FromSeconds(g.Sum(s => s.PlayedSeconds)), g.Count()))
            .OrderByDescending(x => x.Revenue).ToList();

        var topProducts = lines.GroupBy(l => l.ProductName)
            .Select(g => new ProductSales(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal), g.Sum(l => l.LineProfit)))
            .OrderByDescending(x => x.Revenue).ToList();

        var modeCounts = gaming.GroupBy(s => s.Mode).ToDictionary(g => g.Key, g => g.Count());
        var methodTotals = payments.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Sum(p => p.TotalAmount));
        int? busiestHour = gaming.Count == 0 ? null : gaming.GroupBy(s => s.StartTime.Hour).OrderByDescending(g => g.Count()).First().Key;

        return new ReportData(
            from, to,
            payments.Sum(p => p.TotalAmount),
            payments.Sum(p => p.GamingAmount),
            payments.Sum(p => p.ProductsAmount),
            lines.Sum(l => l.LineProfit),
            gaming.Count,
            gaming.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(gaming.Average(s => s.PlayedSeconds)),
            previous.Sum(),
            perDay, perStation, topProducts, modeCounts, methodTotals, busiestHour);
    }
}

public sealed class SearchService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), ISearchService
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string text, int limit = 20, CancellationToken ct = default)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return [];
        var pattern = $"%{t.Replace("%", "").Replace("_", "")}%";
        await using var db = await OpenAsync(ct);
        var results = new List<SearchResult>();

        var stations = await db.Stations.AsNoTracking().Include(s => s.StationType)
            .Where(s => !s.IsDeleted && (EF.Functions.Like(s.Name, pattern) || EF.Functions.Like(s.Model ?? "", pattern)
                || EF.Functions.Like(s.Location ?? "", pattern) || EF.Functions.Like(s.StationType!.Name, pattern)))
            .OrderBy(s => s.Name).Take(limit).ToListAsync(ct);
        results.AddRange(stations.Select(s => new SearchResult(SearchKind.Station, s.Id, s.Name,
            $"{s.StationType?.Name} · {s.Location}{(s.IsActive ? "" : " · disabled")}")));

        var products = await db.Products.AsNoTracking().Include(p => p.Category)
            .Where(p => !p.IsDeleted && EF.Functions.Like(p.Name, pattern))
            .OrderBy(p => p.Name).Take(limit).ToListAsync(ct);
        results.AddRange(products.Select(p => new SearchResult(SearchKind.Product, p.Id, p.Name, $"{p.Category?.Name} · {p.Stock} in stock")));

        var customers = await db.Customers.AsNoTracking()
            .Where(c => !c.IsDeleted && (EF.Functions.Like(c.Name, pattern) || EF.Functions.Like(c.Phone ?? "", pattern)))
            .OrderBy(c => c.Name).Take(limit).ToListAsync(ct);
        results.AddRange(customers.Select(c => new SearchResult(SearchKind.Customer, c.Id, c.Name, c.Phone ?? "Customer")));

        var receipts = await db.Payments.AsNoTracking().Include(p => p.Session)
            .Where(p => EF.Functions.Like(p.ReceiptNumber, pattern))
            .OrderByDescending(p => p.PaidAt).Take(limit).ToListAsync(ct);
        results.AddRange(receipts.Select(p => new SearchResult(SearchKind.Session, p.SessionId, $"Receipt #{p.ReceiptNumber}",
            $"{p.Session?.StationName} · {p.PaidAt:dd/MM/yyyy HH:mm}")));

        return results.Take(limit * 2).ToList();
    }
}
