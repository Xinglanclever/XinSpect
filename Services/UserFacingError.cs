using System.IO;

namespace XinSpect;

/// <summary>
/// 把例外訊息整理成<b>給使用者看</b>的一句話。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要這一層：</b>2026-10-26 使用者回報的畫面是
/// 「儲存失敗／<c>UnavailableReason 超過 512 字元。(Parameter 'UnavailableReason')</c>」。
/// 前半是他沒聽過的欄位名（那一半已經在驗證訊息裡改成人話），後半是 .NET 自動接上的參數名——
/// 使用者看到的應該是一句「發生什麼事、接下來怎麼辦」，而不是開發者契約的字面。
/// 原始訊息仍留在例外物件裡，寫日誌時照樣拿得到；這裡只處理<b>顯示</b>。
/// </para>
/// <para>
/// <b>不做的事：</b>不吞掉訊息、不改寫內容、不把失敗講成成功。整理後的字仍然是原本那句話，
/// 只是拿掉對讀者無意義的尾巴，並在「使用者能自己做點什麼」的情況下補一句提示。
/// </para>
/// </remarks>
public static class UserFacingError
{
    /// <summary>拿掉 .NET 接上的參數名尾巴（<c>(Parameter 'x')</c>），並壓掉多餘空白。</summary>
    public static string Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "沒有提供原因。";
        string text = message.Trim();

        // ArgumentException 家族會自動附加 " (Parameter 'name')"；括號前的空白也要一起吃掉。
        int at = text.LastIndexOf(" (Parameter '", StringComparison.Ordinal);
        if (at < 0) at = text.LastIndexOf(" (Parameter \"", StringComparison.Ordinal);
        if (at > 0 && (text.EndsWith("')", StringComparison.Ordinal)
                       || text.EndsWith("\")", StringComparison.Ordinal)))
            text = text[..at];

        return text.TrimEnd();
    }

    /// <summary>整理訊息，並在能指出下一步的地方補一句。</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string text = Clean(ex.Message);
        return ex switch
        {
            UnauthorizedAccessException => $"{text}（這個資料夾沒有寫入權限——換一個位置，或用系統管理員身分開啟）",
            DirectoryNotFoundException => $"{text}（目標資料夾不存在；這個程式不會替你建立路徑）",
            IOException => $"檔案寫不進去：{text}",
            _ => text,
        };
    }
}
