using System.Globalization;
using System.Text;
using Pixela.Application.Chat;
using Pixela.Application.Http;
using Pixela.Infrastructure.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Tests;

public class ChunkerTests
{
    [Fact]
    public void Empty_text_gives_no_chunks()
    {
        Assert.Empty(Chunker.Split(null));
        Assert.Empty(Chunker.Split("   \n\r\n  "));
    }

    [Fact]
    public void A_short_text_is_one_chunk()
    {
        var chunks = Chunker.Split("Abrimos de lunes a viernes.\n\nSábados hasta el mediodía.");

        var chunk = Assert.Single(chunks);
        Assert.Contains("lunes a viernes", chunk, StringComparison.Ordinal);
        Assert.Contains("mediodía", chunk, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_text_is_split_into_bounded_chunks_that_keep_all_the_words()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(i => $"Párrafo {i}: " + string.Join(' ', Enumerable.Repeat("palabra", 25)));
        var chunks = Chunker.Split(string.Join("\n\n", paragraphs));

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= Chunker.MaxSize, $"fragmento de {c.Length} caracteres"));
        for (var i = 1; i <= 40; i++)
        {
            Assert.Contains(chunks, c => c.Contains($"Párrafo {i}:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Consecutive_chunks_overlap_a_little_so_an_idea_split_in_two_keeps_context()
    {
        var chunks = Chunker.Split(string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Línea número {i} con algo de texto de relleno para ocupar espacio.")));

        Assert.True(chunks.Count >= 2);
        var lastLineOfFirst = chunks[0].Split('\n')[^1];
        Assert.Contains(lastLineOfFirst[^20..], chunks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_enormous_sentence_without_breaks_is_still_split_by_words()
    {
        var chunks = Chunker.Split(string.Join(' ', Enumerable.Repeat("larguísima", 600)));

        Assert.True(chunks.Count >= 7);
        Assert.All(chunks, c => Assert.True(c.Length <= Chunker.MaxSize));
    }
}

public class WebPageTextTests
{
    [Fact]
    public void Extracts_the_main_text_without_scripts_menus_or_footers_and_keeps_blocks_apart()
    {
        const string html = """
            <html><head><title>  Café Central &amp; más </title><style>.x{color:red}</style></head>
            <body><nav>Inicio | Contacto</nav><script>alert('x')</script>
            <main><h1>Horario</h1><p>Lunes a viernes</p><p>8 a 18</p></main><footer>© 2026</footer></body></html>
            """;

        var page = SafeWebPageFetcher.ExtractText(html, "fallback.com");

        Assert.Equal("Café Central & más", page.Title);
        Assert.Contains("Horario", page.Text, StringComparison.Ordinal);
        Assert.Contains("Lunes a viernes\n", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Contacto", page.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_the_host_when_the_page_has_no_title()
    {
        Assert.Equal("fallback.com", SafeWebPageFetcher.ExtractText("<body><p>Hola</p></body>", "fallback.com").Title);
    }

    [Fact]
    public async Task The_fetcher_refuses_private_destinations()
    {
        using var fetcher = new SafeWebPageFetcher(Options.Create(new OutboundHttpOptions { AllowPrivateNetworks = false }));

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.FetchAsync(new Uri("http://127.0.0.1:9/secreto"), CancellationToken.None));
    }
}

public class PdfTextTests
{
    /// <summary>Arma un PDF mínimo pero válido (con tabla xref correcta) que contiene el texto dado.</summary>
    private static byte[] BuildPdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 700 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<</Root 1 0 R/Size {objects.Length + 1}>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public void Reads_the_text_of_a_simple_pdf()
    {
        var text = new PdfPigTextExtractor().Extract(new MemoryStream(BuildPdf("Horario de atencion lunes a viernes")), 10);

        Assert.Contains("Horario de atencion lunes a viernes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_pdf_is_reported_as_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => new PdfPigTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes("esto no es un pdf")), 10));
    }
}
