using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Capture the actual HTTP body, including the Vision text part; no network or OCR process.</summary>
[Collection(WpfCollection.Name)]
public sealed class AiAttachmentOutboundTests
{
    private const string Identity = "Serial Number: SN-PRIVATE-98765\nUUID: 01234567-89ab-cdef-0123-456789abcdef\nC:\\Users\\PrivateOwner\\logs\nCPU 65 °C";
    private static readonly byte[] ImageBytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 255];
    private static string ImageUrl => "data:image/png;base64," + Convert.ToBase64String(ImageBytes);

    private sealed class CaptureHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public List<string> Bodies { get; } = [];
        public List<string> Previews { get; } = [];
        public Func<string>? Preview { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Previews.Add(Preview?.Invoke() ?? "");
            return new HttpResponseMessage(_statuses.Count == 0 ? HttpStatusCode.OK : _statuses.Dequeue())
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Session : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "XinSpectAttachment_" + Guid.NewGuid().ToString("N"));
        public SettingsService Settings { get; } = new(loadFromDisk: false);
        public AiService Ai { get; }

        public Session(CaptureHandler handler)
        {
            Settings.AiProvider = (int)AiProvider.OpenAiCompatible;
            Settings.AiBaseUrl = "http://127.0.0.1:1/v1";
            Settings.AiModel = "test-model";
            Settings.AiApiKey = "";
            Settings.AiStreaming = false;
            Settings.AiKeepHistory = false;
            Settings.AiAgentMode = false;
            Settings.AiSystemPrompt = "test system";
            Settings.AiRedactIdentity = true;
            Settings.AiHistoryTurns = 0;
            Settings.AiRequestBudgetChars = AiRequestBudget.MinChars;
            Ai = new AiService(Settings, Folder) { HandlerOverride = handler };
            handler.Preview = Ai.BuildRequestPreview;
        }

        public void Dispose()
        {
            Ai.HandlerOverride = null;
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
    }

    private static (string Text, string? Image) UserContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Last(m => m.GetProperty("role").GetString() == "user").GetProperty("content");
        if (content.ValueKind == JsonValueKind.String) return (content.GetString()!, null);
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var parts = content.EnumerateArray().ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        return (parts[0].GetProperty("text").GetString()!, parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHttpTextUsesRedactedPreparedTextWithOrWithoutImage(bool withImage)
    {
        var handler = new CaptureHandler();
        using var session = new Session(handler);
        if (withImage) session.Ai.AttachClipboardImage(ImageBytes);

        await session.Ai.SendAsync(Identity);

        var sent = UserContent(Assert.Single(handler.Bodies));
        Assert.DoesNotContain("SN-PRIVATE-98765", sent.Text);
        Assert.DoesNotContain("01234567-89ab-cdef-0123-456789abcdef", sent.Text);
        Assert.DoesNotContain("PrivateOwner", sent.Text);
        Assert.Contains("65 °C", sent.Text);
        Assert.Contains("已遮蔽", sent.Text);
        Assert.Equal(withImage ? ImageUrl : null, sent.Image);
        Assert.Contains(sent.Text, Assert.Single(handler.Previews));
        Assert.DoesNotContain("SN-PRIVATE-98765", handler.Previews[0]);
        Assert.Equal(Identity, session.Ai.Messages.Single(m => m.IsUser).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextFileAndQuestionUseSameBudgetInActualHttpAndPreview(bool withImage)
    {
        var handler = new CaptureHandler();
        using var session = new Session(handler);
        Directory.CreateDirectory(session.Folder);
        string path = Path.Combine(session.Folder, "long-context.txt");
        await File.WriteAllTextAsync(path, new string('x', 30_000) + "FILE-END-MARKER");
        await session.Ai.AttachFileAsync(path);
        if (withImage) session.Ai.AttachClipboardImage(ImageBytes);

        await session.Ai.SendAsync("QUESTION-END-MARKER");

        string raw = session.Ai.Messages.Single(m => m.IsUser).Text;
        var expected = AiRequestBudget.Apply("test system", [raw], AiRequestBudget.MinChars);
        var sent = UserContent(Assert.Single(handler.Bodies));
        Assert.Equal(expected.History[0], sent.Text);
        Assert.Contains("已截斷", sent.Text);
        Assert.DoesNotContain("FILE-END-MARKER", sent.Text);
        Assert.DoesNotContain("QUESTION-END-MARKER", sent.Text);
        Assert.True(sent.Text.Length < raw.Length);
        Assert.Equal(withImage ? ImageUrl : null, sent.Image);
        Assert.Contains(sent.Text, Assert.Single(handler.Previews));
        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(expected.System + expected.Notice,
            doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task RetryRepreparesImageTextWhenRedactionSettingChanges()
    {
        var handler = new CaptureHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var session = new Session(handler);
        session.Settings.AiRedactIdentity = false;
        session.Ai.AttachClipboardImage(ImageBytes);
        await session.Ai.SendAsync(Identity);
        Assert.True(session.Ai.CanRetry);
        Assert.Contains("SN-PRIVATE-98765", UserContent(handler.Bodies[0]).Text);

        session.Settings.AiRedactIdentity = true;
        await session.Ai.RetryLastAsync();

        Assert.Equal(2, handler.Bodies.Count);
        var retried = UserContent(handler.Bodies[1]);
        Assert.DoesNotContain("SN-PRIVATE-98765", retried.Text);
        Assert.DoesNotContain("PrivateOwner", retried.Text);
        Assert.Contains("已遮蔽", retried.Text);
        Assert.Equal(ImageUrl, UserContent(handler.Bodies[0]).Image);
        Assert.Equal(ImageUrl, retried.Image);
        Assert.Contains(retried.Text, handler.Previews[1]);
        Assert.Single(session.Ai.Messages.Where(m => m.IsUser));
        Assert.False(session.Ai.CanRetry);
    }
}
