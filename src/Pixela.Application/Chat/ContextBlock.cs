using System.Text;

namespace Pixela.Application.Chat;

/// <summary>Arma el bloque <c>&lt;contexto&gt;</c> con los fragmentos recuperados. Son datos de terceros (una web, un PDF): no pueden cerrar la etiqueta.</summary>
public static class ContextBlock
{
    public static string Render(IReadOnlyList<KnowledgeHit> hits)
    {
        var sb = new StringBuilder("<contexto>\n");
        foreach (var hit in hits)
        {
            sb.Append("[Fuente: ").Append(Neutralize(hit.SourceTitle)).Append("]\n")
                .Append(Neutralize(hit.Text)).Append("\n---\n");
        }

        return sb.Append("</contexto>").ToString();
    }

    private static string Neutralize(string text) =>
        text.Replace("<contexto", "‹contexto", StringComparison.OrdinalIgnoreCase)
            .Replace("</contexto", "‹/contexto", StringComparison.OrdinalIgnoreCase);
}
