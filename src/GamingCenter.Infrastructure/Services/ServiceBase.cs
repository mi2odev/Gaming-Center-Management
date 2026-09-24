using GamingCenter.Application.Interfaces;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

/// <summary>
/// Services are singletons that open a short-lived DbContext per operation,
/// which is the recommended pattern for desktop apps.
/// </summary>
public abstract class ServiceBase(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
{
    protected IDbContextFactory<GamingCenterDbContext> DbFactory { get; } = dbFactory;
    protected IClock Clock { get; } = clock;
    protected ICurrentUser CurrentUser { get; } = currentUser;

    protected int? CurrentUserId => CurrentUser.User?.Id;

    protected Task<GamingCenterDbContext> OpenAsync(CancellationToken ct) => DbFactory.CreateDbContextAsync(ct);

    protected void RequireAdmin()
    {
        if (!CurrentUser.IsAdmin)
            throw new BusinessException("Only an administrator can do this.");
    }

    protected static string Required(string? value, string field, int maxLength = 128)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) throw new BusinessException($"{field} is required.");
        if (v.Length > maxLength) throw new BusinessException($"{field} must be at most {maxLength} characters.");
        return v;
    }

    protected static string? Optional(string? value, int maxLength = 500)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > maxLength ? v[..maxLength] : v;
    }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}
