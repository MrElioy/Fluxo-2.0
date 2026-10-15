namespace Fluxo.Core.Application;

// Intención de negocio recibida desde la API; no contiene detalles de HTTP ni SQLite.
// OperationId lo genera el cliente y se conserva en cada reintento; no es el ID del movimiento.
// UserId debe incorporarlo la API desde la identidad configurada, no confiarse al cliente.
// Amount es positivo; la moneda se obtiene de AccountId. OccurredAt indica cuándo ocurrió el hecho.
// Description es opcional en significado (puede ser vacía); CategoryIds admite cero o varias categorías.
public sealed record RegisterExpenseCommand(
    Guid OperationId, Guid UserId, Guid AccountId, decimal Amount,
    DateTimeOffset OccurredAt, string Description, IReadOnlyCollection<Guid> CategoryIds);
