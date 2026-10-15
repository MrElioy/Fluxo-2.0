using Fluxo.Core.Domain;
using Fluxo.Core.Persistence;

namespace Fluxo.Core.Application;

// Casos de uso de cuentas; delega el cálculo financiero al dominio y el almacenamiento al puerto.
public sealed class AccountHandler(IFinancialStore store)
{
    // El saldo inicial representa el dinero al comenzar el registro, no un ingreso ganado.
    public async Task<Account> CreateAsync(Guid userId, string name, string currency,
        decimal openingBalance, CancellationToken cancellationToken = default)
    {
        var account = new Account(Guid.NewGuid(), userId, name, currency, openingBalance);
        // La cuenta se entrega al llamador únicamente después de confirmar su persistencia.
        await store.SaveAccountAsync(account, cancellationToken);
        return account;
    }

    public async Task<AccountBalance> GetBalanceAsync(Guid userId, Guid accountId,
        CancellationToken cancellationToken = default)
    {
        Guard.Id(userId, nameof(userId));
        Guard.Id(accountId, nameof(accountId));
        var account = await store.GetAccountAsync(accountId, cancellationToken);
        // Un mismo rechazo evita revelar la existencia de cuentas de otro usuario.
        if (account is null || account.UserId != userId)
            throw new DomainException("AccountNotFound", "Cuenta no encontrada.");
        // Archivar impide nuevos gastos, pero conserva el acceso al historial y al saldo.
        // La lectura debe traer todos los movimientos de esta cuenta una sola vez, sin paginación.
        var movements = await store.GetMovementsAsync(accountId, cancellationToken);
        return new AccountBalance(account.Id, account.Currency, account.CalculateBalance(movements));
    }
}

// El saldo siempre se devuelve junto con su moneda; no se mezclan ni convierten monedas.
public sealed record AccountBalance(Guid AccountId, string Currency, decimal Amount);
