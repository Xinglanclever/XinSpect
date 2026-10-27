using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「一段文字太長，整份時間膠囊就存不起來」這個缺陷的守門（2026-10-26 使用者回報）。
/// </summary>
/// <remarks>
/// <para>
/// 當時的畫面是「儲存失敗／<c>UnavailableReason 超過 512 字元。(Parameter 'UnavailableReason')</c>」——
/// 使用者做的是「建立時間膠囊」，得到的訊息卻是一個他從沒聽過的欄位名，而且
/// <b>不知道是哪一筆事實</b>。根因是 SMART 探針把「每一顆讀不到的碟」逐顆串成一句，
/// 碟多的機器就超過快照 schema 的 512 字元上限。
/// </para>
/// <para>
/// 兩層修法各有一條守門：①生產端逐顆清單有上限（<see cref="SmartLookupProbe.JoinFailureLines"/>）；
/// ②快照邊界把超長文字截短並<b>留下原長</b>，任何新的生產端都不會再用同一種方式弄壞存檔。
/// </para>
/// </remarks>
public class SnapshotReasonClampTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static HardwareSnapshotFact Fact(string key, string? reason, string value = "—",
        FactAvailability availability = FactAvailability.ReadError)
        => new()
        {
            Key = key,
            Category = "測試",
            Name = "測試事實",
            Value = value,
            Source = "測試來源",
            Trust = FactTrustLevel.Unknown,
            MeasuredAtUtc = At,
            Availability = availability,
            UnavailableReason = reason,
        };

    private static HardwareSnapshot Make(params HardwareSnapshotFact[] facts)
        => HardwareSnapshotService.Create(facts, ["test-machine"], At, "0.0.0-test");

    // ── 生產端：逐顆碟的失敗清單 ────────────────────────────────────────

    [Fact]
    public void 逐顆失敗清單超過上限時要說出還有幾顆沒有逐顆列出()
    {
        var lines = Enumerable.Range(0, 12)
            .Select(i => $"PhysicalDrive {i}：驅動拒絕或未回應 ATA SMART READ DATA（讀不到，不代填）")
            .ToList();

        string text = SmartLookupProbe.JoinFailureLines(lines, SmartLookupProbe.MaxFailureLines);

        Assert.Contains("另有 7 顆同樣讀不到", text);
        Assert.DoesNotContain("PhysicalDrive 11", text);          // 第 12 顆沒有逐顆列出
        Assert.True(text.Length < 512, $"十二顆碟的說明合起來 {text.Length} 字，仍會撞上快照上限");
    }

    [Fact]
    public void 未超過上限的失敗清單一個字都不動()
    {
        string[] lines = ["PhysicalDrive 0：逾時", "PhysicalDrive 1：驅動拒絕"];
        Assert.Equal(string.Join("；", lines),
            SmartLookupProbe.JoinFailureLines(lines, SmartLookupProbe.MaxFailureLines));
    }

    // ── 邊界：超長文字截短但留下痕跡 ────────────────────────────────────

    [Fact]
    public void 超長的讀不到原因會被截短且留下原長()
    {
        string reason = new('長', 900);
        var snapshot = Make(Fact("test.reason.long", reason));

        string stored = snapshot.Facts.Single().UnavailableReason!;
        Assert.True(stored.Length <= 512, $"截短後仍有 {stored.Length} 字");
        Assert.EndsWith("已截斷）", stored);
        Assert.Contains("900", stored);                            // 原長看得見
        Assert.StartsWith(new string('長', 32), stored);           // 保留開頭（原因通常寫在前面）
    }

    [Fact]
    public void 超長的說明仍可存檔並讀回_不會讓時間膠囊失敗()
    {
        var snapshot = Make(Fact("test.reason.long", new string('長', 2_000)) );
        string path = Path.Combine(Path.GetTempPath(), "XinSpect_clamp_" + Guid.NewGuid().ToString("N") + ".xinsnapshot");
        try
        {
            HardwareSnapshotService.Save(path, snapshot);
            var loaded = HardwareSnapshotService.Load(path);
            Assert.Single(loaded.Facts);
            Assert.True(loaded.Facts[0].UnavailableReason!.Length <= 512);
            Assert.Equal(snapshot.Integrity.Hash, loaded.Integrity.Hash);   // 存回來的內容與建立時一致
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 未超過上限的原因保持原樣()
    {
        string reason = new('短', 300);
        Assert.Equal(reason, Make(Fact("test.reason.short", reason)).Facts.Single().UnavailableReason);
    }

    [Fact]
    public void 超長的值也會被截短而不是讓存檔失敗()
    {
        var snapshot = Make(Fact("test.value.long", null, new string('值', 20_000), FactAvailability.Present));
        string stored = snapshot.Facts.Single().Value;
        Assert.True(stored.Length <= 16_384);
        Assert.EndsWith("已截斷）", stored);
    }

    // ── 訊息：要指名是哪一把鍵 ────────────────────────────────────────

    [Fact]
    public void 驗證失敗的訊息要指名是哪一把鍵()
    {
        // 空白的原因（不是太長）仍然不合法——那是「沒說為什麼」，不是「原因很短」。
        var fact = Fact("test.reason.blank", "   ");
        var ex = Assert.Throws<ArgumentException>(() => Make(fact));
        Assert.Contains("test.reason.blank", ex.Message);
    }
}
