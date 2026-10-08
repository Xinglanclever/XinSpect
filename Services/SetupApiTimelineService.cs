using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>
/// setupapi.dev.log 的外設連接時間線（peripheral-forensic 那條能力）：唯讀解析 Windows 的
/// 裝置安裝記錄，回答「這台機器曾經裝過哪些裝置、何時、結果如何」。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是它：</b>記錄由系統維護、離線、純文字、唯讀；解析器只取語言中立的欄位
/// （標記符號、裝置 ID 語彙、時間戳——見 <see cref="SetupApiLog"/> 的說明），
/// <b>不解析在地化欄位</b>。USB 隨身碟插過哪個埠、哪天裝了什麼驅動、成敗如何——都在這份記錄裡。
/// </para>
/// <para>
/// <b>誠實界線：</b>裝置安裝記錄是「安裝歷史」不是「線上狀態」——曾經裝過不代表現在還在。
/// 解析不出的欄位（時間、狀態）如實為 null；記錄會被系統輪替清舊，這裡看到的是現存檔案的內容。
/// </para>
/// </remarks>
public static class SetupApiTimelineService
{
    private const string Category = "系統與軟體";
    public const string CountKey = "setuptl.sections.count";
    private const string Source = "setupapi.dev.log（唯讀）÷ 區段標記解析";
    private const string LogPath = "inf\\setupapi.dev.log";

    /// <summary>摘進事實文字的最近區段數上限。</summary>
    private const int MaxRecentInText = 10;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<string>? readLog = null, string? basePath = null)
    {
        string path = basePath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), LogPath)
            : Path.Combine(basePath, LogPath);
        string content;
        if (readLog is null)
        {
            if (!File.Exists(path))
                return [Unavailable(at, FactAvailability.NotApplicable, $"記錄檔不存在（{path}）——這台機器沒有裝置安裝記錄可解析")];
            try { content = File.ReadAllText(path); }
            catch (UnauthorizedAccessException)
            {
                return [Unavailable(at, FactAvailability.InsufficientPrivilege, "記錄檔無權讀取——需要管理員權限；讀不到不是 0 也不是「沒有內容」")];
            }
            catch (Exception e)
            {
                return [Unavailable(at, FactAvailability.ReadError, $"記錄檔讀取失敗：{e.Message}")];
            }
        }
        else
        {
            content = readLog();
            if (content is null)
                return [Unavailable(at, FactAvailability.ReadError, "記錄檔讀取失敗（來源回 null）")];
        }

        return CollectFromContent(content, at);
    }

    /// <summary>從已讀回的記錄內容產出事實；讀取通路與解析分離，測試不必碰真檔案。</summary>
    internal static IReadOnlyList<HardwareFact> CollectFromContent(string content, DateTimeOffset at)
    {
        var sections = SetupApiLog.Parse(content);
        if (sections.Count == 0)
            return [Unavailable(at, FactAvailability.ReadError,
                "記錄讀到了，但解析不出任何區段標記——不是預期的 setupapi.dev.log 形狀，如實回報不猜")];

        int errorLines = sections.Sum(s => s.ErrorLines);
        long sizeBytes = content.Length;

        var text = new System.Text.StringBuilder();
        text.Append($"解析出 {sections.Count} 個安裝區段（記錄約 {sizeBytes / 1024} KiB、{errorLines} 個錯誤標記行）。")
            .Append("解析只取語言中立的欄位（標記符號、裝置 ID、時間戳），不解析在地化文字；")
            .Append("這是安裝歷史不是線上狀態——曾經裝過不代表現在還在");

        var recent = new System.Text.StringBuilder();
        recent.Append($"最近 {Math.Min(MaxRecentInText, sections.Count)} 個區段（由舊到新）：");
        recent.Append(string.Join("；", sections.TakeLast(MaxRecentInText).Select(s =>
        {
            string who = s.DeviceId ?? s.HeaderText;
            string when = s.StartTime ?? "（時間未解析出）";
            string how = s.Status ?? "（狀態未解析出）";
            return $"{when} {who} → {how}";
        })));

        return
        [
            new HardwareFact(CountKey, Category, "裝置安裝記錄（setupapi.dev.log）",
                text.ToString(), "", Source, FactTrustLevel.Measured, false, at, sections.Count),
            new HardwareFact("setuptl.recent", Category, "裝置安裝最近區段",
                recent.ToString(), "", Source, FactTrustLevel.Measured, false, at),
        ];
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(CountKey, Category, "裝置安裝記錄（setupapi.dev.log）", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
