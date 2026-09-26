using GamingCenter.Application.Common;
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
            .Include(p => p.Session!).ThenInclude(s => s.Products).ThenInclude(l => l.Product!).ThenInclude(pr => pr.Category)
            .Include(p => p.Session!).ThenInclude(s => s.Station!).ThenInclude(st => st.StationType)
            .Include(p => p.Session!).ThenInclude(s => s.Customer)
            .Include(p => p.User)
            .Include(p => p.Parts)
            .AsSplitQuery()
            .ToListAsync(ct);

    private static async Task<List<RepaymentRow>> LoadRepaymentsAsync(GamingCenterDbContext db, DateTime from, DateTime to, CancellationToken ct) =>
        (await db.CreditTransactions.AsNoTracking()
            .Where(t => t.Kind == CreditKind.Repayment && t.At >= from && t.At < to)
            .Include(t => t.Customer).Include(t => t.User)
            .OrderByDescending(t => t.At)
            .ToListAsync(ct))
        .Select(t => new RepaymentRow(t.At, t.Customer?.Name ?? "?", -t.Amount, t.Method ?? PaymentMethod.Cash, t.User?.DisplayName))
        .ToList();

    /// <summary>Money received in the period: what was paid at the till (not the part left on credit) plus credit paid back.</summary>
    private static async Task<decimal> IncomeAsync(GamingCenterDbContext db, DateTime from, DateTime to, CancellationToken ct)
    {
        var paid = await db.Payments.AsNoTracking().Where(p => p.PaidAt >= from && p.PaidAt < to)
            .Select(p => new { p.TotalAmount, p.CreditAmount }).ToListAsync(ct);
        var repaid = await db.CreditTransactions.AsNoTracking().Where(t => t.Kind == CreditKind.Repayment && t.At >= from && t.At < to)
            .Select(t => t.Amount).ToListAsync(ct);
        return paid.Sum(p => p.TotalAmount - p.CreditAmount) - repaid.Sum();
    }

    public async Task<IReadOnlyList<RepaymentRow>> GetRepaymentsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await LoadRepaymentsAsync(db, from, to, ct);
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(DateTime day, CancellationToken ct = default)
    {
        var from = day.Date;
        await using var db = await OpenAsync(ct);
        var today = await LoadPaymentsAsync(db, from, from.AddDays(1), ct);
        var repaid = (await LoadRepaymentsAsync(db, from, from.AddDays(1), ct)).Sum(r => r.Amount);
        var yesterday = await IncomeAsync(db, from.AddDays(-1), from, ct);

        var gamingSessions = today.Where(p => p.Session!.Mode != SessionMode.CounterSale).Select(p => p.Session!).ToList();
        var lines = today.SelectMany(p => p.Session!.Products).ToList();

        var topStation = gamingSessions.GroupBy(s => s.StationName)
            .Select(g => (Name: g.Key, Seconds: g.Sum(s => s.PlayedSeconds)))
            .OrderByDescending(x => x.Seconds).FirstOrDefault();
        var topProduct = lines.GroupBy(l => l.ProductName)
            .Select(g => (Name: g.Key, Units: g.Sum(l => l.Quantity)))
            .OrderByDescending(x => x.Units).FirstOrDefault();

        return new DashboardStats(
            Revenue: today.Sum(p => p.PaidNow) + repaid,
            RevenueYesterday: yesterday,
            GamingRevenue: today.Sum(p => p.GamingAmount),
            ProductRevenue: today.Sum(p => p.ProductsAmount),
            ProductProfit: lines.Sum(l => l.LineProfit),
            Sessions: gamingSessions.Count,
            AverageSession: gamingSessions.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(gamingSessions.Average(s => s.PlayedSeconds)),
            MostUsedStation: topStation.Name,
            MostUsedStationTime: TimeSpan.FromSeconds(topStation.Seconds),
            MostSoldProduct: topProduct.Name,
            MostSoldProductUnits: topProduct.Units,
            LowStock: await products.GetLowStockAsync(ct),
            Discounts: today.Sum(p => p.DiscountAmount),
            CreditRepaid: repaid,
            CreditLeft: today.Sum(p => p.CreditAmount));
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
            s.Payment?.Method, s.Payment?.PaidAt, s.Payment?.User?.DisplayName ?? s.StartedBy?.DisplayName, s.DiscountTotal)).ToList();
    }

    public async Task<ReceiptDto?> GetReceiptAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var s = await db.Sessions.AsNoTracking()
            .Include(x => x.Products).Include(x => x.Pauses).Include(x => x.RateChanges).Include(x => x.Customer)
            .Include(x => x.Payment!).ThenInclude(p => p.User)
            .Include(x => x.Payment!).ThenInclude(p => p.Parts)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s?.Payment is not { } pay) return null;

        var cfg = settings.Current;
        return new ReceiptDto(
            s.Id, pay.ReceiptNumber, pay.PaidAt, cfg.CenterName, cfg.Address, cfg.Phone,
            s.StationName, cfg.ShowCustomerOnReceipt ? s.Customer?.Name : null, s.Mode,
            s.StartTime, s.EndTime, s.PlayedSeconds, s.EndTime is { } end ? s.AverageRate(end) : s.HourlyRate, s.Rules.UnitLabel,
            RateNote(s),
            s.GamingTotal,
            s.Products.OrderBy(p => p.AddedAt).Select(p => new ReceiptLine(p.ProductName, p.Quantity, p.UnitPrice, p.LineTotal)).ToList(),
            s.ProductsTotal, s.DiscountTotal, s.Total, pay.Method, pay.AmountReceived, pay.ChangeGiven, pay.CreditAmount,
            s.CustomerId is { } cid ? (await db.CreditTransactions.AsNoTracking().Where(t => t.CustomerId == cid).Select(t => t.Amount).ToListAsync(ct)).Sum() : 0,
            pay.Parts.Select(x => new PaymentPartRequest(x.Method, x.Amount)).ToList(),
            pay.User?.DisplayName,
            s.Pauses.OrderBy(p => p.StartTime).Select(p => (p.StartTime, p.EndTime)).ToList(),
            cfg.ReceiptFooter);
    }

    /// <summary>"2 controllers" or "18:20–19:05 2 ctrl @300 · 19:05–20:13 4 ctrl @500" when controllers changed.</summary>
    internal static string? RateNote(GamingSession s)
    {
        if (s.EndTime is not { } end) return null;
        var segments = s.RateSegments(end);
        if (s.RateChanges.Count == 0)
            return s.Controllers is { } n ? $"{n} controller{(n == 1 ? "" : "s")}" : null;
        return string.Join(" · ", segments.Select(x =>
            $"{x.From:HH:mm}–{x.To:HH:mm} {(x.Controllers is { } c ? $"{c} ctrl " : "")}@{Money.Number(x.HourlyRate)}/h"));
    }

    /// <summary>"Cash" or "Cash 50 + Card 70", with "+ credit" when part is unpaid.</summary>
    internal static string MethodsText(Payment p)
    {
        var text = p.Parts.Count > 1
            ? string.Join(" + ", p.Parts.Select(x => $"{x.Method} {Money.Number(x.Amount)}"))
            : p.Parts.Count == 1 ? p.Parts[0].Method.ToString() : p.CreditAmount >= p.TotalAmount ? "" : p.Method.ToString();
        if (p.CreditAmount > 0) text = text.Length == 0 ? "Credit" : text + " + credit";
        return text;
    }

    public async Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var list = await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= from && p.PaidAt < to)
            .Include(p => p.Session!).ThenInclude(s => s.Customer)
            .Include(p => p.User)
            .Include(p => p.Parts)
            .AsSplitQuery()
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync(ct);
        return list.Select(p => new PaymentRow(p.Id, p.SessionId, p.ReceiptNumber, p.PaidAt, p.Session!.StationName,
            p.Session.Customer?.Name ?? "Walk-in", p.GamingAmount, p.ProductsAmount, p.TotalAmount, p.Method, p.User?.DisplayName, p.CreditAmount, MethodsText(p),
            p.CollectedBy(PaymentMethod.Cash), p.CollectedBy(PaymentMethod.Card), p.CollectedBy(PaymentMethod.Other))).ToList();
    }

    public async Task<ReportData> GetReportAsync(DateTime from, DateTime to, bool groupByMonth, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var payments = await LoadPaymentsAsync(db, from, to, ct);
        var span = to - from;
        var previous = await IncomeAsync(db, from - span, from, ct);
        var repayments = await LoadRepaymentsAsync(db, from, to, ct);

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
                var back = repayments.Where(r => r.At.Year == m.Year && r.At.Month == m.Month).Sum(r => r.Amount);
                perDay.Add(new DayRevenue(m, m.ToString("MMM", culture), bucket.Sum(p => p.GamingAmount), bucket.Sum(p => p.ProductsAmount),
                    bucket.Sum(p => p.DiscountAmount), bucket.Sum(p => p.CreditAmount), back));
            }
        }
        else
        {
            var days = (int)Math.Ceiling(span.TotalDays);
            for (var d = from.Date; d < to; d = d.AddDays(1))
            {
                var bucket = payments.Where(p => p.PaidAt.Date == d).ToList();
                var back = repayments.Where(r => r.At.Date == d).Sum(r => r.Amount);
                var label = days <= 7 ? d.ToString("ddd", culture) : d.ToString("dd", culture);
                perDay.Add(new DayRevenue(d, label, bucket.Sum(p => p.GamingAmount), bucket.Sum(p => p.ProductsAmount),
                    bucket.Sum(p => p.DiscountAmount), bucket.Sum(p => p.CreditAmount), back));
            }
        }

        var perStation = gaming.GroupBy(s => s.StationName)
            .Select(g => new StationRevenue(g.Key, g.Sum(s => s.Total), TimeSpan.FromSeconds(g.Sum(s => s.PlayedSeconds)), g.Count()))
            .OrderByDescending(x => x.Revenue).ToList();

        var topProducts = lines.GroupBy(l => l.ProductName)
            .Select(g => new ProductSales(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal), g.Sum(l => l.LineProfit)))
            .OrderByDescending(x => x.Revenue).ToList();

        var modeCounts = gaming.GroupBy(s => s.Mode).ToDictionary(g => g.Key, g => g.Count());
        var methodTotals = Enum.GetValues<PaymentMethod>()
            .ToDictionary(m => m, m => payments.Sum(p => p.CollectedBy(m)) + repayments.Where(r => r.Method == m).Sum(r => r.Amount))
            .Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var creditRows = await db.CreditTransactions.AsNoTracking()
            .Where(t => t.At >= from && t.At < to).Select(t => new { t.Amount, t.Kind }).ToListAsync(ct);
        decimal creditGiven = creditRows.Where(t => t.Amount > 0).Sum(t => t.Amount);
        decimal creditCollected = -creditRows.Where(t => t.Kind == CreditKind.Repayment).Sum(t => t.Amount);
        int? busiestHour = gaming.Count == 0 ? null : gaming.GroupBy(s => s.StartTime.Hour).OrderByDescending(g => g.Count()).First().Key;

        var extras = await BuildExtrasAsync(db, from, to, payments, repayments, gaming, lines, perDay, groupByMonth, ct);

        return new ReportData(
            from, to,
            payments.Sum(p => p.PaidNow) + repayments.Sum(r => r.Amount),
            payments.Sum(p => p.GamingAmount),
            payments.Sum(p => p.ProductsAmount),
            lines.Sum(l => l.LineProfit),
            gaming.Count,
            gaming.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(gaming.Average(s => s.PlayedSeconds)),
            previous,
            perDay, perStation, topProducts, modeCounts, methodTotals, busiestHour, creditGiven, creditCollected,
            payments.Sum(p => p.DiscountAmount), extras, payments.Sum(p => p.TotalAmount));
    }

    /// <summary>Per user account: money taken in by method (payments and credit paid back), discounts and credit given, sessions started.</summary>
    private static async Task<List<OperatorTotal>> OperatorTotalsAsync(GamingCenterDbContext db, DateTime from, DateTime to,
        List<Payment> payments, List<DayRevenue> buckets, bool groupByMonth, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var repaid = await db.CreditTransactions.AsNoTracking()
            .Where(t => t.Kind == CreditKind.Repayment && t.At >= from && t.At < to)
            .Select(t => new { t.UserId, t.At, t.Amount, t.Method }).ToListAsync(ct);
        var started = await db.Sessions.AsNoTracking()
            .Where(s => s.StartTime >= from && s.StartTime < to && s.Mode != SessionMode.CounterSale && s.Status != SessionStatus.Cancelled)
            .Select(s => s.StartedByUserId).ToListAsync(ct);

        int Bucket(DateTime at)
        {
            for (int i = 0; i < buckets.Count; i++)
            {
                var b = buckets[i].Day;
                if (groupByMonth ? b.Year == at.Year && b.Month == at.Month : b.Date == at.Date) return i;
            }
            return -1;
        }

        var ids = payments.Select(p => p.UserId).Concat(repaid.Select(r => r.UserId)).Concat(started).Distinct().ToList();
        var result = new List<OperatorTotal>();
        foreach (var id in ids)
        {
            var mine = payments.Where(p => p.UserId == id).ToList();
            var back = repaid.Where(r => r.UserId == id).ToList();
            decimal By(PaymentMethod m) => mine.Sum(p => p.CollectedBy(m)) + back.Where(r => (r.Method ?? PaymentMethod.Cash) == m).Sum(r => -r.Amount);
            var perBucket = new decimal[buckets.Count];
            foreach (var p in mine) { int i = Bucket(p.PaidAt); if (i >= 0) perBucket[i] += p.PaidNow; }
            foreach (var r in back) { int i = Bucket(r.At); if (i >= 0) perBucket[i] += -r.Amount; }

            var user = id is { } uid ? users.GetValueOrDefault(uid) : null;
            decimal cash = By(PaymentMethod.Cash), card = By(PaymentMethod.Card), other = By(PaymentMethod.Other);
            result.Add(new OperatorTotal(user?.DisplayName ?? "—", mine.Count, cash + card + other, mine.Sum(p => p.DiscountAmount),
                user?.Role.ToString() ?? "", cash, card, other, back.Sum(r => -r.Amount), mine.Sum(p => p.CreditAmount),
                started.Count(s => s == id), mine.Sum(p => p.TotalAmount), perBucket));
        }
        return result.OrderByDescending(o => o.Collected).ThenBy(o => o.Name).ToList();
    }

    private async Task<ReportExtras> BuildExtrasAsync(GamingCenterDbContext db, DateTime from, DateTime to,
        List<Payment> payments, List<RepaymentRow> repayments, List<GamingSession> gaming, List<SessionProduct> lines,
        List<DayRevenue> buckets, bool groupByMonth, CancellationToken ct)
    {
        var perHour = new int[24];
        foreach (var s in gaming) perHour[s.StartTime.Hour]++;

        // Monday first.
        var perWeekday = new decimal[7];
        foreach (var p in payments) perWeekday[((int)p.PaidAt.DayOfWeek + 6) % 7] += p.PaidNow;
        foreach (var r in repayments) perWeekday[((int)r.At.DayOfWeek + 6) % 7] += r.Amount;

        static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        var perRoom = gaming.GroupBy(s => Or(s.Station?.Location, "No room"))
            .Select(g => new NamedAmount(g.Key, g.Sum(s => s.Total), g.Count(), (decimal)g.Sum(s => s.PlayedSeconds) / 3600m))
            .OrderByDescending(x => x.Amount).ToList();
        var perType = gaming.GroupBy(s => Or(s.Station?.StationType?.Name, "Other"))
            .Select(g => new NamedAmount(g.Key, g.Sum(s => s.Total), g.Count(), (decimal)g.Sum(s => s.PlayedSeconds) / 3600m))
            .OrderByDescending(x => x.Amount).ToList();
        var perCategory = lines.GroupBy(l => Or(l.Product?.Category?.Name, "Other"))
            .Select(g => new NamedAmount(g.Key, g.Sum(l => l.LineTotal), g.Sum(l => l.Quantity), g.Sum(l => l.LineProfit)))
            .OrderByDescending(x => x.Amount).ToList();

        var methods = Enum.GetValues<PaymentMethod>()
            .Select(m => new NamedAmount(m.ToString(), payments.Sum(p => p.CollectedBy(m)) + repayments.Where(r => r.Method == m).Sum(r => r.Amount),
                payments.Count(p => p.CollectedBy(m) > 0) + repayments.Count(r => r.Method == m)))
            .Where(x => x.Amount > 0).ToList();
        decimal onCredit = payments.Sum(p => p.CreditAmount);
        if (onCredit > 0) methods.Add(new NamedAmount("Credit", onCredit, payments.Count(p => p.CreditAmount > 0)));

        // Occupancy: play time over the hours the active stations could have been used (up to now).
        var end = to < Clock.Now ? to : Clock.Now;
        double openHours = Math.Max(0, (end - from).TotalHours);
        var stationRows = await db.Stations.AsNoTracking().Include(s => s.StationType)
            .Where(s => !s.IsDeleted && s.IsActive).ToListAsync(ct);
        long playSeconds = gaming.Sum(s => s.PlayedSeconds);
        double occupancy = openHours <= 0 || stationRows.Count == 0 ? 0 : Math.Min(1, playSeconds / 3600.0 / (openHours * stationRows.Count));

        var byStation = gaming.GroupBy(s => s.StationId).ToDictionary(g => g.Key ?? 0, g => g.ToList());
        var stations = stationRows.Select(st =>
            {
                var list = byStation.GetValueOrDefault(st.Id) ?? [];
                long secs = list.Sum(s => s.PlayedSeconds);
                return new StationUsage(st.Name, st.StationType?.Name ?? "", st.Location ?? "", list.Count, TimeSpan.FromSeconds(secs),
                    list.Sum(s => s.Total), openHours <= 0 ? 0 : Math.Min(1, secs / 3600.0 / openHours));
            })
            // Removed stations that still earned money in the period.
            .Concat(gaming.Where(s => s.StationId is null || stationRows.All(r => r.Id != s.StationId)).GroupBy(s => s.StationName)
                .Select(g => new StationUsage(g.Key, g.First().Station?.StationType?.Name ?? "", g.First().Station?.Location ?? "", g.Count(),
                    TimeSpan.FromSeconds(g.Sum(s => s.PlayedSeconds)), g.Sum(s => s.Total), 0)))
            .OrderByDescending(x => x.Revenue).ThenBy(x => x.Name).ToList();

        var withCustomer = payments.Where(p => p.Session?.CustomerId is not null).ToList();
        var customerIds = withCustomer.Select(p => p.Session!.CustomerId!.Value).Distinct().ToList();
        var balances = (await db.CreditTransactions.AsNoTracking().Where(t => customerIds.Contains(t.CustomerId))
                .Select(t => new { t.CustomerId, t.Amount }).ToListAsync(ct))
            .GroupBy(t => t.CustomerId).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var topCustomers = withCustomer.GroupBy(p => p.Session!.CustomerId!.Value)
            .Select(g => new CustomerSpend(g.First().Session!.Customer?.Name ?? "?", g.Count(), g.Sum(p => p.TotalAmount), Math.Max(0, balances.GetValueOrDefault(g.Key))))
            .OrderByDescending(x => x.Spent).Take(10).ToList();
        int newCustomers = await db.Customers.CountAsync(c => !c.IsDeleted && c.CreatedAt >= from && c.CreatedAt < to, ct);

        var operators = await OperatorTotalsAsync(db, from, to, payments, buckets, groupByMonth, ct);

        var counter = payments.Where(p => p.Session?.Mode == SessionMode.CounterSale).ToList();
        return new ReportExtras(perHour, perWeekday, perRoom, perType, perCategory, methods, stations, topCustomers, operators,
            payments.Count, counter.Count, counter.Sum(p => p.TotalAmount), TimeSpan.FromSeconds(playSeconds), occupancy,
            customerIds.Count, newCustomers, gaming.Count(s => s.CustomerId is null), onCredit);
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
