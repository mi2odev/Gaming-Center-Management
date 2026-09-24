using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

public sealed class AuthService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), IAuthService
{
    public async Task<UserDto?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        await using var db = await OpenAsync(ct);
        var name = username.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == name, ct);
        if (user is null || !user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            return null;
        user.LastLoginAt = Clock.Now;
        await db.SaveChangesAsync(ct);
        return UserService.ToDto(user);
    }
}

public sealed class UserService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), IUserService
{
    internal static UserDto ToDto(User u) => new(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.LastLoginAt);

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var users = await db.Users.AsNoTracking().OrderByDescending(u => u.Role).ThenBy(u => u.Username).ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto> SaveAsync(SaveUserRequest r, CancellationToken ct = default)
    {
        RequireAdmin();
        var username = Required(r.Username, "Username", 64);
        if (username.Any(char.IsWhiteSpace)) throw new BusinessException("Username cannot contain spaces.");
        var display = Required(string.IsNullOrWhiteSpace(r.DisplayName) ? username : r.DisplayName, "Display name", 128);
        if (r.NewPassword is { Length: > 0 and < 6 }) throw new BusinessException("Password must be at least 6 characters.");

        await using var db = await OpenAsync(ct);
        if (await db.Users.AnyAsync(u => u.Id != (r.Id ?? 0) && u.Username == username, ct))
            throw new BusinessException($"Username \"{username}\" is already taken.");

        User user;
        if (r.Id is { } id)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new BusinessException("User not found.");
            bool losesAdmin = user.Role == UserRole.Admin && user.IsActive && (r.Role != UserRole.Admin || !r.IsActive);
            if (losesAdmin && await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive, ct) <= 1)
                throw new BusinessException("At least one active administrator is required.");
            if (id == CurrentUserId && !r.IsActive) throw new BusinessException("You cannot deactivate your own account.");
        }
        else
        {
            if (string.IsNullOrEmpty(r.NewPassword)) throw new BusinessException("Set a password for the new user.");
            user = new User { CreatedAt = Clock.Now };
            db.Users.Add(user);
        }

        user.Username = username;
        user.DisplayName = display;
        user.Role = r.Role;
        user.IsActive = r.IsActive;
        if (!string.IsNullOrEmpty(r.NewPassword))
            (user.PasswordHash, user.PasswordSalt) = PasswordHasher.Hash(r.NewPassword);
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (CurrentUserId is not { } id) throw new BusinessException("Not signed in.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6) throw new BusinessException("Password must be at least 6 characters.");
        await using var db = await OpenAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt))
            throw new BusinessException("Current password is incorrect.");
        (user.PasswordHash, user.PasswordSalt) = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class CustomerService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), ICustomerService
{
    public async Task<IReadOnlyList<CustomerDto>> GetAllAsync(string? search = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Customers.AsNoTracking().Where(c => !c.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(c => EF.Functions.Like(c.Name, pattern) || (c.Phone != null && EF.Functions.Like(c.Phone, pattern)));
        }
        var customers = await q.OrderBy(c => c.Name).ToListAsync(ct);
        var ids = customers.Select(c => c.Id).ToList();

        // Aggregate in memory: SQLite stores decimals as TEXT and cannot SUM them exactly.
        var visits = await db.Sessions.AsNoTracking()
            .Where(s => s.CustomerId != null && ids.Contains(s.CustomerId.Value) && s.Status == SessionStatus.Completed)
            .Select(s => new { CustomerId = s.CustomerId!.Value, s.Total, s.StartTime })
            .ToListAsync(ct);
        var stats = visits.GroupBy(v => v.CustomerId).ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Sum(x => x.Total), Last: g.Max(x => x.StartTime)));

        return customers.Select(c =>
        {
            var s = stats.GetValueOrDefault(c.Id);
            return new CustomerDto(c.Id, c.Name, c.Phone, c.Notes, s.Count, s.Total, s.Count > 0 ? s.Last : null, c.CreatedAt);
        }).ToList();
    }

    public async Task<CustomerDto> SaveAsync(SaveCustomerRequest r, CancellationToken ct = default)
    {
        var name = Required(r.Name, "Customer name", 128);
        var phone = Optional(r.Phone, 32);
        if (phone is not null && !phone.All(ch => char.IsDigit(ch) || ch is ' ' or '+' or '-' or '(' or ')'))
            throw new BusinessException("Phone number can contain only digits, spaces and + - ( ).");

        await using var db = await OpenAsync(ct);
        if (phone is not null && await db.Customers.AnyAsync(c => !c.IsDeleted && c.Id != (r.Id ?? 0) && c.Phone == phone, ct))
            throw new BusinessException("Another customer already has this phone number.");

        Customer c;
        if (r.Id is { } id)
            c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Customer not found.");
        else
        {
            c = new Customer { CreatedAt = Clock.Now };
            db.Customers.Add(c);
        }
        c.Name = name;
        c.Phone = phone;
        c.Notes = Optional(r.Notes, 1000);
        await db.SaveChangesAsync(ct);
        return (await GetAllAsync(null, ct)).First(x => x.Id == c.Id);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Customer not found.");
        c.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }
}
