using Fluxo.Core.Application;
using Fluxo.Core.Domain;
using Fluxo.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class SqlitePersistenceTests
{
    public static async Task RunAsync()
    {
        // Archivo aislado por ejecución: prueba el proveedor real, no EF InMemory ni una conexión compartida.
        var directory = Path.Combine(Path.GetTempPath(), "fluxo-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "test.db"), ForeignKeys = true, Pooling = false, DefaultTimeout = 10
        }.ToString();
        try
        {
            await VerifyAsync(connectionString);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task VerifyAsync(string cs)
    {
        var user = Guid.NewGuid();
        var store = new SqliteFinancialStore(cs);
        await store.InitializeAsync(user, "Local", "ARS");
        await store.InitializeAsync(user, "No sobrescribir", "USD");
        Check(await Scalar(cs, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 1, "Migración reaplicable");
        Check(await Scalar(cs, "SELECT COUNT(*) FROM Users WHERE Name = 'Local'") == 1, "Usuario inicial estable");
        var accounts = new AccountHandler(store);
        var account = await accounts.CreateAsync(user, "Banco", "ars", 1234.123456789012345678901234m);
        var category = new Category(Guid.NewGuid(), user, "Comida");
        var otherCategory = new Category(Guid.NewGuid(), user, "Trabajo");
        await store.SaveCategoryAsync(category);
        await store.SaveCategoryAsync(otherCategory);
        var when = new DateTimeOffset(2026, 10, 15, 12, 30, 45, TimeSpan.FromHours(-3)).AddTicks(1234567);
        var command = new RegisterExpenseCommand(Guid.NewGuid(), user, account.Id, 0.123456789012345678901234m,
            when, " Prueba ", new[] { category.Id, otherCategory.Id, category.Id });
        var expense = await new ExpenseHandler(store).HandleAsync(command);

        // Otra instancia debe reconstruir exactamente los datos, sin depender de objetos en memoria.
        store = new SqliteFinancialStore(cs);
        var restored = await store.FindExpenseAsync(user, command.OperationId) ?? throw new Exception("Gasto perdido");
        Check(restored.Id == expense.Id && restored.Amount == command.Amount, "Importe exacto tras reabrir");
        Check(restored.OccurredAt.EqualsExact(when) && restored.RegisteredAt.EqualsExact(expense.RegisteredAt), "Fechas, offset y ticks exactos");
        Check(restored.CategoryIds.Count == 2, "Asociación N:N sin duplicados");
        Check((await new AccountHandler(store).GetBalanceAsync(user, account.Id)).Amount == 1234m, "Saldo decimal exacto");
        Check((await new ExpenseHandler(store).HandleAsync(command with { CategoryIds = new[] { otherCategory.Id, category.Id } })).Id == expense.Id,
            "Reintento recupera resultado persistido");
        await Reject("OperationConflict", () => new ExpenseHandler(store).HandleAsync(command with { Amount = 1 }));
        await Throws<DbUpdateException>(() => store.SaveAccountAsync(account));
        Check((await store.GetAccountAsync(account.Id))!.OpeningBalance == account.OpeningBalance, "Cuenta duplicada no sobrescribe apertura");
        await Throws<DbUpdateException>(() => store.SaveAccountAsync(new Account(Guid.NewGuid(), Guid.NewGuid(), "Sin usuario", "ARS", 0)));

        // Saltamos las lecturas del handler para verificar la revalidación dentro de la transacción.
        Movement Build(RegisterExpenseCommand cmd, string currency = "ARS") => new(Guid.NewGuid(), cmd.AccountId,
            cmd.Amount, currency, MovementType.Expense, cmd.OccurredAt, DateTimeOffset.UtcNow, cmd.Description, cmd.CategoryIds);
        var next = command with { OperationId = Guid.NewGuid() };
        await Reject("InvalidMovementAccount", () => store.SaveExpenseAsync(next, Build(next, "USD")));
        var unknown = next with { CategoryIds = new[] { Guid.NewGuid() } };
        await Reject("CategoryNotFound", () => store.SaveExpenseAsync(unknown, Build(unknown)));
        var foreignUser = Guid.NewGuid();
        await store.InitializeAsync(foreignUser, "Otro", "ARS");
        var foreign = new Category(Guid.NewGuid(), foreignUser, "Ajena");
        await store.SaveCategoryAsync(foreign);
        var foreignCommand = next with { CategoryIds = new[] { foreign.Id } };
        await Reject("CategoryNotFound", () => store.SaveExpenseAsync(foreignCommand, Build(foreignCommand)));
        var wrongUser = next with { UserId = foreignUser };
        await Reject("AccountNotFound", () => store.SaveExpenseAsync(wrongUser, Build(wrongUser)));
        await Execute(cs, "UPDATE Accounts SET IsArchived = 1 WHERE Id = $id", account.Id);
        await Reject("AccountArchived", () => store.SaveExpenseAsync(next, Build(next)));
        Check((await new ExpenseHandler(store).HandleAsync(command)).Id == expense.Id, "Reintento de cuenta archivada");
        await Execute(cs, "UPDATE Accounts SET IsArchived = 0 WHERE Id = $id", account.Id);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Throws<OperationCanceledException>(() => store.SaveExpenseAsync(next, Build(next), canceled.Token));
        Check(await store.FindExpenseAsync(user, next.OperationId) is null, "Cancelación sin efectos");

        // Fallo inducido al insertar la identidad: EF ya puede haber insertado movimiento y categorías.
        await Execute(cs, "CREATE TRIGGER RejectOperation BEFORE INSERT ON ExpenseOperations BEGIN SELECT RAISE(ABORT, 'fallo de prueba'); END;");
        await Throws<DbUpdateException>(() => store.SaveExpenseAsync(next, Build(next)));
        Check(await Scalar(cs, "SELECT COUNT(*) FROM Movements") == 1, "Rollback de movimiento");
        Check(await Scalar(cs, "SELECT COUNT(*) FROM MovementCategories") == 2, "Rollback de asociaciones");
        Check(await store.FindExpenseAsync(user, next.OperationId) is null, "Rollback de identidad");
        await Execute(cs, "DROP TRIGGER RejectOperation;");

        // Escritores con contextos/conexiones independientes, lanzados desde hilos reales.
        using var start = new ManualResetEventSlim(false);
        var jobs = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            var independent = new SqliteFinancialStore(cs);
            return independent.SaveExpenseAsync(next, Build(next)).GetAwaiter().GetResult();
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(jobs);
        Check(results.Select(m => m.Id).Distinct().Count() == 1, "Escritores simultáneos devuelven mismo resultado");
        Check(await Scalar(cs, "SELECT COUNT(*) FROM Movements") == 2, "Sin duplicados concurrentes");
        await Reject("OperationConflict", () => store.SaveExpenseAsync(next with { Amount = 2 }, Build(next with { Amount = 2 })));

        // Misma clave con contenidos distintos: solo uno puede confirmarse incluso si llegan juntos.
        var contested = next with { OperationId = Guid.NewGuid() };
        using var conflictStart = new ManualResetEventSlim(false);
        var contenders = new[] { 3m, 4m }.Select(amount => Task.Run(() =>
        {
            conflictStart.Wait();
            var request = contested with { Amount = amount };
            try
            {
                new SqliteFinancialStore(cs).SaveExpenseAsync(request, Build(request)).GetAwaiter().GetResult();
                return "confirmed";
            }
            catch (DomainException exception) when (exception.Code == "OperationConflict") { return "conflict"; }
        })).ToArray();
        conflictStart.Set();
        var outcomes = await Task.WhenAll(contenders);
        Check(outcomes.Count(value => value == "confirmed") == 1 && outcomes.Count(value => value == "conflict") == 1,
            "Misma clave concurrente con datos distintos");

        // Constraints de la base: se verifican directamente, fuera del handler y del store.
        await Throws<SqliteException>(() => Execute(cs, "DELETE FROM Accounts WHERE Id = $id", account.Id));
        await Throws<SqliteException>(() => Execute(cs, "UPDATE Movements SET Amount = '-1'"));
        await Throws<SqliteException>(() => Execute(cs, "UPDATE Movements SET Type = 99"));
        await Throws<SqliteException>(() => Execute(cs, "UPDATE MovementCategories SET CategoryId = $id", Guid.NewGuid()));
        Check(await Scalar(cs, "PRAGMA foreign_keys") == 1, "Claves foráneas activas");
        await using var db = new FluxoDbContext(new DbContextOptionsBuilder<FluxoDbContext>().UseSqlite(cs).Options);
        Check(!db.Database.HasPendingModelChanges(), "Modelo y snapshot de migración alineados");
        Console.WriteLine("OK: SQLite real, migraciones, precisión, referencias, rollback, cancelación y reintentos concurrentes.");
    }

    private static async Task Execute(string cs, string sql, Guid? id = null)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string cs, string sql)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static async Task Reject(string code, Func<Task> action)
    {
        try { await action(); }
        catch (DomainException exception) when (exception.Code == code) { return; }
        throw new Exception("Se esperaba " + code);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Se esperaba " + typeof(T).Name);
    }
}
