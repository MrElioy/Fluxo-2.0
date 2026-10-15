namespace Fluxo.Core.Domain;

// Clasificación dinámica perteneciente a un usuario; agregar categorías no requiere cambiar un enum.
// Esta entidad valida sus propios campos; el handler comprueba su existencia y pertenencia al usarla.
public sealed class Category
{
    public Guid Id { get; }
    public Guid UserId { get; }
    public string Name { get; }

    public Category(Guid id, Guid userId, string name)
    {
        Id = Guard.Id(id, nameof(id));
        UserId = Guard.Id(userId, nameof(userId));
        Name = Guard.Text(name, nameof(name));
    }
}
