using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-051（資料主權報告）、P1-052（供檔零出網守門）、
/// P1-054（報告分級由內容推導）的守門。
///
/// <para>
/// P1-052 與既有的 <c>DataSovereigntyTests.事實蒐集與解碼器_零網路API</c> 的差別：
/// 既有那條掃的是<b>事實蒐集與解碼層</b>（Services／XinSpect.Decoders，排除五個 opt-in 檔）；
/// 這一條額外釘住<b>供檔路徑</b>——使用者提供的檔案在被解析的那條路上，
/// 不得有任何連出去的可能。兩者範圍不同，不是重複。
/// </para>
/// </summary>
public class DataSovereigntyReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, bool sensitive = false) =>
        new(key, "測試", key, "值", "", "測試來源", FactTrustLevel.Reported, sensitive, At,
            null, FactAvailability.Present, null);

    // ── P1-051 資料主權報告 ────────────────────────────────────────────

    [Fact]
    public void 主權_全部留在本機時要說那是實際結果不是承諾()
    {
        var fact = DataSovereigntyReport.ToEgressReportFact(At, 100, 100, 0, []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("全部留在本機", fact.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("不是承諾", fact.Value);
    }

    [Fact]
    public void 主權_分母對不上時不報假的總數()
    {
        var fact = DataSovereigntyReport.ToEgressReportFact(At, 100, 90, 5, []);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("不能報一個對不上的總數", fact.UnavailableReason);
    }

    [Fact]
    public void 主權_有資料離開時要說出筆數與去向()
    {
        var fact = DataSovereigntyReport.ToEgressReportFact(At, 100, 98, 2, ["例外的更新檢查"]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：2 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(fact.NumericValue);
        Assert.Contains("原本帶了數字 2", fact.UnavailableReason);
        Assert.Contains("例外的更新檢查", fact.Value);
    }

    [Fact]
    public void 主權_沒有事實時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            DataSovereigntyReport.ToEgressReportFact(At, 0, 0, 0, []).Availability);
    }

    // ── P1-054 報告分級由內容推導 ──────────────────────────────────────

    [Fact]
    public void 分級_由內容推導且與宣告一致時標Present()
    {
        var fact = DataSovereigntyReport.ToReportClassFact(At,
            [Fact("cpu.cores"), Fact("system.name")], SensitivityPolicy.Level.L1);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1.0, fact.NumericValue);
    }

    [Fact]
    public void 分級_取最高敏感度而不是平均()
    {
        // 一份報告只要有一筆 L2，整份就是 L2——報告是以整體被傳遞的，
        // 讀者不會只讀 L0 那幾頁。
        var fact = DataSovereigntyReport.ToReportClassFact(At,
            [Fact("cpu.cores"), Fact("system.name"), Fact("tpm.ek", sensitive: true)],
            SensitivityPolicy.Level.L2);

        Assert.Equal(2.0, fact.NumericValue);
        Assert.Contains("L2", fact.Value);
    }

    [Fact]
    public void 分級_宣告低於內容時要標讀取失敗()
    {
        var fact = DataSovereigntyReport.ToReportClassFact(At,
            [Fact("tpm.ek", sensitive: true)], SensitivityPolicy.Level.L0);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("宣告的等級低於", fact.UnavailableReason);
    }

    [Fact]
    public void 分級_過度宣告也要指出來()
    {
        var fact = DataSovereigntyReport.ToReportClassFact(At,
            [Fact("cpu.cores")], SensitivityPolicy.Level.L3);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("過度宣告", fact.UnavailableReason);
    }

    [Fact]
    public void 分級_沒有事實時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            DataSovereigntyReport.ToReportClassFact(At, [], SensitivityPolicy.Level.L0).Availability);
    }

    // ── P1-052 供檔零出網守門 ──────────────────────────────────────────

    /// <summary>供檔路徑上的服務：使用者提供的檔案會經過這幾支。</summary>
    private static readonly string[] FileInputServices =
    [
        "OfflineInputMount.cs",
        "KnowledgePackService.cs",
        "HardwareSnapshotService.cs",
    ];

    /// <summary>出網 API。<b>每一條都要說得出為什麼它算「連出去」</b>。</summary>
    private static readonly (string Token, string Why)[] NetworkTokens =
    [
        ("HttpClient", "HTTP 連線"),
        ("WebRequest.Create", "HTTP 連線"),
        ("WebClient", "HTTP 連線"),
        ("HttpWebRequest", "HTTP 連線"),
        ("FtpWebRequest", "FTP 連線"),
        ("TcpClient", "TCP 連線"),
        ("UdpClient", "UDP 連線"),
        ("Socket(", "原始 socket 連線"),
        ("Dns.", "DNS 查詢（也是一種連出去）"),
        ("SmtpClient", "郵件寄送"),
        ("Ping.", "ICMP 探測"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 供檔零出網_供檔路徑不得出現出網API()
    {
        string root = RepoRoot();
        var violations = new List<string>();
        int scanned = 0;

        foreach (string name in FileInputServices)
        {
            string path = Path.Combine(root, "Services", name);
            if (!File.Exists(path)) continue;
            scanned++;
            string text = File.ReadAllText(path);
            foreach (var (token, why) in NetworkTokens.Where(t => text.Contains(t.Token)))
                violations.Add($"{name}：{why}（{token}）");
        }

        Assert.True(scanned > 0, "一個供檔服務都找不到——檔名改了？這條守門就失效了");
        Assert.True(violations.Count == 0,
            "供檔路徑出現出網 API——使用者提供的檔案只在本機解析：\n"
            + string.Join("\n", violations.Select(v => "  • " + v)));
    }

    [Fact]
    public void 供檔零出網_正對照_含出網呼叫的假來源必須被抓到()
    {
        const string fake = """
            public static class 假供檔來源
            {
                public static string Fetch() => new HttpClient().GetStringAsync("https://example.com").Result;
            }
            """;

        var hits = NetworkTokens.Where(t => fake.Contains(t.Token)).ToList();
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Why.Contains("HTTP"));
    }

    [Fact]
    public void 供檔零出網_乾淨的來源不得誤報()
    {
        const string clean = """
            public static class 乾淨來源
            {
                public static string Read(string path) => File.ReadAllText(path);
            }
            """;
        Assert.Empty(NetworkTokens.Where(t => clean.Contains(t.Token)));
    }

    [Fact]
    public void 出網API清單每一條都要有理由()
    {
        foreach (var (token, why) in NetworkTokens)
        {
            Assert.False(string.IsNullOrWhiteSpace(why), $"樣式 {token} 沒有寫出為什麼它算連出去");
            Assert.True(why.Length >= 4, $"樣式 {token} 的理由太短：{why}");
        }
    }
}
