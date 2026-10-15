namespace Fluxo.Core.Domain;

// Cuenta inmutable: no permite cambiar la moneda y reinterpretar el historial existente.
public sealed class Account
{
    public Guid Id { get; }
    public Guid UserId { get; }
    public string Name { get; }
    public string Currency { get; }
    // Puede ser negativo; no hay una regla de fondos suficientes para hechos ya ocurridos.
    public decimal OpeningBalance { get; }
    public bool IsArchived { get; }

    public Account(Guid id, Guid userId, string name, string currency,
        decimal openingBalance, bool isArchived = false)
    {
        Id = Guard.Id(id, nameof(id));
        UserId = Guard.Id(userId, nameof(userId));
        Name = Guard.Text(name, nameof(name));
        Currency = Guard.Text(currency, nameof(currency)).ToUpperInvariant();
        OpeningBalance = openingBalance;
        IsArchived = isArchived;
    }

    // Política inicial: solo el propietario puede registrar gastos y la cuenta debe estar activa.
    public void EnsureCanRegisterExpense(Guid userId)
    {
        if (UserId != userId)
            throw new DomainException("AccountNotFound", "Cuenta no encontrada.");
        if (IsArchived)
            throw new DomainException("AccountArchived", "La cuenta está archivada.");
    }

    // Saldo de caja = apertura + ingresos - gastos efectivos; no calcula patrimonio ni proyecciones.
    // Quien llama debe proveer el historial completo, sin duplicados ni el saldo inicial como movimiento.
    public decimal CalculateBalance(IEnumerable<Movement> movements)
    {
        var balance = OpeningBalance;
        foreach (var movement in movements)
        {
            // Rechazar datos mezclados evita presentar como válido un saldo de otra cuenta o moneda.
            if (movement.AccountId != Id || movement.Currency != Currency)
                throw new DomainException("InvalidMovementAccount", "El movimiento no corresponde a la cuenta.");
            balance += movement.Type == MovementType.Expense ? -movement.Amount : movement.Amount;
        }
        return balance;
    }
}
