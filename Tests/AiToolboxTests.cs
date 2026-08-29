using System.IO;
using System.Text.Json;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 診斷代理的工具層測試：工具箱的註冊／結構描述／執行容錯，參數解析的寬鬆處理，
/// 以及對話保存檔的存取。全部不觸網、不碰真實硬體。
/// </summary>
public class AiToolboxTests
{
    private static AiToolbox Box()
    {
        var box = new AiToolbox();
        box.Add("get_x", "取 X", _ => "X 值");
        return box;
    }

    [Fact]
    public void EmptyBox_HasNoTools()
    {
        var box = new AiToolbox();
        Assert.False(box.HasTools);
        Assert.Empty(box.Tools);
        Assert.Empty(box.ToSchema());
    }

    [Fact]
    public void Add_RegistersTool()
    {
        var box = Box();
        Assert.True(box.HasTools);
        Assert.Single(box.Tools);
        Assert.Equal("get_x", box.Tools[0].Name);
    }

    [Fact]
    public void Add_SameName_ReplacesInPlace()
    {
        var box = Box();
        box.Add("get_y", "取 Y", _ => "Y");
        box.Add("get_x", "取 X（新版）", _ => "新 X");

        Assert.Equal(2, box.Tools.Count);
        Assert.Equal("get_x", box.Tools[0].Name);            // 順序不變
        Assert.Equal("取 X（新版）", box.Tools[0].Description);
        Assert.Equal("新 X", box.Invoke("get_x", ""));
    }

    [Fact]
    public void ToSchema_ProducesOpenAiFunctionShape()
    {
        var box = new AiToolbox();
        box.Add("get_events", "取事件", _ => "",
            """{"type":"object","properties":{"count":{"type":"integer"}}}""");

        string json = JsonSerializer.Serialize(box.ToSchema());
        using var doc = JsonDocument.Parse(json);
        var fn = doc.RootElement[0];

        Assert.Equal("function", fn.GetProperty("type").GetString());
        var f = fn.GetProperty("function");
        Assert.Equal("get_events", f.GetProperty("name").GetString());
        Assert.Equal("取事件", f.GetProperty("description").GetString());
        Assert.Equal("object", f.GetProperty("parameters").GetProperty("type").GetString());
        Assert.True(f.GetProperty("parameters").GetProperty("properties").TryGetProperty("count", out _));
    }

