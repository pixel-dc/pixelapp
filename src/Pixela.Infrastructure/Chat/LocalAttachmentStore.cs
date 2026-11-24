using Pixela.Application.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Chat;

/// <summary>Guarda los adjuntos como archivos en una carpeta del servidor: <c>&lt;raíz&gt;/&lt;negocio&gt;/&lt;id&gt;</c>, sin extensión ni nombre original.</summary>
public sealed class LocalAttachmentStore(IOptions<MetaOptions> options) : IAttachmentStore
{
    private string Root => Path.GetFullPath(options.Value.AttachmentsPath);

    private string FileOf(Guid tenantId, Guid id) => Path.Combine(Root, tenantId.ToString("N"), id.ToString("N"));

    public async Task SaveAsync(Guid tenantId, Guid id, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var path = FileOf(tenantId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, ct);
    }

    public Stream? OpenRead(Guid tenantId, Guid id)
    {
        var path = FileOf(tenantId, id);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Delete(Guid tenantId, Guid id)
    {
        try
        {
            File.Delete(FileOf(tenantId, id));
        }
        catch (IOException)
        {
            // Si no se pudo borrar ahora, lo recoge el barrido por antigüedad.
        }
    }

    public bool Move(Guid fromTenantId, Guid toTenantId, Guid id)
    {
        try
        {
            var from = FileOf(fromTenantId, id);
            if (!File.Exists(from))
            {
                return false;
            }

            var to = FileOf(toTenantId, id);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public int SweepOlderThan(DateTimeOffset cutoff)
    {
        var root = Root;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // En uso o ya borrado: se intenta en la próxima pasada.
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException)
            {
            }
        }

        return deleted;
    }
}
