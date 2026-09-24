using System.Globalization;
using System.Reflection;
using GamingCenter.Application.Common;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Entities;
using GamingCenter.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Services;

/// <summary>Maps <see cref="AppSettings"/> properties to key/value rows.</summary>
public sealed class SettingsService(IDbContextFactory<GamingCenterDbContext> dbFactory, IClock clock, ICurrentUser currentUser)
    : ServiceBase(dbFactory, clock, currentUser), ISettingsService
{
    private static readonly PropertyInfo[] Props = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = new AppSettings();
        foreach (var p in Props)
        {
            if (!rows.TryGetValue(p.Name, out var raw)) continue;
            try { p.SetValue(settings, Parse(raw, p.PropertyType)); }
            catch (FormatException) { /* keep default for corrupt values */ }
        }
        Current = settings;
        Money.CurrencySymbol = settings.CurrencySymbol;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        RequireAdmin();
        Validate(settings);
        await PersistAsync(settings, ct);
    }

    /// <summary>For background jobs (e.g. backup timestamp) that run without an admin session.</summary>
    internal async Task PersistAsync(AppSettings settings, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Settings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var p in Props)
        {
            var value = Format(p.GetValue(settings));
            if (rows.TryGetValue(p.Name, out var row)) row.Value = value;
            else db.Settings.Add(new ApplicationSetting { Key = p.Name, Value = value });
        }
        await db.SaveChangesAsync(ct);
        Current = settings.Clone();
        Money.CurrencySymbol = Current.CurrencySymbol;
        Changed?.Invoke(this, Current);
    }

    private static void Validate(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.CenterName)) throw new BusinessException("Gaming center name is required.");
        if (string.IsNullOrWhiteSpace(s.CurrencySymbol)) throw new BusinessException("Currency symbol is required.");
        if (Array.IndexOf(Domain.Billing.BillingRules.SupportedUnits, s.BillingUnitSeconds) < 0) throw new BusinessException("Unsupported billing unit.");
        if (s.MinimumChargeMinutes is < 0 or > 240) throw new BusinessException("Minimum charge must be between 0 and 240 minutes.");
        if (s.MoneyRoundingStep < 0) throw new BusinessException("Money rounding step cannot be negative.");
        if (s.WarnBeforeEndMinutes is < 0 or > 120) throw new BusinessException("Warning time must be between 0 and 120 minutes.");
        if (s.LongSessionAlertHours is < 0 or > 48) throw new BusinessException("Long session alert must be between 0 and 48 hours.");
        if (s.AutoBackupHour is < 0 or > 23) throw new BusinessException("Backup hour must be between 0 and 23.");
        if (s.BackupsToKeep is < 1 or > 365) throw new BusinessException("Backups to keep must be between 1 and 365.");
        if (s.ReceiptWidthMm is < 48 or > 210) throw new BusinessException("Receipt width must be between 48 and 210 mm.");
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static object? Parse(string raw, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            type = underlying;
        }
        // Optional strings come back as "" rather than null; callers test with IsNullOrWhiteSpace.
        if (type == typeof(string)) return raw;
        if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(raw);
        if (type == typeof(DateTime)) return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type.IsEnum) return Enum.Parse(type, raw);
        throw new FormatException($"Unsupported setting type {type}.");
    }
}
