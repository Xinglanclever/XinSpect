using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 電池與 EC 充放狀態（ULTIMATE-1000 階段二 P2-095）。
///
/// <para>
/// <b>這一項不新增 WMI 讀取——它把既有的 <see cref="BatteryService"/> 結果變成事實。</b>
/// 既有的服務產的是 UI 模型（<see cref="BatteryInfo"/>），而事實層需要
/// 可查詢、可三態的鍵。<b>狀態文字的映射直接引用既有的
/// <see cref="BatteryService.StatusText"/>，不複製一份。</b>
/// </para>
///
/// <para>
/// <b>而這個鍵與既有的 <c>pw.battery.summary</c> 是不同的東西：</b>
/// </para>
/// <list type="bullet">
///   <item><c>pw.battery.summary</c>——<b>電池健康</b>：設計容量、滿充容量、
///     循環次數、健康度。那回答「這顆電池老了沒有」。</item>
///   <item><c>ec.battery.state</c>——<b>充放狀態</b>：現在在放電還是充電、
///     電量幾趴。那回答「現在是什麼狀況」。</item>
/// </list>
/// <para>
/// 兩者都可以由同一顆電池讀出，但問題不同——<b>而把它們混成一把鍵，
/// 就會讓「健康但沒接電」與「接電但快掛了」看起來一樣。</b>
/// </para>
///
/// <para>
/// <b>界線：這裡不預測續航、不判斷健康。</b>那些是 <c>pw.battery.summary</c> 的領域。
/// </para>
/// </summary>
public static class BatteryStateFactsService
{
    private const string Category = "平台";
    private const string Source = "WMI Win32_Battery（既有的 BatteryService）";

    public const string StateKey = "ec.battery.state";

    /// <summary>
    /// <paramref name="battery"/> 是既有的讀取結果。
    /// <c>null</c> 代表讀取本身失敗（WMI 不可用、丟例外）——
    /// <b>而那與「這台機器沒有電池」是不同的斷言</b>
    /// （後者是 <c>Present == false</c>，一個確定的答案）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, BatteryInfo? battery)
    {
        if (battery is null)
            return
            [
                new HardwareFact(StateKey, Category, "電池充放狀態", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "**WMI 讀取本身失敗（類別不存在或查詢被拒）。**"
                    + "**「讀不到」與「這台機器沒有電池」是不同的斷言**——"
                    + "後者是一個確定的答案（桌機沒有電池是正常的），"
                    + "而把前者也說成「沒有電池」會讓一次查詢失敗看起來像一個硬體事實"),
            ];

        if (!battery.Present)
            return
            [
                new HardwareFact(StateKey, Category, "電池充放狀態", "沒有電池", "", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "**這是確定的答案，不是讀不到。**"
                    + "桌機與伺服器沒有電池是正常的——"
                    + "而既有的 BatteryService 也是這樣回報的"
                    + "（「未偵測到電池（桌機或伺服器環境）」）"),
            ];

        string chemistry = string.IsNullOrWhiteSpace(battery.Chemistry) ? "" : $"、{battery.Chemistry}";
        return
        [
            new HardwareFact(StateKey, Category, "電池充放狀態",
                $"{battery.StatusText}、電量 {battery.ChargePercent}%"
                + (string.IsNullOrWhiteSpace(battery.Name) ? "" : $"（{battery.Name}{chemistry}）"),
                "%", Source, FactTrustLevel.Measured, false, at, battery.ChargePercent,
                FactAvailability.Present,
                "**這個鍵回答「現在是什麼狀況」，而既有的 pw.battery.summary 回答"
                + "「這顆電池老了沒有」**（設計容量／滿充容量／循環次數／健康度）。"
                + "兩者都可以由同一顆電池讀出，但問題不同——"
                + "**把它們混成一把鍵，就會讓「健康但沒接電」與「接電但快掛了」看起來一樣。**"
                + "而**這裡不預測續航、不判斷健康**——那些是 pw.battery.summary 的領域"),
        ];
    }
}
