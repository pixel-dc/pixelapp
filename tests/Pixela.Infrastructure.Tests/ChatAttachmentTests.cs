using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Pixela.Application.Chat;
using Pixela.Domain.Chat;
using Pixela.Infrastructure.Chat;
using Microsoft.Extensions.Options;

namespace Pixela.Infrastructure.Tests;

/// <summary>Archivos que envían los contactos: descarga segura desde Meta, tipos permitidos y almacenamiento en disco.</summary>
public class ChatAttachmentTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "fc-att-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content, string type = "image/jpeg")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }

    private static MetaGraphClient Client(FakeHandler handler) =>
        new(Options.Create(new MetaOptions { GraphVersion = "v99.0", MaxRetries = 0, RetryDelayMs = 0 }), handler);

    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];

    [Fact]
    public async Task WhatsApp_media_is_looked_up_by_id_and_downloaded_with_the_channel_token()
    {
        var handler = new FakeHandler(
            _ => Json("{\"url\":\"https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=1\",\"mime_type\":\"image/jpeg\",\"file_size\":6}"),
            _ => Bytes(Photo));
        using var client = Client(handler);

        var file = await client.DownloadMediaAsync(ChannelKind.WhatsApp, "tok-1", new InboundAttachment("image", "MEDIA1", null, null, null), 1000, CancellationToken.None);

        Assert.Equal(Photo, file.Content);
        Assert.Equal("image/jpeg", file.MimeType);
        Assert.Equal(2, handler.Received.Count);
        Assert.Equal("https://graph.facebook.com/v99.0/MEDIA1", handler.Received[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer tok-1", handler.Received[0].Request.Headers.Authorization!.ToString());
        Assert.Equal("Bearer tok-1", handler.Received[1].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Messenger_attachments_are_downloaded_without_sending_the_token()
    {
        var handler = new FakeHandler(_ => Bytes(Photo));
        using var client = Client(handler);

        var file = await client.DownloadMediaAsync(ChannelKind.Messenger, "tok-1", new InboundAttachment("image", null, "https://scontent.xx.fbcdn.net/a.jpg", null, null), 1000, CancellationToken.None);

        Assert.Equal(Photo, file.Content);
        Assert.Null(Assert.Single(handler.Received).Request.Headers.Authorization);
    }

    [Theory]
    [InlineData("https://evil.example.com/a.jpg")]
    [InlineData("http://scontent.xx.fbcdn.net/a.jpg")]
    [InlineData("https://fbcdn.net.evil.com/a.jpg")]
    [InlineData("https://user:pass@scontent.xx.fbcdn.net/a.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("no es una dirección")]
    public async Task Attachment_urls_that_are_not_https_meta_servers_are_never_fetched(string url)
    {
        var handler = new FakeHandler(_ => Bytes(Photo));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() =>
            client.DownloadMediaAsync(ChannelKind.Instagram, "tok-1", new InboundAttachment("image", null, url, null, null), 1000, CancellationToken.None));

        Assert.True(ex.Permanent);
        Assert.Empty(handler.Received);
    }

    [Fact]
    public async Task A_whatsapp_lookup_that_points_to_a_foreign_host_does_not_get_the_token()
    {
        var handler = new FakeHandler(_ => Json("{\"url\":\"https://evil.example.com/steal\",\"mime_type\":\"image/jpeg\"}"), _ => Bytes(Photo));
        using var client = Client(handler);

        await Assert.ThrowsAsync<MetaSendException>(() =>
            client.DownloadMediaAsync(ChannelKind.WhatsApp, "tok-1", new InboundAttachment("image", "MEDIA1", null, null, null), 1000, CancellationToken.None));

        Assert.Single(handler.Received); // solo la consulta a Meta; el token nunca viajó al otro servidor
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task A_whatsapp_media_id_with_odd_characters_is_refused(string id)
    {
        var handler = new FakeHandler(_ => Json("{}"));
        using var client = Client(handler);

        await Assert.ThrowsAsync<MetaSendException>(() =>
            client.DownloadMediaAsync(ChannelKind.WhatsApp, "tok-1", new InboundAttachment("image", id, null, null, null), 1000, CancellationToken.None));

        Assert.Empty(handler.Received);
    }

    [Fact]
    public async Task Files_larger_than_the_limit_are_refused()
    {
        var handler = new FakeHandler(_ => Bytes(Photo));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() =>
            client.DownloadMediaAsync(ChannelKind.Messenger, "tok-1", new InboundAttachment("image", null, "https://scontent.xx.fbcdn.net/a.jpg", null, null), 3, CancellationToken.None));

        Assert.True(ex.Permanent);
    }

    [Fact]
    public async Task A_failed_response_from_meta_is_a_permanent_error()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<MetaSendException>(() =>
            client.DownloadMediaAsync(ChannelKind.Messenger, "tok-1", new InboundAttachment("image", null, "https://scontent.xx.fbcdn.net/a.jpg", null, null), 1000, CancellationToken.None));

        Assert.True(ex.Permanent);
    }

    [Fact]
    public async Task The_store_saves_reads_deletes_and_sweeps_old_files_by_modification_date()
    {
        var store = new LocalAttachmentStore(Options.Create(new MetaOptions { AttachmentsPath = dir }));
        var tenant = Guid.CreateVersion7();
        var fresh = Guid.CreateVersion7();
        var old = Guid.CreateVersion7();

        await store.SaveAsync(tenant, fresh, Photo, CancellationToken.None);
        await store.SaveAsync(tenant, old, Photo, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(dir, tenant.ToString("N"), old.ToString("N")), DateTime.UtcNow.AddDays(-10));

        using (var read = store.OpenRead(tenant, fresh))
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(Photo, copy.ToArray());
        }

        Assert.Equal(1, store.SweepOlderThan(DateTimeOffset.UtcNow.AddDays(-9)));
        Assert.Null(store.OpenRead(tenant, old));
        Assert.NotNull(store.OpenRead(tenant, fresh)?.Also(s => s.Dispose()));

        store.Delete(tenant, fresh);
        Assert.Null(store.OpenRead(tenant, fresh));
        Assert.Equal(0, store.SweepOlderThan(DateTimeOffset.UtcNow)); // y la carpeta vacía del negocio se limpia
        Assert.False(Directory.Exists(Path.Combine(dir, tenant.ToString("N"))));
    }

    [Theory]
    [InlineData("image/jpeg; charset=x", "image/jpeg", true)]
    [InlineData("IMAGE/PNG", "image/png", true)]
    [InlineData("application/pdf", "application/pdf", false)]
    [InlineData("text/html", "application/octet-stream", false)]
    [InlineData("image/svg+xml", "application/octet-stream", false)]
    [InlineData("application/javascript", "application/octet-stream", false)]
    [InlineData(null, "application/octet-stream", false)]
    public void Only_safe_types_are_served_with_their_own_type_and_only_images_inline(string? incoming, string expected, bool inline)
    {
        var mime = AttachmentTypes.Normalize(incoming);

        Assert.Equal(expected, mime);
        Assert.Equal(inline, AttachmentTypes.IsInline(mime));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("comprobante \"pago\".pdf", "comprobante pago.pdf")]
    [InlineData("a\r\nb.pdf", "ab.pdf")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void File_names_are_cleaned_of_paths_and_control_characters(string? name, string? expected) =>
        Assert.Equal(expected, AttachmentTypes.SafeFileName(name));
}

internal static class StreamExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
