using System.Text;
using System.Text.RegularExpressions;

namespace Pixela.Application.Chat;

/// <summary>Parte un texto en fragmentos de ~800 caracteres, respetando párrafos y frases, con un pequeño solape entre fragmentos.</summary>
public static partial class Chunker
{
    public const int TargetSize = 800;
    public const int MaxSize = 1000;
    private const int OverlapSize = 120;

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=[\.\?\!…])\s+")]
    private static partial Regex SentenceBreak();

    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = Spaces().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), " ");
        var paragraphs = normalized.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0);

        var pieces = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            pieces.AddRange(paragraph.Length <= MaxSize ? [paragraph] : BreakLong(paragraph));
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length + 1 > TargetSize)
            {
                chunks.Add(current.ToString());
                current.Clear().Append(Tail(chunks[^1]));
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(piece);
        }

        if (current.Length > 0 && (chunks.Count == 0 || current.Length > OverlapSize))
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// Reconstruye un texto a partir de sus fragmentos (para las fuentes cargadas antes de guardar el original). Los fragmentos no guardan su orden:
    /// se enlazan por el solape (cada uno empieza con el final del anterior) y lo que no enlaza va al final.
    /// </summary>
    public static string Join(IReadOnlyList<string> chunks)
    {
        if (chunks.Count == 0)
        {
            return string.Empty;
        }

        var remaining = chunks.ToList();
        var successorOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasPredecessor = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in remaining)
        {
            var tail = Tail(a);
            if (tail.Length == 0)
            {
                continue;
            }

            var next = remaining.FirstOrDefault(b => !ReferenceEquals(a, b) && b.StartsWith(tail + "\n", StringComparison.Ordinal) && !hasPredecessor.Contains(b));
            if (next is not null)
            {
                successorOf[a] = next;
                hasPredecessor.Add(next);
            }
        }

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in remaining.Where(x => !hasPredecessor.Contains(x)).Concat(remaining))
        {
            for (var cur = start; seen.Add(cur);)
            {
                ordered.Add(cur);
                if (!successorOf.TryGetValue(cur, out var nx))
                {
                    break;
                }

                cur = nx;
            }
        }

        var sb = new StringBuilder(ordered[0]);
        for (var i = 1; i < ordered.Count; i++)
        {
            var tail = Tail(ordered[i - 1]);
            var piece = tail.Length > 0 && ordered[i].StartsWith(tail + "\n", StringComparison.Ordinal) ? ordered[i][(tail.Length + 1)..] : ordered[i];
            sb.Append('\n').Append(piece);
        }

        return sb.ToString();
    }

    /// <summary>Un párrafo demasiado largo se parte por frases y, si una frase sola no cabe, por palabras.</summary>
    private static IEnumerable<string> BreakLong(string paragraph)
    {
        var buffer = new StringBuilder();
        foreach (var sentence in SentenceBreak().Split(paragraph))
        {
            foreach (var part in sentence.Length <= MaxSize ? [sentence] : BreakByWords(sentence))
            {
                if (buffer.Length > 0 && buffer.Length + part.Length + 1 > TargetSize)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                if (buffer.Length > 0)
                {
                    buffer.Append(' ');
                }

                buffer.Append(part);
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static IEnumerable<string> BreakByWords(string sentence)
    {
        var buffer = new StringBuilder();
        foreach (var word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (buffer.Length > 0 && buffer.Length + word.Length + 1 > TargetSize)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(word);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    /// <summary>Final del fragmento anterior (cortado en una palabra) para que una idea partida no pierda su contexto.</summary>
    private static string Tail(string chunk)
    {
        if (chunk.Length <= OverlapSize)
        {
            return string.Empty;
        }

        var tail = chunk[^OverlapSize..];
        var space = tail.IndexOf(' ', StringComparison.Ordinal);
        return space >= 0 ? tail[(space + 1)..] : tail;
    }
}
