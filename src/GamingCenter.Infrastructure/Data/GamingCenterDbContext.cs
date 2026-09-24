using GamingCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GamingCenter.Infrastructure.Data;

public sealed class GamingCenterDbContext(DbContextOptions<GamingCenterDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GamingStationType> StationTypes => Set<GamingStationType>();
    public DbSet<GamingStation> Stations => Set<GamingStation>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<GamingSession> Sessions => Set<GamingSession>();
    public DbSet<SessionPause> SessionPauses => Set<SessionPause>();
    public DbSet<SessionProduct> SessionProducts => Set<SessionProduct>();
    public DbSet<SessionRateChange> SessionRateChanges => Set<SessionRateChange>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<ApplicationSetting> Settings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(64).UseCollation("NOCASE");
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<GamingStationType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Tag).HasMaxLength(8);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<GamingStation>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Brand).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(64);
            e.Property(x => x.Location).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ImagePath).HasMaxLength(260);
            e.Property(x => x.ReservedFor).HasMaxLength(128);
            e.HasOne(x => x.StationType).WithMany().HasForeignKey(x => x.StationTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => new { x.IsDeleted, x.IsActive });
        });

        b.Entity<PriceHistory>(e =>
        {
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ChangedBy).WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.StationId, x.ChangedAt });
        });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.Phone);
        });

        b.Entity<ProductCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.ImagePath).HasMaxLength(260);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Name);
            e.Ignore(x => x.IsLowStock);
        });

        b.Entity<StockMovement>(e =>
        {
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ProductId, x.At });
            e.Property(x => x.Note).HasMaxLength(256);
        });

        b.Entity<GamingSession>(e =>
        {
            e.Property(x => x.StationName).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StartedBy).WithMany().HasForeignKey(x => x.StartedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Pauses).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Products).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RateChanges).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Payment).WithOne(x => x.Session).HasForeignKey<Payment>(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.StartTime);
            e.HasIndex(x => new { x.StationId, x.Status });
            e.HasIndex(x => x.CustomerId);
            e.Ignore(x => x.Rules);
            e.Ignore(x => x.OpenPause);
        });

        b.Entity<SessionProduct>(e =>
        {
            e.Property(x => x.ProductName).HasMaxLength(128);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ProductId);
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.ReceiptNumber).HasMaxLength(32);
            e.HasIndex(x => x.ReceiptNumber).IsUnique();
            e.HasIndex(x => x.PaidAt);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.PaidNow);
            e.HasMany(x => x.Parts).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CreditTransaction>(e =>
        {
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Note).HasMaxLength(256);
            e.HasIndex(x => new { x.CustomerId, x.At });
            e.HasIndex(x => x.At);
        });

        b.Entity<ApplicationSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
        });
    }
}
