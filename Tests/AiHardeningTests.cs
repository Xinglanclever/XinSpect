using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 方面的加固：請求長度預算、暫時性失敗重試、送出前遮蔽、金鑰的落地加密。
///
/// <para>
/// 這幾條的共同點是「安靜地錯」：預算爆掉只會看到一句「呼叫失敗」、429 只會看到紅色氣泡、
/// 序號原樣送出去沒有人知道、金鑰明文躺在設定檔裡。測試要把這些行為釘成<b>可預期的</b>。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class AiHardeningTests
{
    // ── 請求長度預算 ──────────────────────────────────────────────────────

    [Fact]
    public void 預算沒超過時什麼都不動也不說謊()
    {
        var r = AiRequestBudget.Apply("系統", ["一", "二", "三"], 10_000);
        Assert.Equal("系統", r.System);
        Assert.Equal(new[] { "一", "二", "三" }, r.History);
        Assert.Equal(0, r.DroppedMessages);
        Assert.Equal(0, r.ClippedMessages);
        Assert.Equal("", r.Notice);          // 沒有裁東西就不能冒出「已省略」的說明
    }

    [Fact]
    public void 超長時由最舊的整則丟棄而且最後一則一定留著()
    {
        // 每則 400 字、共 30 則 → 遠超過 8,000 的預算
        var history = Enumerable.Range(0, 30).Select(i => new string((char)('a' + i % 26), 400)).ToList();
        var r = AiRequestBudget.Apply("sys", history, AiRequestBudget.MinChars);

        Assert.True(r.DroppedMessages > 0, "超過預算卻一則都沒丟");
        Assert.Equal(history[^1], r.History[^1]);            // 最後一則（這次的提問）必須保留
        Assert.Equal(history.Skip(30 - r.History.Count), r.History);   // 保留的是連續尾部
        Assert.True(r.TotalChars <= AiRequestBudget.MinChars, $"總長 {r.TotalChars} 仍超過預算");
        Assert.Contains("已省略", r.Notice);
    }

    [Fact]
    public void 單則過長會被截斷並寫出原長度()
    {
        string huge = new('x', 50_000);
        var r = AiRequestBudget.Apply("sys", [huge], 10_000);

        Assert.Equal(1, r.ClippedMessages);
        Assert.True(r.History[0].Length < huge.Length);
        Assert.Contains("已截斷", r.History[0]);
        Assert.Contains("50,000", r.History[0]);            // 原長度要寫出來，讀的人才知道少了多少
    }

    [Fact]
    public void 系統提示最多佔一半預算()
    {
        string sys = new('s', 40_000);
        var r = AiRequestBudget.Apply(sys, ["問題"], 10_000);
        Assert.True(r.System.Length <= 10_000 / 2 + 200, $"系統提示 {r.System.Length} 字，超過一半預算");
        Assert.Equal("問題", r.History[0]);                 // 對話不能被系統提示擠掉
    }

    [Fact]
    public void 預算上下限會被夾住()
    {
        // 太小的預算會連硬體快照都放不進去，太大則不是預算問題而是端點問題
        var tiny = AiRequestBudget.Apply("s", ["x"], 1);
        Assert.True(tiny.TotalChars > 0);
        var huge = AiRequestBudget.Apply("s", [new string('y', 50_000)], int.MaxValue);
        Assert.True(huge.TotalChars <= 50_001);
    }

    // ── 重試政策 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(502, true)]
    [InlineData(504, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void 只有暫時性狀態碼才重試(int status, bool expected)
    {
        Assert.Equal(expected, AiRetryPolicy.IsTransient(status));
        var d = AiRetryPolicy.Decide(status, attempt: 1);
        Assert.Equal(expected, d.Retry);
        // 不重試時一定要說得出為什麼——錯誤訊息裡不能只有一個狀態碼
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }

    [Fact]
    public void 重試次數有上限()
    {
        var last = AiRetryPolicy.Decide(429, attempt: AiRetryPolicy.MaxAttempts);
        Assert.False(last.Retry);
        Assert.Contains("已連續失敗", last.Reason);
    }

    [Fact]
    public void 端點要求的等待太久就不要等()
    {
        var d = AiRetryPolicy.Decide(429, 1, TimeSpan.FromSeconds(AiRetryPolicy.MaxDelaySeconds + 1));
        Assert.False(d.Retry);
        Assert.Contains("超過上限", d.Reason);

        var ok = AiRetryPolicy.Decide(429, 1, TimeSpan.FromSeconds(3));
        Assert.True(ok.Retry);
        Assert.Equal(TimeSpan.FromSeconds(3), ok.Delay);   // 端點說的時間優先於自己的退避
    }

    [Fact]
    public void 沒有RetryAfter時採遞增退避且不超過上限()
    {
        var d1 = AiRetryPolicy.Decide(503, 1);
        var d2 = AiRetryPolicy.Decide(503, 2);
        Assert.True(d2.Delay > d1.Delay);
        Assert.True(d2.Delay <= TimeSpan.FromSeconds(AiRetryPolicy.MaxDelaySeconds));
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("0", 0)]
    [InlineData("not-a-number", -1)]
    [InlineData("", -1)]
    public void 解析RetryAfter秒數格式(string raw, int expectedSeconds)
    {
        var d = AiRetryPolicy.ParseRetryAfter(raw);
        Assert.Equal(expectedSeconds < 0 ? null : TimeSpan.FromSeconds(expectedSeconds), d);
    }

    [Fact]
    public void 解析RetryAfter日期格式()
    {
        string when = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        var d = AiRetryPolicy.ParseRetryAfter(when);
        Assert.NotNull(d);
        Assert.InRange(d!.Value.TotalSeconds, 20, 31);
    }

    // ── 送出前遮蔽 ────────────────────────────────────────────────────────

    [Fact]
    public void 會遮掉能指到機器或人的字串()
    {
        string input = """
            主機 UUID：3f2504e0-4f89-11d3-9a0c-0305e82c3301
            網卡 MAC：00-1A-2B-3C-4D-5E
            IP 位址：192.168.1.23
            使用者：C:\Users\yg\AppData\Local
            序號：SN-ABC12345
            主機名稱：YG-DESKTOP
            """;

        var r = AiRedactor.Apply(input);

        Assert.DoesNotContain("3f2504e0", r.Text);
        Assert.DoesNotContain("00-1A-2B-3C-4D-5E", r.Text);
        Assert.DoesNotContain("192.168.1.23", r.Text);
        Assert.DoesNotContain(@"\yg\", r.Text);
        Assert.DoesNotContain("SN-ABC12345", r.Text);
        Assert.DoesNotContain("YG-DESKTOP", r.Text);

        Assert.Equal(6, r.Count);
        foreach (string kind in new[] { "UUID", "MAC", "IP 位址", "使用者路徑", "序號", "主機名稱" })
            Assert.Contains(kind, r.Kinds);
    }

    [Fact]
    public void 判斷需要的讀值一律不動()
    {
        // 型號、容量、時脈、溫度、電壓、版本號都是回答問題要用的資料，遮掉反而少一個線索。
        string input = "型號：i7-12700K ・ 容量 32 GB ・ 時脈 4.9 GHz ・ 溫度 65 °C ・ 電壓 1.28 V ・ 驅動 1.0.0.0";
        var r = AiRedactor.Apply(input);
        Assert.Equal(0, r.Count);
        Assert.Equal(input, r.Text);
        Assert.Equal("沒有偵測到可識別資訊", r.Summary);
    }

    [Fact]
    public void 沒有標籤的IPv4不遮()
    {
        // 1.0.0.0 / 1.0.0.1 這種字串在硬體資料裡多半是版本號或識別碼，不是位址。
        var r = AiRedactor.Apply("BIOS 版本 1.0.0.1，微碼 1.0.0.0");
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void 遮蔽摘要只列出現過的類別並去重()
    {
        var r = AiRedactor.ApplyAll([
            "IP 位址：10.0.0.5",
            "IP Address: 10.0.0.6",
        ]);
        Assert.Equal(2, r.Count);
        Assert.Single(r.Kinds);                 // 同一類只列一次
        Assert.Contains("2 處", AiRedactor.Apply("IP 位址：10.0.0.5\nIP 位址：10.0.0.6").Summary);
    }

    [Fact]
    public void 空字串與null不會爆()
    {
        Assert.Equal(0, AiRedactor.Apply(null).Count);
        Assert.Equal(0, AiRedactor.Apply("").Count);
        Assert.Equal("", AiRedactor.Apply(null).Text);
    }

    // ── 金鑰的落地加密 ────────────────────────────────────────────────────

    [Fact]
    public void 金鑰加密後可以還原且密文不是明文()
    {
        Assert.True(SecretProtector.IsSupported, "本專案只在 Windows 上跑；DPAPI 應該可用");

        string cipher = SecretProtector.Protect("sk-test-1234567890");
        Assert.StartsWith(SecretProtector.Prefix, cipher);
        Assert.DoesNotContain("sk-test-1234567890", cipher);

        Assert.True(SecretProtector.TryUnprotect(cipher, out string plain, out string? error, out bool wasPlain));
        Assert.Null(error);
        Assert.False(wasPlain);
        Assert.Equal("sk-test-1234567890", plain);
    }

    [Fact]
    public void 同一把金鑰兩次加密的密文不同()
    {
        // 不是「包一層」而已：DPAPI 每次都會用新的 salt，所以至少看得出它不是原樣複製。
        Assert.NotEqual(SecretProtector.Protect("same-key"), SecretProtector.Protect("same-key"));
    }

    [Fact]
    public void 密文損毀時回空值與原因而不是拿密文當金鑰()
    {
        string broken = SecretProtector.Prefix + "!!!這不是-base64!!!";
        Assert.False(SecretProtector.TryUnprotect(broken, out string plain, out string? error, out _));
        Assert.Equal("", plain);                       // 絕不把密文本身當成金鑰送出
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 舊版明文金鑰原樣讀回並標記為明文()
    {
        Assert.True(SecretProtector.TryUnprotect("sk-legacy-plain", out string plain, out _, out bool wasPlain));
        Assert.True(wasPlain);
        Assert.Equal("sk-legacy-plain", plain);
    }

    [Fact]
    public void 空金鑰不算密文也不算錯誤()
    {
        Assert.True(SecretProtector.TryUnprotect("", out string plain, out string? error, out _));
        Assert.Equal("", plain);
        Assert.Null(error);
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.False(SecretProtector.LooksProtected(""));
    }

    [Fact]
    public void 存放方式要說出實話()
    {
        if (SecretProtector.IsSupported)
        {
            Assert.Contains("DPAPI", SecretProtector.StorageText(SecretProtector.Protect("k")));
            Assert.Contains("明文", SecretProtector.StorageText("plain-key"));
        }
        Assert.Contains("未設定", SecretProtector.StorageText(""));
    }

    // ── 三段預算的順序要合理 ──────────────────────────────────────────────

    [Fact]
    public void 三段逾時的順序要合理()
    {
        Assert.True(AiService.HeaderTimeout < AiService.StreamIdleTimeout);
        Assert.True(AiService.StreamIdleTimeout < AiService.WholeTimeout);
        Assert.True(AiService.HeaderTimeout > TimeSpan.Zero);
    }

    // ── 真的接上服務：預覽用的就是實際送出的那一份 ────────────────────────

    [Fact]
    public void 傳送內容預覽會套用遮蔽與長度預算且不含任何憑證()
    {
        var settings = new SettingsService();
        bool origRedact = settings.AiRedactIdentity;
        int origBudget = settings.AiRequestBudgetChars;
        int origTurns = settings.AiHistoryTurns;
        string folder = Path.Combine(Path.GetTempPath(), "XinSpectAiPreview_" + Guid.NewGuid().ToString("N"));
        try
        {
            settings.AiRedactIdentity = true;
            settings.AiRequestBudgetChars = AiRequestBudget.MinChars;
            settings.AiHistoryTurns = 0;        // 則數不裁，讓長度預算單獨負責

            var ai = new AiService(settings, folder)
            {
                SnapshotProvider = () => "序號：SN-LEAK-ME-999\nIP 位址：192.168.1.23\n溫度 65 °C",
            };
            // 前面塞幾則長訊息把預算撐爆（這些會被整則丟棄）；
            // 帶序號的那一則放在後面，確保它真的留下來、被測到的是「遮蔽」而不是「剛好被丟掉」。
            for (int i = 0; i < 6; i++)
                ai.Messages.Add(new AiMessage { IsUser = true, Text = "舊訊息 " + i + "：" + new string('y', 3_000) });
            ai.Messages.Add(new AiMessage { IsUser = true, Text = "我這台的序號是 SN-USER-SECRET-777，這樣正常嗎" });
            ai.Messages.Add(new AiMessage { IsUser = false, Text = "先看溫度。" });
            // 最後一則一律視為「占位中的回覆」而不回送。
            ai.Messages.Add(new AiMessage { IsUser = true, Text = "（占位）" });

            string preview = ai.BuildRequestPreview();

            Assert.Contains("遮蔽：已開啟", preview);
            Assert.DoesNotContain("SN-LEAK-ME-999", preview);         // 快照裡的序號
            Assert.DoesNotContain("SN-USER-SECRET-777", preview);     // 對話裡的序號
            Assert.DoesNotContain("192.168.1.23", preview);
            Assert.Contains("65 °C", preview);                        // 判斷要用的讀值不能被遮
            Assert.Contains("已省略", preview);                        // 爆掉時一定要說出來
            Assert.DoesNotContain("Bearer", preview);                 // 金鑰不會出現在內容裡
            Assert.DoesNotContain(SecretProtector.Prefix, preview);   // 連密文都不該出現
            Assert.True(ai.LastRedactionCount >= 2, "序號與 IP 各一處，至少要遮到兩處");
        }
        finally
        {
            settings.AiRedactIdentity = origRedact;
            settings.AiRequestBudgetChars = origBudget;
            settings.AiHistoryTurns = origTurns;
            try { Directory.Delete(folder, true); } catch { /* 清不掉不影響結論 */ }
        }
    }
}
