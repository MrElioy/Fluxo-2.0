using Fluxo.Core.Domain;
using Fluxo.Core.Persistence;

namespace Fluxo.Core.Application;

// Coordina lecturas, reglas de dominio y guardado; no implementa transacciones ni acceso a datos.
public sealed class ExpenseHandler(IFinancialStore store, TimeProvider? timeProvider = null)
{
    // El reloj puede reemplazarse en pruebas; la fecha del hecho sigue siendo la del comando.
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<Movement> HandleAsync(RegisterExpenseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Guard.Id(command.OperationId, nameof(command.OperationId));
        Guard.Id(command.UserId, nameof(command.UserId));
        Guard.Id(command.AccountId, nameof(command.AccountId));
        ArgumentNullException.ThrowIfNull(command.CategoryIds);
        // Copia la entrada antes del primer await para que no cambie durante el guardado.
        command = command with { CategoryIds = Array.AsReadOnly(command.CategoryIds.ToArray()) };
        // Recuperar primero el resultado permite reintentar incluso si luego se archivó la cuenta.
        // Esta lectura no evita carreras: SaveExpenseAsync debe garantizar unicidad al guardar.
        var previous = await store.FindExpenseAsync(command.UserId, command.OperationId, cancellationToken);
        if (previous is not null)
        {
            EnsureSameExpense(command, previous);
            return previous;
        }

        // La existencia y pertenencia de la cuenta son reglas del core, aunque la API valide formatos.
        var account = await store.GetAccountAsync(command.AccountId, cancellationToken)
            ?? throw new DomainException("AccountNotFound", "Cuenta no encontrada.");
        account.EnsureCanRegisterExpense(command.UserId);
        // Movement protege sus invariantes; el gasto conserva importe positivo y toma la moneda de la cuenta.
        var movement = new Movement(Guid.NewGuid(), account.Id, command.Amount, account.Currency,
            MovementType.Expense, command.OccurredAt, clock.GetUtcNow(), command.Description, command.CategoryIds);
        // Cada categoría debe existir y ser del usuario; el constructor ya eliminó IDs repetidos.
        foreach (var categoryId in movement.CategoryIds)
        {
            var category = await store.GetCategoryAsync(categoryId, cancellationToken);
            if (category is null || category.UserId != command.UserId)
                throw new DomainException("CategoryNotFound", "Categoría no encontrada.");
        }
        // Solo devolver éxito tras confirmar el guardado. Bajo concurrencia puede volver el movimiento
        // de otra solicitud idéntica, con un ID distinto al generado en este intento.
        // Los errores técnicos y la cancelación se propagan para que la API los adapte.
        var confirmed = await store.SaveExpenseAsync(command, movement, cancellationToken);
        EnsureSameExpense(command, confirmed);
        return confirmed;
    }

    // Compara datos de negocio para distinguir un reintento de una reutilización indebida del UUID.
    // No compara ID ni RegisteredAt porque los genera el servidor. Las categorías son un conjunto:
    // su orden y repeticiones no cambian la solicitud; los espacios externos de descripción tampoco.
    // El store debe buscar previamente por (UserId, OperationId): Movement no contiene esa identidad.
    public static void EnsureSameExpense(RegisterExpenseCommand command, Movement movement)
    {
        if (movement.Type != MovementType.Expense || movement.AccountId != command.AccountId ||
            movement.Amount != command.Amount || movement.OccurredAt != command.OccurredAt ||
            movement.Description != (command.Description?.Trim() ?? "") ||
            !movement.CategoryIds.SequenceEqual(command.CategoryIds.Distinct().Order()))
            throw new DomainException("OperationConflict", "El identificador de operación ya se utilizó con otros datos.");
    }
}
