using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

public sealed class SessionService(
    IDbContextFactory<GamingCenterDbContext> dbFactory,
    IClock clock,
    ICurrentUser currentUser,
    ISettingsService settings) : ServiceBase(dbFactory, clock, currentUser), ISessionService
{
    private const int MaxMinutes = 24 * 60;

    private static IQueryable<GamingSession> WithDetails(GamingCenterDbContext db) =>
        db.Sessions
          .Include(s => s.Pauses)
          .Include(s => s.Products)
          .Include(s => s.RateChanges)
          .Include(s => s.Customer)
          .Include(s => s.Station!).ThenInclude(st => st.StationType);

    private static IQueryable<GamingSession> Live(IQueryable<GamingSession> q) =>
        q.Where(s => s.Status == SessionStatus.Running || s.Status == SessionStatus.Paused || s.Status == SessionStatus.AwaitingPayment);

    public async Task<IReadOnlyList<GamingSession>> GetLiveSessionsAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await Live(WithDetails(db)).Where(s => s.Mode != SessionMode.CounterSale)
            .AsNoTracking().AsSplitQuery().ToListAsync(ct);
    }

    public async Task<GamingSession?> GetAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await WithDetails(db).AsNoTracking().AsSplitQuery().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<GamingSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == request.StationId && !s.IsDeleted, ct)
            ?? throw new BusinessException("Station not found.");
        if (!station.IsActive) throw new BusinessException($"{station.Name} is disabled.");
        if (station.State == StationState.Maintenance) throw new BusinessException($"{station.Name} is under maintenance.");
        if (await Live(db.Sessions).AnyAsync(s => s.StationId == station.Id, ct))
            throw new BusinessException($"{station.Name} already has a running session.");

        if (request.CustomerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid && !c.IsDeleted, ct))
            throw new BusinessException("Customer not found.");

        var session = new GamingSession
        {
            StationId = station.Id,
            StationName = station.Name,
            CustomerId = request.CustomerId,
            Mode = request.Mode,
            Status = SessionStatus.Running,
            StartTime = Clock.Now,
            HourlyRate = station.HourlyRate,
            StartedByUserId = CurrentUserId,
        };
        if (ControllerPricing.IsPriced(station.ControllerCount, station.MaxControllers, station.ExtraControllerRate))
        {
            int n = ValidControllers(station, request.Controllers ?? station.ControllerCount!.Value);
            session.Controllers = n;
            session.HourlyRate = ControllerPricing.RateFor(station.HourlyRate, station.ControllerCount, station.ExtraControllerRate, n);
        }
        else session.Controllers = station.ControllerCount;
        session.ApplyRules(settings.Current.BillingRules);

        switch (request.Mode)
        {
            case SessionMode.Open:
                break;
            case SessionMode.FixedDuration:
                session.PlannedMinutes = ValidMinutes(request.PlannedMinutes);
                break;
            case SessionMode.FixedBudget:
                session.Budget = ValidAmount(request.Budget, "Budget");
                break;
            default:
                throw new BusinessException("Choose a session mode.");
        }

        if (station.State == StationState.Reserved)
        {
            station.State = StationState.Available;
            station.ReservedFor = null;
            station.ReservedAt = null;
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(session.Id, ct))!;
    }

    public Task<GamingSession> PauseAsync(int sessionId, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.Status != SessionStatus.Running) throw new BusinessException("Only a running session can be paused.");
            s.Pauses.Add(new SessionPause { StartTime = Clock.Now });
            s.Status = SessionStatus.Paused;
            return Task.CompletedTask;
        }, ct);

    public Task<GamingSession> ResumeAsync(int sessionId, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.Status != SessionStatus.Paused) throw new BusinessException("The session is not paused.");
            var open = s.OpenPause;
            if (open is not null) open.EndTime = Clock.Now;
            s.Status = SessionStatus.Running;
            return Task.CompletedTask;
        }, ct);

    public Task<GamingSession> ExtendTimeAsync(int sessionId, int minutes, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.Mode != SessionMode.FixedDuration) throw new BusinessException("Time can only be added to a fixed-duration session. Change the mode first.");
            ValidMinutes(minutes);
            s.PlannedMinutes = (s.PlannedMinutes ?? 0) + minutes;
            Reopen(s);
            return Task.CompletedTask;
        }, ct);

    public Task<GamingSession> AddBudgetAsync(int sessionId, decimal amount, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.Mode != SessionMode.FixedBudget) throw new BusinessException("Money can only be added to a fixed-budget session. Change the mode first.");
            s.Budget = (s.Budget ?? 0) + ValidAmount(amount, "Amount");
            Reopen(s);
            return Task.CompletedTask;
        }, ct);

    public Task<GamingSession> ChangeModeAsync(int sessionId, SessionMode mode, int? plannedMinutes, decimal? budget, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.IsCounterSale || mode == SessionMode.CounterSale) throw new BusinessException("This mode cannot be changed.");
            switch (mode)
            {
                case SessionMode.Open:
                    s.PlannedMinutes = null;
                    s.Budget = null;
                    break;
                case SessionMode.FixedDuration:
                    s.PlannedMinutes = ValidMinutes(plannedMinutes);
                    s.Budget = null;
                    break;
                case SessionMode.FixedBudget:
                    s.Budget = ValidAmount(budget, "Budget");
                    s.PlannedMinutes = null;
                    break;
            }
            s.Mode = mode;
            Reopen(s);
            return Task.CompletedTask;
        }, ct);

    public Task<GamingSession> ChangeControllersAsync(int sessionId, int controllers, CancellationToken ct = default) =>
        MutateAsync(sessionId, async (db, s) =>
        {
            if (s.Status == SessionStatus.AwaitingPayment) throw new BusinessException("Time is up. Extend the session before changing controllers.");
            var station = await db.Stations.FirstOrDefaultAsync(x => x.Id == s.StationId, ct) ?? throw new BusinessException("Station not found.");
            if (!ControllerPricing.IsPriced(station.ControllerCount, station.MaxControllers, station.ExtraControllerRate))
                throw new BusinessException($"{station.Name} does not have controller pricing. Set it up on the Gaming Stations page.");
            int n = ValidControllers(station, controllers);
            if (n == s.Controllers) return;

            // The price locked for this session stays the base; only the controller supplement follows the station setup.
            decimal baseRate = s.HourlyRate - Math.Max(0, (s.Controllers ?? station.ControllerCount!.Value) - station.ControllerCount!.Value) * station.ExtraControllerRate;
            decimal newRate = ControllerPricing.RateFor(baseRate, station.ControllerCount, station.ExtraControllerRate, n);
            s.RateChanges.Add(new SessionRateChange
            {
                At = Clock.Now, OldRate = s.HourlyRate, NewRate = newRate,
                OldControllers = s.Controllers, NewControllers = n,
            });
            s.HourlyRate = newRate;
            s.Controllers = n;
        }, ct);

    private static int ValidControllers(GamingStation station, int controllers)
    {
        int max = station.MaxControllers ?? station.ControllerCount ?? 1;
        if (controllers < 1 || controllers > max)
            throw new BusinessException($"{station.Name} takes 1 to {max} controllers.");
        return controllers;
    }

    public Task<GamingSession> AttachCustomerAsync(int sessionId, int? customerId, CancellationToken ct = default) =>
        MutateAsync(sessionId, async (db, s) =>
        {
            if (customerId is { } id && !await db.Customers.AnyAsync(c => c.Id == id && !c.IsDeleted, ct))
                throw new BusinessException("Customer not found.");
            s.CustomerId = customerId;
        }, ct);

    public Task<GamingSession> AddProductAsync(int sessionId, int productId, int quantity = 1, CancellationToken ct = default) =>
        MutateAsync(sessionId, async (db, s) =>
        {
            if (quantity <= 0) throw new BusinessException("Quantity must be at least 1.");
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct)
                ?? throw new BusinessException("Product not found.");
            if (!product.IsActive) throw new BusinessException($"{product.Name} is disabled.");
            TakeStock(db, product, quantity, s.Id);

            var line = s.Products.FirstOrDefault(l => l.ProductId == product.Id && l.UnitPrice == product.SellingPrice);
            if (line is null)
                s.Products.Add(new SessionProduct
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Quantity = quantity,
                    UnitPrice = product.SellingPrice,
                    UnitCost = product.PurchasePrice,
                    AddedAt = Clock.Now,
                });
            else
                line.Quantity += quantity;
        }, ct);

    public async Task<GamingSession> SetProductQuantityAsync(int sessionProductId, int quantity, CancellationToken ct = default)
    {
        int sessionId;
        await using (var db = await OpenAsync(ct))
        {
            sessionId = await db.SessionProducts.Where(l => l.Id == sessionProductId).Select(l => l.SessionId).FirstOrDefaultAsync(ct);
            if (sessionId == 0) throw new BusinessException("Item not found.");
        }

        return await MutateAsync(sessionId, async (db, s) =>
        {
            if (quantity < 0) throw new BusinessException("Quantity cannot be negative.");
            var line = s.Products.First(l => l.Id == sessionProductId);
            var product = await db.Products.FirstAsync(p => p.Id == line.ProductId, ct);
            int delta = quantity - line.Quantity;
            if (delta > 0) TakeStock(db, product, delta, s.Id);
            else if (delta < 0) ReturnStock(db, product, -delta, s.Id);

            if (quantity == 0)
            {
                s.Products.Remove(line);
                db.SessionProducts.Remove(line);
            }
            else line.Quantity = quantity;
        }, ct);
    }

    public Task<GamingSession> StopPlayAsync(int sessionId, DateTime endTime, CancellationToken ct = default) =>
        MutateAsync(sessionId, (db, s) =>
        {
            if (s.Status is not (SessionStatus.Running or SessionStatus.Paused)) return Task.CompletedTask;
            StopClock(s, endTime);
            s.Status = SessionStatus.AwaitingPayment;
            return Task.CompletedTask;
        }, ct);

    public async Task<Payment> CompleteAsync(CompleteSessionRequest request, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var s = await LoadTrackedAsync(db, request.SessionId, ct);
        if (!s.IsLive) throw new BusinessException("This session is already closed.");

        if (s.EndTime is null)
        {
            var end = request.EndTime > Clock.Now || request.EndTime < s.StartTime ? Clock.Now : request.EndTime;
            StopClock(s, end);
        }

        if (request.CustomerId is { } cid)
        {
            if (!await db.Customers.AnyAsync(c => c.Id == cid && !c.IsDeleted, ct)) throw new BusinessException("Customer not found.");
            s.CustomerId = cid;
        }

        var end0 = s.EndTime!.Value;
        s.PlayedSeconds = (long)s.PlayedTime(end0).TotalSeconds;
        s.GamingTotal = s.GamingCost(end0);
        s.ProductsTotal = s.ProductsCost();
        s.Total = s.GamingTotal + s.ProductsTotal;
        s.Status = SessionStatus.Completed;
        s.EndedByUserId = CurrentUserId;

        var payment = await CreatePaymentAsync(db, s, request.Method, request.AmountReceived, request.PayNow, request.Parts, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return payment;
    }

    public async Task CancelAsync(int sessionId, string reason, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var s = await LoadTrackedAsync(db, sessionId, ct);
        if (!s.IsLive) throw new BusinessException("This session is already closed.");

        foreach (var line in s.Products)
        {
            var product = await db.Products.FirstAsync(p => p.Id == line.ProductId, ct);
            ReturnStock(db, product, line.Quantity, s.Id);
        }

        if (s.EndTime is null) StopClock(s, Clock.Now);
        s.PlayedSeconds = (long)s.PlayedTime(s.EndTime!.Value).TotalSeconds;
        s.GamingTotal = 0;
        s.ProductsTotal = 0;
        s.Total = 0;
        s.Status = SessionStatus.Cancelled;
        s.EndedByUserId = CurrentUserId;
        s.Notes = Optional($"Cancelled: {reason}", 500);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<Payment> CounterSaleAsync(CounterSaleRequest request, CancellationToken ct = default)
    {
        var lines = request.Lines.Where(l => l.Quantity > 0).GroupBy(l => l.ProductId)
            .Select(g => new CartLine(g.Key, g.Sum(x => x.Quantity))).ToList();
        if (lines.Count == 0) throw new BusinessException("Add at least one product.");

        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var now = Clock.Now;
        var s = new GamingSession
        {
            StationName = "Counter",
            Mode = SessionMode.CounterSale,
            Status = SessionStatus.Completed,
            StartTime = now,
            EndTime = now,
            CustomerId = request.CustomerId,
            StartedByUserId = CurrentUserId,
            EndedByUserId = CurrentUserId,
        };
        s.ApplyRules(settings.Current.BillingRules);
        db.Sessions.Add(s);
        await db.SaveChangesAsync(ct);

        foreach (var l in lines)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == l.ProductId && !p.IsDeleted && p.IsActive, ct)
                ?? throw new BusinessException("A product in the cart is no longer available.");
            TakeStock(db, product, l.Quantity, s.Id);
            s.Products.Add(new SessionProduct
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = l.Quantity,
                UnitPrice = product.SellingPrice,
                UnitCost = product.PurchasePrice,
                AddedAt = now,
            });
        }

        s.ProductsTotal = s.ProductsCost();
        s.Total = s.ProductsTotal;
        var payment = await CreatePaymentAsync(db, s, request.Method, request.AmountReceived, request.PayNow, request.Parts, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return payment;
    }

    // ---- helpers ----

    private async Task<GamingSession> MutateAsync(int sessionId, Func<GamingCenterDbContext, GamingSession, Task> change, CancellationToken ct)
    {
        await using (var db = await OpenAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var s = await LoadTrackedAsync(db, sessionId, ct);
            if (!s.IsLive) throw new BusinessException("This session is already closed.");
            await change(db, s);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return (await GetAsync(sessionId, ct))!;
    }

    private static async Task<GamingSession> LoadTrackedAsync(GamingCenterDbContext db, int id, CancellationToken ct) =>
        await db.Sessions.Include(s => s.Pauses).Include(s => s.Products).Include(s => s.RateChanges).AsSplitQuery().FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new BusinessException("Session not found.");

    /// <summary>Stops the clock at <paramref name="end"/>, closing any open pause.</summary>
    private static void StopClock(GamingSession s, DateTime end)
    {
        foreach (var p in s.Pauses.Where(p => p.EndTime is null))
            p.EndTime = end < p.StartTime ? p.StartTime : end;
        s.EndTime = end;
    }

    /// <summary>If play was stopped (auto-end), resume it; the stopped interval is recorded as a pause so it is not billed.</summary>
    private void Reopen(GamingSession s)
    {
        if (s.Status != SessionStatus.AwaitingPayment || s.EndTime is not { } stoppedAt) return;
        var now = Clock.Now;
        if (now > stoppedAt) s.Pauses.Add(new SessionPause { StartTime = stoppedAt, EndTime = now });
        s.EndTime = null;
        s.Status = SessionStatus.Running;
    }

    private void TakeStock(GamingCenterDbContext db, Product product, int quantity, int sessionId)
    {
        if (product.Stock < quantity)
            throw new BusinessException(product.Stock <= 0
                ? $"{product.Name} is out of stock."
                : $"Only {product.Stock} × {product.Name} left in stock.");
        product.Stock -= quantity;
        product.UpdatedAt = Clock.Now;
        db.StockMovements.Add(new StockMovement
        {
            ProductId = product.Id, Change = -quantity, StockAfter = product.Stock,
            Reason = StockMovementReason.Sale, SessionId = sessionId == 0 ? null : sessionId, UserId = CurrentUserId, At = Clock.Now,
        });
    }

    private void ReturnStock(GamingCenterDbContext db, Product product, int quantity, int sessionId)
    {
        product.Stock += quantity;
        product.UpdatedAt = Clock.Now;
        db.StockMovements.Add(new StockMovement
        {
            ProductId = product.Id, Change = quantity, StockAfter = product.Stock,
            Reason = StockMovementReason.SaleReturn, SessionId = sessionId, UserId = CurrentUserId, At = Clock.Now,
        });
    }

    /// <summary>
    /// Records the payment. <paramref name="parts"/> splits it between people or methods (50 cash + 70 card).
    /// With <paramref name="payNow"/> set, only that much is paid now and the rest goes on the customer's credit
    /// (the customer must be known). Change can only be given from cash. Runs inside the caller's transaction.
    /// </summary>
    private async Task<Payment> CreatePaymentAsync(GamingCenterDbContext db, GamingSession s, PaymentMethod method, decimal received,
        decimal? payNow, IReadOnlyList<PaymentPartRequest>? parts, CancellationToken ct)
    {
        decimal total = s.Total;
        decimal credit = 0;
        if (payNow is { } now0 && now0 < total)
        {
            if (now0 < 0) throw new BusinessException("Amount paid now cannot be negative.");
            if (s.CustomerId is null)
                throw new BusinessException("To leave part of the bill unpaid, choose or create the customer who owes it.");
            credit = total - now0;
        }
        decimal due = total - credit;

        List<PaymentPartRequest> lines;
        if (parts is { Count: > 0 })
        {
            if (parts.Any(p => p.Amount < 0)) throw new BusinessException("Payment amounts cannot be negative.");
            lines = parts.Where(p => p.Amount > 0).ToList();
        }
        else
        {
            // Single payment (no split).
            if (method == PaymentMethod.Cash) { if (received <= 0 && credit == 0) received = due; }
            else received = due;
            lines = received > 0 ? [new PaymentPartRequest(method, received)] : [];
        }

        decimal handed = lines.Sum(p => p.Amount);
        if (handed < due)
            throw new BusinessException($"Still {due - handed:0.##} to pay. Add another payment with +, or tick \"Pay the rest later\" to put it on the customer's credit.");
        decimal change = handed - due;
        decimal cash = lines.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        if (change > cash)
            throw new BusinessException("Card and other payments cannot be more than what is due. Only cash can be given back as change.");

        var now = Clock.Now;
        var prefix = now.ToString("yyyy-MMdd-");
        var countToday = await db.Payments.CountAsync(p => p.ReceiptNumber.StartsWith(prefix), ct);

        var payment = new Payment
        {
            SessionId = s.Id,
            ReceiptNumber = prefix + (countToday + 1).ToString("000"),
            PaidAt = now,
            GamingAmount = s.GamingTotal,
            ProductsAmount = s.ProductsTotal,
            TotalAmount = total,
            // Main method = the biggest part (used where a single method is shown).
            Method = lines.Count > 0 ? lines.GroupBy(p => p.Method).OrderByDescending(g => g.Sum(p => p.Amount)).First().Key : method,
            AmountReceived = handed,
            ChangeGiven = change,
            CreditAmount = credit,
            UserId = CurrentUserId,
            Parts = lines.Select(p => new PaymentPart { Method = p.Method, Amount = p.Amount }).ToList(),
        };
        db.Payments.Add(payment);

        if (credit > 0)
        {
            db.CreditTransactions.Add(new CreditTransaction
            {
                CustomerId = s.CustomerId!.Value,
                At = now,
                Amount = credit,
                Kind = CreditKind.UnpaidBill,
                Payment = payment,
                Note = $"Unpaid part of {s.StationName} bill",
                UserId = CurrentUserId,
            });
        }
        return payment;
    }

    private static int ValidMinutes(int? minutes)
    {
        if (minutes is not { } m || m <= 0) throw new BusinessException("Enter a duration greater than zero.");
        if (m > MaxMinutes) throw new BusinessException("Duration cannot exceed 24 hours.");
        return m;
    }

    private static decimal ValidAmount(decimal? amount, string field)
    {
        if (amount is not { } a || a <= 0) throw new BusinessException($"{field} must be greater than zero.");
        if (a > 10_000_000) throw new BusinessException($"{field} is too large.");
        return a;
    }
}
