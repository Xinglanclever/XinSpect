using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事件 ID 的事實（第 280 輪，U10-EV-0001 ～ U10-EV-0020）。
///
/// <para>
/// 這一組的核心不是「事件查得對不對」（那要真機才有意義），而是<b>四種狀態不能被壓成一種</b>：
/// 查得到 0 筆、頻道不存在、查詢失敗、ID 本身還沒定案。把任何兩種混在一起，
/// 畫面就會把「我不知道」講成「沒發生過」——那是這個專案最貴的一類缺陷。
/// </para>
/// </summary>
public class EventIdEvidenceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static EventIdEvidenceFactsService.EvtProbe Fixed(EvtProbeResult result)
        => (_, _, _, _) => result;

    private static IReadOnlyList<HardwareFact> Collect(EventIdEvidenceFactsService.EvtProbe probe)
        => EventIdEvidenceFactsService.Collect(At, probe);

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── 四種「沒有」必須分開 ──────────────────────────────────────────────

    [Fact]
    public void 查到零筆是量測結果_帶數值零且為Present()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(0, false, null, null))),
            EventIdEvidenceFactsService.WheaInternalKey);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(0, fact.NumericValue);
        Assert.Null(fact.UnavailableReason);
        Assert.Contains("最近 30 天：0 筆", fact.Value);
    }

    [Fact]
    public void 頻道不存在標不支援並說出頻道名_不是零筆()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(0, false, null, "記錄檔不存在"))),
            EventIdEvidenceFactsService.DsmKey);

        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("Microsoft-Windows-DeviceSetupManager/Admin", fact.UnavailableReason!);
        Assert.Contains("不代表「沒發生過」", fact.UnavailableReason!);
    }

    [Fact]
    public void 查詢失敗標讀取錯誤並附原因()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(0, false, "存取被拒", null))),
            EventIdEvidenceFactsService.WheaPcieKey);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("存取被拒", fact.UnavailableReason!);
        Assert.Contains("讀不到不等於沒發生", fact.UnavailableReason!);
    }

    [Fact]
    public void 探針丟例外也標讀取錯誤而不是讓整批崩掉()
    {
        var facts = EventIdEvidenceFactsService.Collect(At,
            (_, _, _, _) => throw new InvalidOperationException("記錄檔損毀"));
        Assert.All(facts.Where(f => f.Availability == FactAvailability.ReadError), f =>
        {
            Assert.Contains("記錄檔損毀", f.UnavailableReason!);
            Assert.Null(f.NumericValue);
        });
        Assert.Equal(20, facts.Count);
    }

    [Fact]
    public void 未定案的ID不查也不命名_標不適用並寫明缺什麼文件()
    {
        int queried = 0;
        var facts = EventIdEvidenceFactsService.Collect(At, (_, _, ids, _) =>
        {
            queried++;
            Assert.NotEmpty(ids);
            return new EvtProbeResult(0, false, null, null);
        });

        var pending = facts.Single(f => f.Key == EventIdEvidenceFactsService.KStorageKey);
        Assert.Equal(FactAvailability.NotApplicable, pending.Availability);
        Assert.Contains("尚未定案", pending.UnavailableReason!);
        Assert.Contains("Kernel-Storage", pending.UnavailableReason!);
        Assert.DoesNotContain("512", pending.Value);

        // 待核與 ETW 那幾列不得發出查詢——它們沒有可查的 ID。
        Assert.Equal(EventIdEvidenceFactsService.Rows.Count(r =>
            r.State is EventIdEvidenceFactsService.RowState.Confirmed
                 or EventIdEvidenceFactsService.RowState.Partial
                 or EventIdEvidenceFactsService.RowState.ManifestOnly), queried);
    }

    [Fact]
    public void ETW追蹤供應者不在事件記錄裡_如實標不適用()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(0, false, null, null))),
            EventIdEvidenceFactsService.UsbHubKey);

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不在事件記錄裡", fact.UnavailableReason!);
        Assert.Contains("ETW", fact.UnavailableReason!);
    }

    // ── 讀取上限與時間窗 ──────────────────────────────────────────────────

    [Fact]
    public void 達到讀取上限時要說至少而不是總數()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(2000, true, null, null))),
            EventIdEvidenceFactsService.NtfsExtraKey);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("至少 2000 筆", fact.Value);
        Assert.Contains("不是總數", fact.Value);
    }

    [Fact]
    public void 查詢的時間窗是三十天且由呼叫端傳入起點()
    {
        DateTimeOffset seen = default;
        EventIdEvidenceFactsService.Collect(At, (_, _, _, since) =>
        {
            seen = since;
            return new EvtProbeResult(0, false, null, null);
        });
        Assert.Equal(At.AddDays(-EventIdEvidenceFactsService.WindowDays), seen);
        Assert.Equal(30, EventIdEvidenceFactsService.WindowDays);
    }

    // ── 值裡要帶語意與界線 ────────────────────────────────────────────────

    [Fact]
    public void 有值的事實要寫出事件ID供應者語意與不建因果的界線()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(3, false, null, null))),
            EventIdEvidenceFactsService.DisplayTdr2Key);

        Assert.Contains("4101／4109", fact.Value);
        Assert.Contains("Display", fact.Value);
        Assert.Contains("TDR", fact.Value);
        Assert.Contains("不判斷嚴重程度", fact.Value);
        Assert.Equal(3, fact.NumericValue);
    }

    [Fact]
    public void 部分定案的列要把還缺什麼寫進值()
    {
        var fact = One(Collect(Fixed(new EvtProbeResult(1, false, null, null))),
            EventIdEvidenceFactsService.WheaMiscKey);

        Assert.Contains("仍缺", fact.Value);
        Assert.Contains("47", fact.Value);
    }

    [Fact]
    public void 待核清單彙總要數得出還缺幾列並點名()
    {
        var facts = Collect(Fixed(new EvtProbeResult(0, false, null, null)));
        var pending = One(facts, EventIdEvidenceFactsService.PendingKey);

        int expected = EventIdEvidenceFactsService.Rows.Count(r =>
            r.State is EventIdEvidenceFactsService.RowState.Pending
                 or EventIdEvidenceFactsService.RowState.Partial);
        Assert.Equal(expected, pending.NumericValue);
        Assert.Contains("本專案的知識還缺多少", pending.Value);
        Assert.Contains(EventIdEvidenceFactsService.KStorageKey, pending.Value);
    }

    // ── 鍵與目錄 ──────────────────────────────────────────────────────────

    [Fact]
    public void 每一列的事實鍵都寫成常數且都在目錄裡()
    {
        var constants = typeof(EventIdEvidenceFactsService)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(EventIdEvidenceFactsService.Rows.Count + 1, constants.Count);
        foreach (string key in constants)
            Assert.True(FactKeyCatalog.Keys.Contains(key, StringComparer.Ordinal),
                $"{key} 不在事實鍵目錄裡——執行期對帳會紅燈");
    }

    [Fact]
    public void 二十列的鍵互不相同且每列的狀態欄位自洽()
    {
        var keys = EventIdEvidenceFactsService.Rows.Select(r => r.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        foreach (var row in EventIdEvidenceFactsService.Rows)
        {
            if (row.State is EventIdEvidenceFactsService.RowState.Pending
                or EventIdEvidenceFactsService.RowState.NotInEventLog)
            {
                Assert.True(row.Ids.Length == 0, $"{row.Key}：未定案的列不得帶事件 ID");
                Assert.False(string.IsNullOrWhiteSpace(row.Gap), $"{row.Key}：未定案的列必須寫明缺什麼");
            }
            else
            {
                Assert.True(row.Ids.Length > 0, $"{row.Key}：已定案的列必須有事件 ID");
                Assert.False(string.IsNullOrWhiteSpace(row.Channel), $"{row.Key}：已定案的列必須有頻道");
                Assert.False(string.IsNullOrWhiteSpace(row.Provider), $"{row.Key}：已定案的列必須有供應者");
            }
            Assert.True(row.Source.Length >= 10, $"{row.Key}：出處太短");
            Assert.True(row.Meaning.Length >= 10, $"{row.Key}：語意太短");
        }
    }

    [Fact]
    public void 語意不得與稽核勘誤相反()
    {
        // 這四條是稽核文件裡明確更正過的誤解——舊的錯誤說法不得再出現在語意欄。
        var byKey = EventIdEvidenceFactsService.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);

        // 每一條都要「明確否定」舊的錯誤說法，而不是靜靜換成新的——
        // 讀者曾經看到的是舊的那句，不否定它，他會以為兩句都對。
        Assert.Contains("不是", byKey[EventIdEvidenceFactsService.WheaInternalKey].Meaning);
        Assert.Contains("「內部錯誤」", byKey[EventIdEvidenceFactsService.WheaInternalKey].Meaning);

        Assert.Contains("不是", byKey[EventIdEvidenceFactsService.KppParkingKey].Meaning);
        Assert.Contains("核心停泊", byKey[EventIdEvidenceFactsService.KppParkingKey].Meaning);

        Assert.Contains("不是", byKey[EventIdEvidenceFactsService.ModernStandbyWakeKey].Meaning);
        Assert.Contains("507", byKey[EventIdEvidenceFactsService.ModernStandbyWakeKey].Meaning);

        Assert.Contains("17", byKey[EventIdEvidenceFactsService.WheaPcieKey].Meaning);
        Assert.Contains("19", byKey[EventIdEvidenceFactsService.WheaPcieKey].Meaning);
    }

    [Fact]
    public void 稽核文件的EV組列數與本服務的列數對得上()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string audit = File.ReadAllText(Path.Combine(dir!.FullName,
            "docs", "ULTIMATE-10000", "AUDIT-pending.md"));

        int audited = Regex.Matches(audit, @"^- U10-EV-\d+｜", RegexOptions.Multiline).Count;

        // 每一列都必須「要嘛引用稽核文件、要嘛明說自己沒有被稽核」。
        // 稽核文件本身少了兩列（EV-0002 沒被納入、EV-0020 是程序性彙總），
        // 那不是遺漏而是申報——所以守門驗的是「兩邊相加等於列數」，不是「兩邊相等」。
        int backed = EventIdEvidenceFactsService.Rows.Count(r =>
            r.Source.Contains("AUDIT-pending.md", StringComparison.Ordinal));
        int manifestOnly = EventIdEvidenceFactsService.Rows.Count(r =>
            r.State == EventIdEvidenceFactsService.RowState.ManifestOnly);

        Assert.True(backed + manifestOnly == EventIdEvidenceFactsService.Rows.Count,
            $"有 {EventIdEvidenceFactsService.Rows.Count - backed - manifestOnly} 列既沒引用稽核文件、"
            + "也沒標成「只取 manifest」——那代表它有一個沒人查過出處的語意");
        Assert.True(audited >= backed - 2,
            $"稽核文件只有 {audited} 列，但有 {backed} 列宣稱引用它");
    }
}
