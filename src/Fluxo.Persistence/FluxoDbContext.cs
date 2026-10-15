using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fluxo.Persistence;

public sealed class FluxoDbContext(DbContextOptions<FluxoDbContext> options) : DbContext(options)
{
    internal DbSet<UserRecord> Users => Set<UserRecord>();
    internal DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    internal DbSet<CategoryRecord> Categories => Set<CategoryRecord>();
    internal DbSet<MovementRecord> Movements => Set<MovementRecord>();
    internal DbSet<ExpenseOperationRecord> ExpenseOperations => Set<ExpenseOperationRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // TEXT conserva todos los dígitos de decimal. Las sumas se hacen en el dominio, nunca como REAL.
        var money = new ValueConverter<decimal, string>(
            value => value.ToString(CultureInfo.InvariantCulture),
            value => decimal.Parse(value, CultureInfo.InvariantCulture));
        // Formato round-trip: conserva precisión y offset. No se ordenan fechas como texto en SQL.
        var instant = new ValueConverter<DateTimeOffset, string>(
            value => value.ToString("O", CultureInfo.InvariantCulture),
            value => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture));

        model.Entity<UserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CreatedAt).HasConversion(instant);
        });
        model.Entity<AccountRecord>(entity =>
        {
            entity.ToTable("Accounts");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.OpeningBalance).HasConversion(money);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CategoryRecord>(entity =>
        {
            entity.ToTable("Categories");
            entity.HasKey(row => row.Id);
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MovementRecord>(entity =>
        {
            entity.ToTable("Movements", table =>
            {
                // Además del guardado validado, SQLite rechaza magnitudes no positivas y tipos desconocidos.
                table.HasCheckConstraint("CK_Movements_Amount", "CAST(Amount AS NUMERIC) > 0");
                table.HasCheckConstraint("CK_Movements_Type", "Type IN (0, 1)");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Amount).HasConversion(money);
            entity.Property(row => row.OccurredAt).HasConversion(instant);
            entity.Property(row => row.RegisteredAt).HasConversion(instant);
            entity.HasOne<AccountRecord>().WithMany().HasForeignKey(row => row.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MovementCategoryRecord>(entity =>
        {
            entity.ToTable("MovementCategories");
            entity.HasKey(row => new { row.MovementId, row.CategoryId });
            entity.HasOne<MovementRecord>().WithMany(row => row.Categories)
                .HasForeignKey(row => row.MovementId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CategoryRecord>().WithMany().HasForeignKey(row => row.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ExpenseOperationRecord>(entity =>
        {
            entity.ToTable("ExpenseOperations");
            entity.HasKey(row => new { row.UserId, row.OperationId });
            entity.HasIndex(row => row.MovementId).IsUnique();
            entity.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.Movement).WithOne().HasForeignKey<ExpenseOperationRecord>(row => row.MovementId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
