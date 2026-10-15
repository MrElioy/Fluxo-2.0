using Fluxo.Core.Application;
using Fluxo.Core.Domain;
using Fluxo.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fluxo.Persistence;

// Un contexto y conexión por operación: no se comparte seguimiento de EF entre solicitudes o hilos.
public sealed class SqliteFinancialStore : IFinancialStore
{
    private readonly DbContextOptions<FluxoDbContext> options;

    public SqliteFinancialStore(string connectionString)
    {
        // Fuerza integridad incluso si la cadena recibida intenta desactivar foreign_keys.
        var connection = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true };
        options = new DbContextOptionsBuilder<FluxoDbContext>().UseSqlite(connection.ToString()).Options;
    }

    private FluxoDbContext CreateContext() => new(options);

    // Inicialización explícita: migraciones versionadas y creación del usuario local, sin sobrescribirlo.
    public async Task InitializeAsync(Guid userId, string name, string currency, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            throw new DomainException("InvalidId", "El usuario local es obligatorio.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(currency))
            throw new DomainException("RequiredField", "Nombre y moneda del usuario local son obligatorios.");
        await using var db = CreateContext();
        await db.Database.MigrateAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Users.AnyAsync(row => row.Id == userId, ct))
        {
            db.Users.Add(new UserRecord { Id = userId, Name = name.Trim(),
                PreferredCurrency = currency.Trim().ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<Account?> GetAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        var row = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == accountId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        var row = await db.Categories.AsNoTracking().SingleOrDefaultAsync(row => row.Id == categoryId, ct);
        return row is null ? null : new Category(row.Id, row.UserId, row.Name);
    }

    public async Task<IReadOnlyList<Movement>> GetMovementsAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        var rows = await db.Movements.AsNoTracking().Include(row => row.Categories)
            .Where(row => row.AccountId == accountId).ToListAsync(ct);
        return rows.Select(Map).ToArray();
    }

    public async Task SaveAccountAsync(Account account, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        // INSERT, nunca UPSERT: un ID repetido no modifica moneda ni saldo de apertura del historial.
        db.Accounts.Add(new AccountRecord { Id = account.Id, UserId = account.UserId, Name = account.Name,
            Currency = account.Currency, OpeningBalance = account.OpeningBalance, IsArchived = account.IsArchived });
        await db.SaveChangesAsync(ct);
    }

    // Alta mínima para poblar categorías dinámicas; su caso de uso y endpoint quedan pendientes.
    public async Task SaveCategoryAsync(Category category, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        db.Categories.Add(new CategoryRecord { Id = category.Id, UserId = category.UserId, Name = category.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Movement?> FindExpenseAsync(Guid userId, Guid operationId, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        return await FindExpenseAsync(db, userId, operationId, ct);
    }

    public async Task<Movement> SaveExpenseAsync(RegisterExpenseCommand command, Movement movement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(movement);
        if (command.OperationId == Guid.Empty || command.UserId == Guid.Empty)
            throw new DomainException("InvalidId", "Usuario y operación son obligatorios.");
        command = command with { CategoryIds = command.CategoryIds.ToArray() };
        ExpenseHandler.EnsureSameExpense(command, movement);
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        ct.ThrowIfCancellationRequested();
        // BEGIN IMMEDIATE toma el turno de escritura ANTES de leer la identidad y las reglas.
        // Así otro escritor no puede confirmar el mismo UUID ni archivar la cuenta entre lectura y guardado.
        // Microsoft.Data.Sqlite espera bloqueos hasta Default Timeout; sus operaciones de E/S son síncronas.
        using var sqliteTransaction = connection.BeginTransaction(deferred: false);
        await using var transaction = await db.Database.UseTransactionAsync(sqliteTransaction, ct);
        var previous = await FindExpenseAsync(db, command.UserId, command.OperationId, ct);
        if (previous is not null)
        {
            ExpenseHandler.EnsureSameExpense(command, previous);
            await transaction!.CommitAsync(ct);
            return previous;
        }

        var accountRow = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == movement.AccountId, ct)
            ?? throw new DomainException("AccountNotFound", "Cuenta no encontrada.");
        Map(accountRow).EnsureCanRegisterExpense(command.UserId);
        if (accountRow.Currency != movement.Currency)
            throw new DomainException("InvalidMovementAccount", "La moneda no corresponde a la cuenta.");
        var categoryIds = movement.CategoryIds.ToArray();
        var validCategories = await db.Categories.CountAsync(row => categoryIds.Contains(row.Id)
            && row.UserId == command.UserId, ct);
        if (validCategories != categoryIds.Length)
            throw new DomainException("CategoryNotFound", "Categoría no encontrada.");

        var row = new MovementRecord { Id = movement.Id, AccountId = movement.AccountId, Amount = movement.Amount,
            Currency = movement.Currency, Type = (int)movement.Type, OccurredAt = movement.OccurredAt,
            RegisteredAt = movement.RegisteredAt, Description = movement.Description,
            Categories = categoryIds.Select(id => new MovementCategoryRecord { MovementId = movement.Id, CategoryId = id }).ToList() };
        db.ExpenseOperations.Add(new ExpenseOperationRecord { UserId = command.UserId,
            OperationId = command.OperationId, MovementId = movement.Id, Movement = row });
        // Al salir sin Commit, Dispose revierte también cualquier INSERT ya ejecutado por EF.
        await db.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        await transaction!.CommitAsync(ct);
        return movement;
    }

    private static async Task<Movement?> FindExpenseAsync(FluxoDbContext db, Guid userId, Guid operationId,
        CancellationToken ct)
    {
        var row = await db.ExpenseOperations.AsNoTracking().Include(row => row.Movement).ThenInclude(row => row.Categories)
            .SingleOrDefaultAsync(row => row.UserId == userId && row.OperationId == operationId, ct);
        return row is null ? null : Map(row.Movement);
    }

    private static Account Map(AccountRecord row) => new(row.Id, row.UserId, row.Name, row.Currency,
        row.OpeningBalance, row.IsArchived);

    private static Movement Map(MovementRecord row) => new(row.Id, row.AccountId, row.Amount, row.Currency,
        (MovementType)row.Type, row.OccurredAt, row.RegisteredAt, row.Description, row.Categories.Select(link => link.CategoryId));
}
