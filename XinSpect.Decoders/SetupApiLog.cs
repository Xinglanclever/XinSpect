using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// setupapi.dev.log 的一個安裝區段：起訖時間、結束狀態、裝置實例 ID、錯誤行數。
/// 解析不出來的欄位為 null——「沒解析出」與「沒有」是兩件事。
/// </summary>
public sealed record SetupApiSection(
    string? StartTime, string? EndTime, string? Status,
    string? DeviceId, string HeaderText, int ErrorLines, string? BootSession);

/// <summary>
/// Windows 裝置安裝記錄（setupapi.dev.log）的純解析器：peripheral-forensic 那條能力的唯讀版。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼只取這些欄位：</b>記錄的文字（「Section start」等）隨系統語言在地化——字串比對會在
/// 別的語言上靜默失效。語言中立的有三樣：區段標記符號（<c>&gt;&gt;&gt;</c>／<c>&lt;&lt;&lt;</c>）、
/// 裝置實例 ID 的語彙（PCI\、USB\、ACPI\、SWD\…）、時間戳格式（yyyy/MM/dd HH:mm:ss.fff）。
/// 本解析器只認這三樣：<b>不解析在地化欄位</b>，標頭原文照抄供人判讀。
/// </para>
/// </remarks>
public static class SetupApiLog
{
    /// <summary>時間戳：yyyy/MM/dd 或 yyyy-MM-dd + HH:mm:ss（含可選毫秒）。</summary>
    private static readonly Regex Timestamp = new(
        @"\d{4}[/\-]\d{2}[/\-]\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?", RegexOptions.Compiled);

    /// <summary>裝置實例 ID 的語彙：匯流排前綴 + 反斜線 + 非空白（語言中立）。</summary>
    private static readonly Regex DeviceId = new(
        @"\b(PCI|USB|ACPI|SCSI|SWD|HID|ROOT|STORAGE|DISPLAY|MONITOR|IDE|NDIS|UEFI|SERENUM|INTELAUDIO|COMPOSITE)\\" + @"[^\]\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [SpecRef("setupapi.dev.log 格式（Windows PnP 裝置安裝記錄）：'>>>'/'<<<' 區段標記、[方括號] 標頭含裝置實例 ID（匯流排＋裝置＋實例）、"
           + "'!!!' 錯誤行、[Boot Session: 時間戳] 開機段；標籤文字隨系統語言在地化，本解析器只取標記符號／裝置 ID 語彙／時間戳，不解析在地化欄位")]
    public static IReadOnlyList<SetupApiSection> Parse(string content)
    {
        var sections = new List<SetupApiSection>();
        string? boot = null;
        int index = -1; // 進行中區段在 sections 的索引；record 不可變——更新一律走清單替換，
                        // 「current = current with …」會把更新留在副本上、清單裡仍是舊值（測試抓到的真 bug）

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("[Boot Session:", StringComparison.Ordinal))
            {
                boot = Timestamp.Match(line) is { Success: true } m ? m.Value : boot;
                continue;
            }
            if (line.StartsWith(">>>  [", StringComparison.Ordinal))
            {
                sections.Add(new SetupApiSection(null, null, null, DeviceId.Match(line).Value, line.Trim(), 0, boot));
                index = sections.Count - 1;
                continue;
            }
            if (index < 0) continue;

            if (line.StartsWith("!!!", StringComparison.Ordinal))
            {
                sections[index] = sections[index] with { ErrorLines = sections[index].ErrorLines + 1 };
                continue;
            }
            if (line.StartsWith("<<<", StringComparison.Ordinal))
            {
                if (Timestamp.Match(line) is { Success: true } m)
                    sections[index] = sections[index] with { EndTime = m.Value };
                else if (line.Contains(':'))
                {
                    // 狀態行（<<<  [Exit status: SUCCESS] 形狀）：取最後一個冒號之後、右括號之前的值
                    int idx = line.LastIndexOf(':');
                    int end = line.LastIndexOf(']');
                    if (end > idx)
                    {
                        string status = line[(idx + 1)..end].Trim();
                        if (status.Length > 0)
                            sections[index] = sections[index] with { Status = status };
                    }
                }
                continue;
            }
            if (sections[index].StartTime is null
                && line.StartsWith(">>>", StringComparison.Ordinal)
                && Timestamp.Match(line) is { Success: true } ms)
            {
                sections[index] = sections[index] with { StartTime = ms.Value };
            }
        }
        return sections;
    }
}