    [Fact]
    public void ToSchema_BadSchema_FallsBackToNoParameters()
    {
        var box = new AiToolbox();
        box.Add("broken", "手寫 schema 打錯字", _ => "", "{ this is not json");

        string json = JsonSerializer.Serialize(box.ToSchema());
        using var doc = JsonDocument.Parse(json);
        var parameters = doc.RootElement[0].GetProperty("function").GetProperty("parameters");

        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Empty(parameters.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void Invoke_UnknownTool_ListsAvailableInsteadOfThrowing()
    {
        string text = Box().Invoke("get_nothing", "{}");
        Assert.Contains("get_nothing", text);
        Assert.Contains("get_x", text);           // 告訴模型有哪些可用
    }

    [Fact]
    public void Invoke_IsCaseInsensitive()
        => Assert.Equal("X 值", Box().Invoke("GET_X", ""));

    [Fact]
    public void Invoke_ToolThrows_ReturnsExplanation()
    {
        var box = new AiToolbox();
        box.Add("boom", "會炸的工具", _ => throw new InvalidOperationException("讀值失敗"));

        string text = box.Invoke("boom", "{}");
        Assert.Contains("boom", text);
        Assert.Contains("讀值失敗", text);
    }

    [Fact]
    public void Invoke_EmptyResult_ReturnsPlaceholder()
    {
        var box = new AiToolbox();
        box.Add("blank", "沒有讀值", _ => "");
        Assert.Contains("沒有可用讀值", box.Invoke("blank", "{}"));
    }

    [Fact]
    public void Invoke_PassesArgumentsThrough()
    {
        string? seen = null;
        var box = new AiToolbox();
        box.Add("echo", "回傳參數", a => { seen = a; return "ok"; });

        box.Invoke("echo", """{"keyword":"風扇"}""");
        Assert.Equal("""{"keyword":"風扇"}""", seen);
    }

    // ── 參數解析 ──────────────────────────────────────────────

    [Theory]
    [InlineData("""{"hours":48}""", 48)]
    [InlineData("""{"hours":"48"}""", 48)]      // 模型常把數字寫成字串
    [InlineData("""{"hours":48.6}""", 49)]      // 小數四捨五入
    [InlineData("""{"hours":9999}""", 100)]     // 夾到上限
    [InlineData("""{"hours":-5}""", 1)]         // 夾到下限
    [InlineData("""{"other":3}""", 24)]         // 缺這個鍵
    [InlineData("{}", 24)]
    [InlineData("", 24)]
    [InlineData("not json at all", 24)]
    [InlineData("""{"hours":null}""", 24)]
    [InlineData("""{"hours":"abc"}""", 24)]
    public void IntArg_IsLenientAndClamped(string args, int expected)
        => Assert.Equal(expected, AiToolbox.IntArg(args, "hours", 24, 1, 100));

    [Fact]
    public void IntArg_FallbackItselfIsClamped()
        => Assert.Equal(10, AiToolbox.IntArg("{}", "n", 999, 1, 10));

    [Theory]
    [InlineData("""{"keyword":"電壓"}""", "電壓")]
    [InlineData("""{"keyword":"  風扇  "}""", "風扇")]   // 去頭尾空白
    [InlineData("""{"keyword":12}""", "12")]             // 數字也接受
    [InlineData("""{"keyword":true}""", "True")]
    public void StringArg_ReadsValue(string args, string expected)
        => Assert.Equal(expected, AiToolbox.StringArg(args, "keyword"));

    [Theory]
    [InlineData("""{"keyword":""}""")]
    [InlineData("""{"keyword":"   "}""")]
    [InlineData("""{"keyword":null}""")]
    [InlineData("""{"other":"x"}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("這不是 JSON")]
    public void StringArg_MissingOrBlank_ReturnsNull(string args)
        => Assert.Null(AiToolbox.StringArg(args, "keyword"));
}

/// <summary>對話保存檔：寫入、讀回、上限與刪除，全在每次測試自己的暫存夾內。</summary>
public class AiChatStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));

    private AiChatStore Store() => new(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoFile_ReturnsEmpty()
    {
        Assert.Empty(Store().Load());
        Assert.False(File.Exists(Store().FilePath));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRolesAndText()
    {
        Store().Save(
        [
            new AiMessage { IsUser = true, Text = "這台電腦如何？" },
            new AiMessage { IsTool = true, Text = "get_temperatures → 處理器 62 °C" },
            new AiMessage { Text = "整體屬於主流配置。" },
        ]);

        var back = Store().Load();
        Assert.Equal(3, back.Count);
        Assert.True(back[0].IsUser);
        Assert.Equal("這台電腦如何？", back[0].Text);
        Assert.True(back[1].IsTool);
        Assert.False(back[1].IsUser);
        Assert.True(back[2].IsAssistant);
        Assert.Equal("整體屬於主流配置。", back[2].Text);
    }

    [Fact]
    public void Save_SkipsBlankMessages()
    {
        Store().Save([new AiMessage { Text = "" }, new AiMessage { Text = "   " }, new AiMessage { Text = "有內容" }]);
        Assert.Single(Store().Load());
    }

    [Fact]
    public void Save_KeepsOnlyTheNewestRows()
    {
        var many = Enumerable.Range(1, AiChatStore.MaxRows + 40)
            .Select(i => new AiMessage { Text = "第 " + i + " 則" }).ToList();
        Store().Save(many);

        var back = Store().Load();
        Assert.Equal(AiChatStore.MaxRows, back.Count);
        Assert.Equal("第 41 則", back[0].Text);                                   // 最舊的被丟掉
        Assert.Equal($"第 {AiChatStore.MaxRows + 40} 則", back[^1].Text);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var s = Store();
        s.Save([new AiMessage { Text = "留下紀錄" }]);
        Assert.True(File.Exists(s.FilePath));

        s.Delete();
        Assert.False(File.Exists(s.FilePath));
        Assert.Empty(s.Load());
    }

    [Fact]
    public void Delete_WhenNoFile_DoesNotThrow() => Store().Delete();

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var s = Store();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(s.FilePath, "{ 這不是有效的 JSON");
        Assert.Empty(s.Load());
    }
}
