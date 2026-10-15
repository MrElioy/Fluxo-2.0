namespace Fluxo.Core.Domain;

// Rechazo esperado de negocio. Code permite identificarlo sin depender del texto del mensaje.
// Los fallos técnicos de persistencia no se convierten en rechazos de dominio.
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Invariantes comunes de las entidades y comandos; no sustituyen las reglas que necesitan contexto.
internal static class Guard
{
    public static Guid Id(Guid value, string name) => value != Guid.Empty
        ? value : throw new DomainException("InvalidId", $"{name} no puede estar vacío.");

    public static string Text(string value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value.Trim() : throw new DomainException("RequiredField", $"{name} es obligatorio.");
}
