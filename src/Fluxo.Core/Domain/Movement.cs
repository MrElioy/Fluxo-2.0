namespace Fluxo.Core.Domain;

// Tipos soportados por esta primera base; aún no modelan transferencias, préstamos ni inversiones.
public enum MovementType { Expense, Income }

// Hecho financiero efectivo e inmutable, con importe positivo y dirección determinada por Type.
public sealed class Movement
{
    public Guid Id { get; }
    public Guid AccountId { get; }
    public decimal Amount { get; }
    public string Currency { get; }
    public MovementType Type { get; }
    // Fecha del hecho informada por el cliente; puede diferir de la fecha en que Fluxo lo registró.
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset RegisteredAt { get; }
    public string Description { get; }
    public IReadOnlyList<Guid> CategoryIds { get; }

    public Movement(Guid id, Guid accountId, decimal amount, string currency,
        MovementType type, DateTimeOffset occurredAt, DateTimeOffset registeredAt,
        string description, IEnumerable<Guid> categoryIds)
    {
        Id = Guard.Id(id, nameof(id));
        AccountId = Guard.Id(accountId, nameof(accountId));
        // El signo no se guarda en Amount: Account.CalculateBalance interpreta el tipo.
        if (amount <= 0)
            throw new DomainException("InvalidAmount", "El importe debe ser mayor que cero.");
        // Un enum puede recibir valores numéricos desconocidos; se rechazan explícitamente.
        if (!Enum.IsDefined(type))
            throw new DomainException("InvalidMovementType", "Tipo de movimiento inválido.");
        Amount = amount;
        Currency = Guard.Text(currency, nameof(currency)).ToUpperInvariant();
        Type = type;
        OccurredAt = occurredAt;
        RegisteredAt = registeredAt;
        Description = description?.Trim() ?? "";
        ArgumentNullException.ThrowIfNull(categoryIds);
        // Copiar y exponer solo lectura evita que el llamador modifique las asociaciones después.
        // Ordenar y deduplicar permite comparar categorías como conjunto en los reintentos.
        CategoryIds = Array.AsReadOnly(categoryIds.Select(id => Guard.Id(id, "categoryId"))
            .Distinct().Order().ToArray());
    }
}
