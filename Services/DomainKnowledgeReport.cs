using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 三個域知識包的查詢申報（ULTIMATE-1000 階段一 P1-041 微碼 CVE、P1-042 dbx 代數、P1-043 PSU 型號）。
///
/// <para>
/// <b>三者的共同形狀：</b>拿一個本機讀到的值（微碼修訂版、dbx 的 SVN、電源型號）
/// 去查一份離線知識表，回答「這個版本有沒有已知的對應」。
/// 它們共用同一個陷阱——<b>查不到時的沉默</b>。
/// </para>
///
/// <para>
/// <b>為什麼「查不到」在這三項特別危險：</b>
/// 使用者看到「微碼版本 0xf4」旁邊一片空白，很自然會讀成「沒問題」。
/// 但真相可能是「表裡沒有這一版」——那是<b>未知</b>，不是<b>安全</b>。
/// 這三把鍵的工作就是把那個空白填成一句誠實的話。
/// </para>
/// </summary>
public static class DomainKnowledgeReport
{
    public const string MicrocodeCveKey = "mc.cve";
    public const string DbxKey = "secure.dbx";
    public const string PsuKey = "psu.model";

    private const string Category = "知識與更新";
    private const string Source = "內建知識表（離線）";

    /// <summary>
    /// 一次域知識查詢的結果。<see cref="NotCovered"/> 是這一組的核心狀態：
    /// <b>「表裡沒有這一版」既不是命中也不是失敗，它是一個必須被說出來的未知。</b>
    /// </summary>
    public enum Coverage
    {
        /// <summary>表裡有這一版，並給得出對應資訊。</summary>
        Covered,
        /// <summary>表裡沒有這一版——未知，不是安全。</summary>
        NotCovered,
        /// <summary>本機讀不到要查的值（連查都沒得查）。</summary>
        NoLocalValue,
    }

    /// <summary>
    /// 產生 <c>mc.cve</c>：微碼版本對已知 CVE 的對照申報。
    /// </summary>
    public static HardwareFact ToMicrocodeCveFact(DateTimeOffset at, string? microcodeRevision,
        Coverage coverage, IReadOnlyList<string> cves)
    {
        if (microcodeRevision is null)
            return Unknown( MicrocodeCveKey, "微碼 CVE 對照", at, "微碼修訂版");

        if (coverage == Coverage.NotCovered)
            // 這是最重要的一條分支：沒有對照資料**不等於**沒有已知問題。
            return new HardwareFact(MicrocodeCveKey, Category, "微碼 CVE 對照",
                $"微碼修訂版 {microcodeRevision} 不在知識包內——沒有可對照的 CVE 清單", "筆",
                Source, FactTrustLevel.Reported, false, at, 0, FactAvailability.NotSupported,
                "知識包沒有收錄這個修訂版；「查不到」不等於「沒有已知問題」");

        if (cves.Count == 0)
            return new HardwareFact(MicrocodeCveKey, Category, "微碼 CVE 對照",
                $"微碼修訂版 {microcodeRevision}：知識包內沒有對應的 CVE", "筆",
                Source, FactTrustLevel.Reported, false, at, 0, FactAvailability.Present,
                "這是知識包當下版本的結論，不是「這個修訂版永遠安全」");

        return new HardwareFact(MicrocodeCveKey, Category, "微碼 CVE 對照",
            $"微碼修訂版 {microcodeRevision}：{cves.Count} 筆對應 CVE（" + string.Join("、", cves.Take(8)) + "）",
            "筆", Source, FactTrustLevel.Reported, false, at, cves.Count,
            FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>secure.dbx</c>：撤銷清單（dbx）的版本比對。
    ///
    /// <para>
    /// <b>這一項最容易誤導的地方是「有 dbx」不等於「dbx 夠新」。</b>
    /// 一台機器可以有 dbx、Secure Boot 也開著，但那份撤銷清單是三年前的——
    /// 這把鍵把 SVN 與知識包裡的已知最新值並列，讓落差自己說話。
    /// </para>
    /// </summary>
    public static HardwareFact ToDbxFact(DateTimeOffset at, int? currentSvn, int? knownLatestSvn)
    {
        if (currentSvn is null)
            return Unknown(DbxKey, "撤銷清單版本", at, "本機 dbx 的 SVN");

        if (knownLatestSvn is null)
            return new HardwareFact(DbxKey, Category, "撤銷清單版本",
                $"本機 dbx SVN {currentSvn}；知識包沒有收錄已知最新值", "級",
                Source, FactTrustLevel.Reported, false, at, currentSvn,
                FactAvailability.Unknown,
                "沒有可比較的最新值——「有 dbx」不等於「dbx 夠新」，但這裡無法判斷");

        if (currentSvn >= knownLatestSvn)
            return new HardwareFact(DbxKey, Category, "撤銷清單版本",
                $"本機 dbx SVN {currentSvn} ≥ 知識包已知最新 {knownLatestSvn}", "級",
                Source, FactTrustLevel.Reported, false, at, currentSvn,
                FactAvailability.Present, null);

        return new HardwareFact(DbxKey, Category, "撤銷清單版本",
            $"本機 dbx SVN {currentSvn} 低於知識包已知最新 {knownLatestSvn}（差 {knownLatestSvn - currentSvn} 級）",
            "級", Source, FactTrustLevel.Reported, false, at, currentSvn,
            // 這是「有落差」——使用者該知道，但這不是讀取失敗。
            FactAvailability.Unknown,
            $"撤銷清單落後 {knownLatestSvn - currentSvn} 級；落後本身不是故障，但它是一個需要被看見的事實");
    }

    /// <summary>
    /// 產生 <c>psu.model</c>：電源型號與容量對照。
    /// <b>型號查不到時不猜容量</b>——猜出來的瓦數會直接影響「這顆夠不夠」的判斷。
    /// </summary>
    public static HardwareFact ToPsuFact(DateTimeOffset at, string? model, int? knownWattage)
    {
        if (string.IsNullOrWhiteSpace(model))
            return Unknown(PsuKey, "電源型號對照", at, "電源型號");

        if (knownWattage is null)
            return new HardwareFact(PsuKey, Category, "電源型號對照",
                $"型號 {model} 不在知識包內——沒有可對照的額定瓦數", "筆",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported,
                "查不到型號就不給瓦數：猜出來的容量會直接影響「這顆夠不夠」的判斷");

        return new HardwareFact(PsuKey, Category, "電源型號對照",
            $"型號 {model}：知識包記載額定 {knownWattage} W", "筆",
            Source, FactTrustLevel.Reported, false, at, knownWattage.Value,
            FactAvailability.Present,
            "知識包的瓦數是型號標稱值，不是這顆電源當下的輸出能力");
    }

    /// <summary>本機讀不到要查的值——三把鍵共用的這一條，措辭必須一致。</summary>
    private static HardwareFact Unknown(string key, string name, DateTimeOffset at, string subject) =>
        new(key, Category, name, $"本機讀不到{subject}", "", Source,
            FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
            $"沒有{subject}就無從對照——這不是「沒有問題」");
}
