using Fluxo.Core.Application;
using Fluxo.Core.Domain;

namespace Fluxo.Core.Persistence;

// Puerto definido por el core; Persistence deberá implementarlo sin exponer tipos de SQLite.
// null significa ausencia real. Un fallo de lectura debe propagarse como excepción.
// Las operaciones de E/S deben respetar el CancellationToken recibido.
public interface IFinancialStore
{
    Task<Account?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken);
    // Historial efectivo completo y sin duplicados de la cuenta, para calcular su saldo.
    Task<IReadOnlyList<Movement>> GetMovementsAsync(Guid accountId, CancellationToken cancellationToken);

    // Guarda la cuenta una sola vez; un ID existente se rechaza sin sobrescribir historial.
    Task SaveAccountAsync(Account account, CancellationToken cancellationToken);

    // Recupera el resultado confirmado por (usuario, operación), no por ID de movimiento.
    Task<Movement?> FindExpenseAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);

    // Confirma movimiento, categorías e identidad (usuario, operación) atómicamente.
    // Ante concurrencia devuelve el movimiento ya confirmado si coincide con el comando;
    // si los datos difieren lanza DomainException con código OperationConflict.
    // Revalida propietario, estado, moneda y categorías bajo la misma transacción.
    // Los fallos técnicos se propagan como excepciones, nunca como ausencia o éxito.
    Task<Movement> SaveExpenseAsync(RegisterExpenseCommand command, Movement movement,
        CancellationToken cancellationToken);
}
