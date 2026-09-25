using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

/// <summary>
/// Customer credit ("pay later"). Balances are the sum of an append-only ledger,
/// aggregated in memory because SQLite stores decimals as TEXT.
/// </summary>
public sealed class CreditService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), ICreditService
{
    public async Task<IReadOnlyList<CreditBalanceDto>> GetBalancesAsync(bool includeSettled = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.CreditTransactions.AsNoTracking()
            .Select(t => new { t.CustomerId, t.Amount, t.At, t.Kind })
            .ToListAsync(ct);
        var customers = await db.Customers.AsNoTracking()
            .Where(c => rows.Select(r => r.CustomerId).Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        return rows.GroupBy(r => r.CustomerId)
            .Select(g =>
            {
                var c = customers[g.Key];
                return new CreditBalanceDto(
                    c.Id, c.Name, c.Phone, g.Sum(r => r.Amount),
                    g.Where(r => r.Amount > 0).Select(r => (DateTime?)r.At).Max(),
                    g.Where(r => r.Kind == CreditKind.Repayment).Select(r => (DateTime?)r.At).Max(),
                    g.Count(r => r.Kind == CreditKind.UnpaidBill));
            })
            .Where(b => includeSettled || b.Balance != 0)
            .OrderByDescending(b => b.Balance)
            .ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<CreditEntryDto>> GetHistoryAsync(int customerId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.CreditTransactions.AsNoTracking()
            .Where(t => t.CustomerId == customerId)
            .Include(t => t.Payment)
            .Include(t => t.User)
            .OrderBy(t => t.At).ThenBy(t => t.Id)
            .ToListAsync(ct);
        decimal running = 0;
        var list = new List<CreditEntryDto>(rows.Count);
        foreach (var t in rows)
        {
            running += t.Amount;
            list.Add(ToDto(t, running));
        }
        list.Reverse(); // newest first
        return list;
    }

    public async Task<decimal> GetBalanceAsync(int customerId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var amounts = await db.CreditTransactions.AsNoTracking().Where(t => t.CustomerId == customerId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }

    public async Task<decimal> GetTotalOutstandingAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.CreditTransactions.AsNoTracking().Select(t => new { t.CustomerId, t.Amount }).ToListAsync(ct);
        return rows.GroupBy(r => r.CustomerId).Select(g => g.Sum(r => r.Amount)).Where(b => b > 0).Sum();
    }

    public async Task<CreditEntryDto> RecordRepaymentAsync(int customerId, decimal amount, PaymentMethod method, string? note, CancellationToken ct = default)
    {
        if (amount <= 0) throw new BusinessException("Enter the amount the customer pays back.");
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct) ?? throw new BusinessException("Customer not found.");
        var balance = (await db.CreditTransactions.Where(t => t.CustomerId == customerId).Select(t => t.Amount).ToListAsync(ct)).Sum();
        if (balance <= 0) throw new BusinessException($"{customer.Name} does not owe anything.");
        if (amount > balance) throw new BusinessException($"{customer.Name} owes only {balance:0.##}. Enter at most that amount.");

        var entry = new CreditTransaction
        {
            CustomerId = customerId, At = Clock.Now, Amount = -amount, Kind = CreditKind.Repayment,
            Method = method, Note = Optional(note, 256), UserId = CurrentUserId,
        };
        db.CreditTransactions.Add(entry);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(entry, balance - amount);
    }

    public async Task<CreditEntryDto> AddManualAsync(int customerId, decimal amount, string note, CancellationToken ct = default)
    {
        // Anyone can charge a customer; only an admin can reduce or forgive a debt.
        if (amount < 0) RequireAdmin();
        if (amount == 0) throw new BusinessException("Enter an amount.");
        var text = Required(note, "Reason", 256);
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct)) throw new BusinessException("Customer not found.");
        var balance = (await db.CreditTransactions.Where(t => t.CustomerId == customerId).Select(t => t.Amount).ToListAsync(ct)).Sum();
        if (balance + amount < 0) throw new BusinessException("This would make the balance negative.");
        var entry = new CreditTransaction
        {
            CustomerId = customerId, At = Clock.Now, Amount = amount, Kind = CreditKind.Manual, Note = text, UserId = CurrentUserId,
        };
        db.CreditTransactions.Add(entry);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(entry, balance + amount);
    }

    private static CreditEntryDto ToDto(CreditTransaction t, decimal balanceAfter) => new(
        t.Id, t.At, t.Kind, t.Amount, balanceAfter, t.Payment?.ReceiptNumber, t.Payment?.SessionId, t.Method, t.Note, t.User?.DisplayName);
}
