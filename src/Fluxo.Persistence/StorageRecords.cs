namespace Fluxo.Persistence;

// Representación física: estas clases pertenecen a EF y no se exponen como contratos de la API.
internal sealed class UserRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string PreferredCurrency { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AccountRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal OpeningBalance { get; set; }
    public bool IsArchived { get; set; }
}

internal sealed class CategoryRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class MovementRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public int Type { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public string Description { get; set; } = "";
    public List<MovementCategoryRecord> Categories { get; set; } = new();
}

internal sealed class MovementCategoryRecord
{
    public Guid MovementId { get; set; }
    public Guid CategoryId { get; set; }
}

// Identidad de la solicitud separada del ID financiero; permite recuperar el resultado confirmado.
internal sealed class ExpenseOperationRecord
{
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public Guid MovementId { get; set; }
    public MovementRecord Movement { get; set; } = null!;
}
