using Fluxo.Core.Application;
using Fluxo.Core.Domain;
using Fluxo.Core.Persistence;

// Pruebas ejecutables con dotnet run: cualquier aserción fallida termina con una excepción.
var store = new MemoryStore();
var userId = Guid.NewGuid();
var accounts = new AccountHandler(store);
var expenses = new ExpenseHandler(store);
var account = await accounts.CreateAsync(userId, "Banco", "ars", 1000.25m);
var category = new Category(Guid.NewGuid(), userId, "Comida");
store.Categories.Add(category.Id, category);
var secondCategory = new Category(Guid.NewGuid(), userId, "Trabajo");
store.Categories.Add(secondCategory.Id, secondCategory);
var command = new RegisterExpenseCommand(Guid.NewGuid(), userId, account.Id, 100.10m,
    DateTimeOffset.UtcNow, " Almuerzo ", new[] { category.Id, secondCategory.Id, category.Id });
// Recorrido principal: apertura de cuenta, gasto multicategoría y saldo con precisión decimal.
var expense = await expenses.HandleAsync(command);
Check(expense.Amount == 100.10m && expense.Type == MovementType.Expense, "Importe positivo y tipo gasto");
Check(expense.Currency == "ARS" && expense.CategoryIds.Count == 2, "Moneda y categorías únicas");
Check((await accounts.GetBalanceAsync(userId, account.Id)).Amount == 900.15m, "Saldo exacto");
// El mismo UUID admite reintentos equivalentes; cambiar su importe debe provocar conflicto.
Check((await expenses.HandleAsync(command with { CategoryIds = new[] { secondCategory.Id, category.Id } })).Id == expense.Id && store.Movements.Count == 1, "Reintento sin duplicados, independiente del orden de categorías");
await Reject("OperationConflict", () => expenses.HandleAsync(command with { Amount = 101m }));
// Los rechazos siguientes no deben producir movimientos adicionales.
await Reject("InvalidAmount", () => expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), Amount = 0 }));
await Reject("InvalidAmount", () => expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), Amount = -1 }));
await Reject("AccountNotFound", () => expenses.HandleAsync(command with { UserId = Guid.NewGuid() }));
await Reject("AccountNotFound", () => expenses.HandleAsync(command with { AccountId = Guid.NewGuid(), OperationId = Guid.NewGuid() }));
await Reject("CategoryNotFound", () => expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), CategoryIds = new[] { Guid.NewGuid() } }));
var foreignCategory = new Category(Guid.NewGuid(), Guid.NewGuid(), "Ajena");
store.Categories.Add(foreignCategory.Id, foreignCategory);
await Reject("CategoryNotFound", () => expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), CategoryIds = new[] { foreignCategory.Id } }));
var archived = new Account(Guid.NewGuid(), userId, "Vieja", "ARS", 0, true);
store.Accounts.Add(archived.Id, archived);
await Reject("AccountArchived", () => expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), AccountId = archived.Id }));
Check((await accounts.GetBalanceAsync(userId, archived.Id)).Amount == 0, "Consulta de cuenta archivada");
await expenses.HandleAsync(command with { OperationId = Guid.NewGuid(), Amount = 2000m, CategoryIds = Array.Empty<Guid>() });
Check((await accounts.GetBalanceAsync(userId, account.Id)).Amount == -1099.85m, "Permite saldo negativo en hechos efectivos");
Check(store.Movements.Count == 2, "Rechazos sin escrituras");
// Un fallo técnico no puede convertirse en confirmación ni confundirse con un recurso inexistente.
store.FailWrite = true;
try
{
    await expenses.HandleAsync(command with { OperationId = Guid.NewGuid() });
    throw new Exception("Se comunicó éxito ante un fallo de escritura");
}
catch (IOException) { }
Check(store.Movements.Count == 2, "Fallo de guardado sin éxito ni efectos");
store.FailRead = true;
try
{
    await accounts.GetBalanceAsync(userId, account.Id);
    throw new Exception("Se ocultó un fallo de lectura");
}
catch (IOException) { }
Console.WriteLine("OK: recorrido cuenta/gasto/saldo, reintentos, validaciones y fallos técnicos.");

await SqlitePersistenceTests.RunAsync();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Reject(string code, Func<Task> action)
{
    try { await action(); }
    catch (DomainException exception) when (exception.Code == code) { return; }
    throw new Exception($"Se esperaba rechazo {code}");
}

// Doble de pruebas, exclusivo de este ejecutable. No implementa todas las garantías del puerto:
// no valida moneda/categorías al guardar ni cancelación, y no prueba transacciones reales de SQLite.
sealed class MemoryStore : IFinancialStore
{
    public Dictionary<Guid, Account> Accounts { get; } = new();
    public Dictionary<Guid, Category> Categories { get; } = new();
    public List<Movement> Movements { get; } = new();
    private readonly Dictionary<(Guid, Guid), Movement> operations = new();
    public bool FailWrite { get; set; }
    public bool FailRead { get; set; }

    public Task<Account?> GetAccountAsync(Guid id, CancellationToken ct)
    {
        if (FailRead) throw new IOException("Lectura fallida");
        return Task.FromResult(Accounts.GetValueOrDefault(id));
    }
    public Task<Category?> GetCategoryAsync(Guid id, CancellationToken ct) => Task.FromResult(Categories.GetValueOrDefault(id));
    public Task<IReadOnlyList<Movement>> GetMovementsAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Movement>>(Movements.Where(m => m.AccountId == id).ToArray());
    public Task SaveAccountAsync(Account account, CancellationToken ct)
    {
        Accounts.Add(account.Id, account);
        return Task.CompletedTask;
    }
    public Task<Movement?> FindExpenseAsync(Guid userId, Guid operationId, CancellationToken ct) =>
        Task.FromResult(operations.GetValueOrDefault((userId, operationId)));
    public Task<Movement> SaveExpenseAsync(RegisterExpenseCommand command, Movement movement, CancellationToken ct)
    {
        // Simula el guardado conjunto de identidad y movimiento; no demuestra concurrencia real de SQLite.
        lock (operations)
        {
            if (FailWrite) throw new IOException("Guardado fallido");
            if (operations.TryGetValue((command.UserId, command.OperationId), out var previous))
            {
                ExpenseHandler.EnsureSameExpense(command, previous);
                return Task.FromResult(previous);
            }
            Accounts[movement.AccountId].EnsureCanRegisterExpense(command.UserId);
            operations.Add((command.UserId, command.OperationId), movement);
            Movements.Add(movement);
            return Task.FromResult(movement);
        }
    }
}
