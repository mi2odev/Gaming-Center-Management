using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

/// <summary>What the owner spends on the center. Only admins see or change expenses.</summary>
public sealed class ExpenseService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), IExpenseService
{
    public async Task<IReadOnlyList<ExpenseDto>> GetAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var rows = await db.Expenses.AsNoTracking()
            .Where(e => e.Date >= from && e.Date < to)
            .Include(e => e.User)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ExpenseDto> SaveAsync(SaveExpenseRequest request, CancellationToken ct = default)
    {
        RequireAdmin();
        var description = Required(request.Description, "What was bought", 128);
        if (request.Amount <= 0) throw new BusinessException("Enter the price paid.");
        if (!Enum.IsDefined(request.Category)) throw new BusinessException("Choose a category.");
        if (request.Date.Date > Clock.Now.Date) throw new BusinessException("The date cannot be in the future.");

        await using var db = await OpenAsync(ct);
        Expense entity;
        if (request.Id is { } id)
        {
            entity = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new BusinessException("Expense not found.");
        }
        else
        {
            entity = new Expense { CreatedAt = Clock.Now, UserId = CurrentUserId };
            db.Expenses.Add(entity);
        }
        // Keep the time of day for today's purchases so they sort naturally; past dates have no meaningful time.
        entity.Date = request.Date.Date == Clock.Now.Date && request.Id is null ? Clock.Now : request.Date;
        entity.Description = description;
        entity.Category = request.Category;
        entity.Amount = request.Amount;
        entity.Note = Optional(request.Note, 500);
        await db.SaveChangesAsync(ct);

        await db.Entry(entity).Reference(e => e.User).LoadAsync(ct);
        return ToDto(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var entity = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new BusinessException("Expense not found.");
        db.Expenses.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    private static ExpenseDto ToDto(Expense e) => new(e.Id, e.Date, e.Description, e.Category, e.Amount, e.Note, e.User?.DisplayName);
}
