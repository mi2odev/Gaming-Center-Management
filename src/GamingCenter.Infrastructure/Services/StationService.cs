using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Billing;
using GamingCenter.Domain.Entities;
using GamingCenter.Domain.Enums;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

public sealed class StationService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser, ISettingsService settings)
    : ServiceBase(dbFactory, clock, currentUser), IStationService
{
    private StationDto ToDto(GamingStation s) => new(
        s.Id, s.Name, s.Number, s.StationTypeId, s.StationType?.Name ?? "", s.StationType?.Tag ?? "",
        s.Brand, s.Model, s.ImagePath, s.HourlyRate, s.Description, s.Location, s.ControllerCount,
        s.MaxControllers, s.ExtraControllerRate, s.State, s.ReservedFor, s.ReservedAt, s.IsActive,
        ControllerPricing.Resolve(s.ControllerCount, s.MaxControllers, s.ExtraControllerRate,
            settings.Current.DefaultExtraControllerRate, settings.Current.DefaultMaxExtraControllers));

    public async Task<IReadOnlyList<StationDto>> GetAllAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Stations.AsNoTracking().Include(s => s.StationType).Where(s => !s.IsDeleted);
        if (!includeInactive) q = q.Where(s => s.IsActive);
        var list = await q.ToListAsync(ct);
        return list
            .OrderBy(s => s.Location ?? "~")
            .ThenBy(s => s.StationType?.SortOrder ?? 99)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto).ToList();
    }

    public async Task<StationDto?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var s = await db.Stations.AsNoTracking().Include(x => x.StationType).FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return s is null ? null : ToDto(s);
    }

    public async Task<StationDto> SaveAsync(SaveStationRequest r, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = Required(r.Name, "Station name", 64);
        if (r.HourlyRate < 0) throw new BusinessException("Hourly price cannot be negative.");
        if (r.HourlyRate > 1_000_000) throw new BusinessException("Hourly price is too large.");
        if (r.ControllerCount is < 0 or > 16) throw new BusinessException("Controllers must be between 0 and 16.");
        if (r.ExtraControllerRate < 0) throw new BusinessException("Extra controller price cannot be negative.");
        if (r.MaxControllers is { } max && (max < 0 || max > 16)) throw new BusinessException("Maximum controllers must be between 0 and 16.");
        if (r.MaxControllers is { } mx && r.ControllerCount is { } inc && mx < inc)
            throw new BusinessException("Maximum controllers cannot be lower than the included controllers.");
        if (r.Number is < 0) throw new BusinessException("Number cannot be negative.");

        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!await db.StationTypes.AnyAsync(t => t.Id == r.StationTypeId, ct))
            throw new BusinessException("Choose a station type.");
        if (await db.Stations.AnyAsync(s => !s.IsDeleted && s.Id != (r.Id ?? 0) && s.Name.ToLower() == name.ToLower(), ct))
            throw new BusinessException($"A station named \"{name}\" already exists.");

        var now = Clock.Now;
        GamingStation station;
        if (r.Id is { } id)
        {
            station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct)
                ?? throw new BusinessException("Station not found.");
            if (station.HourlyRate != r.HourlyRate)
            {
                // Running sessions keep their locked rate; only new sessions use the new one.
                db.PriceHistory.Add(new PriceHistory
                {
                    StationId = station.Id, OldRate = station.HourlyRate, NewRate = r.HourlyRate,
                    ChangedAt = now, ChangedByUserId = CurrentUserId,
                });
            }
            if (!r.IsActive || r.State == StationState.Maintenance)
                await EnsureNoLiveSessionAsync(db, station, ct);
        }
        else
        {
            station = new GamingStation { CreatedAt = now };
            db.Stations.Add(station);
        }

        station.Name = name;
        station.Number = r.Number;
        station.StationTypeId = r.StationTypeId;
        station.Brand = Optional(r.Brand, 64);
        station.Model = Optional(r.Model, 64);
        station.ImagePath = Optional(r.ImagePath, 260);
        station.HourlyRate = r.HourlyRate;
        station.Description = Optional(r.Description, 500);
        station.Location = Optional(r.Location, 64);
        station.ControllerCount = r.ControllerCount;
        station.MaxControllers = r.MaxControllers;
        station.ExtraControllerRate = r.ExtraControllerRate;
        if (station.State != r.State)
        {
            station.State = r.State;
            if (r.State != StationState.Reserved) { station.ReservedFor = null; station.ReservedAt = null; }
        }
        station.IsActive = r.IsActive;
        station.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        if (r.Id is null)
            db.PriceHistory.Add(new PriceHistory { StationId = station.Id, OldRate = 0, NewRate = station.HourlyRate, ChangedAt = now, ChangedByUserId = CurrentUserId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(station.Id, ct))!;
    }

    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Station not found.");
        if (!active) await EnsureNoLiveSessionAsync(db, s, ct);
        s.IsActive = active;
        s.UpdatedAt = Clock.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Station not found.");
        await EnsureNoLiveSessionAsync(db, s, ct);
        // Soft delete: historical sessions keep pointing at this row.
        s.IsDeleted = true;
        s.IsActive = false;
        s.UpdatedAt = Clock.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStateAsync(int id, StationState state, string? reservedFor = null, DateTime? reservedAt = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var s = await db.Stations.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct) ?? throw new BusinessException("Station not found.");
        if (state != StationState.Available) await EnsureNoLiveSessionAsync(db, s, ct);
        s.State = state;
        s.ReservedFor = state == StationState.Reserved ? Optional(reservedFor, 128) : null;
        s.ReservedAt = state == StationState.Reserved ? reservedAt : null;
        s.UpdatedAt = Clock.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PriceHistoryDto>> GetPriceHistoryAsync(int stationId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.PriceHistory.AsNoTracking().Where(p => p.StationId == stationId)
            .OrderByDescending(p => p.ChangedAt)
            .Select(p => new PriceHistoryDto(p.ChangedAt, p.OldRate, p.NewRate, p.ChangedBy != null ? p.ChangedBy.DisplayName : null))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StationTypeDto>> GetTypesAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var counts = await db.Stations.Where(s => !s.IsDeleted).GroupBy(s => s.StationTypeId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var types = await db.StationTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        return types.Select(t => new StationTypeDto(t.Id, t.Name, t.Tag, t.DefaultHourlyRate, t.SortOrder, t.IsActive, counts.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<StationTypeDto> SaveTypeAsync(SaveStationTypeRequest r, CancellationToken ct = default)
    {
        RequireAdmin();
        var name = Required(r.Name, "Type name", 64);
        var tag = Required(string.IsNullOrWhiteSpace(r.Tag) ? name[..Math.Min(4, name.Length)] : r.Tag, "Tag", 8).ToUpperInvariant();
        if (r.DefaultHourlyRate < 0) throw new BusinessException("Default price cannot be negative.");

        await using var db = await OpenAsync(ct);
        if (await db.StationTypes.AnyAsync(t => t.Id != (r.Id ?? 0) && t.Name.ToLower() == name.ToLower(), ct))
            throw new BusinessException($"A type named \"{name}\" already exists.");

        GamingStationType type;
        if (r.Id is { } id)
            type = await db.StationTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new BusinessException("Type not found.");
        else
        {
            type = new GamingStationType { SortOrder = (await db.StationTypes.MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 1 };
            db.StationTypes.Add(type);
        }
        type.Name = name;
        type.Tag = tag;
        type.DefaultHourlyRate = r.DefaultHourlyRate;
        type.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return (await GetTypesAsync(ct)).First(t => t.Id == type.Id);
    }

    public async Task DeleteTypeAsync(int id, CancellationToken ct = default)
    {
        RequireAdmin();
        await using var db = await OpenAsync(ct);
        var type = await db.StationTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new BusinessException("Type not found.");
        if (await db.Stations.AnyAsync(s => s.StationTypeId == id, ct))
            throw new BusinessException("This type is used by stations (including history). Deactivate it instead.");
        db.StationTypes.Remove(type);
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureNoLiveSessionAsync(GamingCenterDbContext db, GamingStation s, CancellationToken ct)
    {
        bool live = await db.Sessions.AnyAsync(x => x.StationId == s.Id &&
            (x.Status == SessionStatus.Running || x.Status == SessionStatus.Paused || x.Status == SessionStatus.AwaitingPayment), ct);
        if (live) throw new BusinessException($"{s.Name} has a running session. End it first.");
    }
}
