namespace Pixela.Application.Abstractions;

/// <summary>
/// Alcance de datos de la petición actual. Alimenta el filtro global de EF Core: un usuario de un cliente solo ve lo de su cliente;
/// la agencia ve todo o, si eligió un cliente en el selector, solo ese.
/// </summary>
public interface IClientScope
{
    /// <summary>Cliente visible. <see cref="Guid.Empty"/> si no hay ninguno (el filtro no deja pasar nada salvo con <see cref="AllClients"/>).</summary>
    Guid ClientId { get; }

    /// <summary>Vista de agencia sin cliente elegido, workers y migraciones: sin filtro.</summary>
    bool AllClients { get; }
}

public sealed class ClientScope : IClientScope
{
    public Guid ClientId { get; private set; }
    public bool AllClients { get; private set; }

    public void SetClient(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("El cliente no puede ser vacío.", nameof(clientId));
        }

        ClientId = clientId;
        AllClients = false;
    }

    public void SetAllClients()
    {
        ClientId = Guid.Empty;
        AllClients = true;
    }
}
