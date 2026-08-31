namespace XinSpect;

/// <summary>
/// 「免費共用」AI 額度的連線設定：由作者自付費用、經自建中轉分享給所有使用者。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是中轉而不是把金鑰內置在程式裡</b>：任何隨程式一起交到使用者手上的祕密都不是祕密。
/// 曦覽是開源、未混淆的 .NET 單一檔案程式，把金鑰內置的話——
/// ①<c>strings</c> 就能撈出字串常數；②把內嵌組件抽出來丟進 dnSpy／ILSpy 幾乎等於直接看原始碼；
/// ③就算改成 AES 加密，解密金鑰也必須一起交出去，躺在同一份 IL 裡；
/// ④最後一道更是無法迴避：程式必須把「明碼金鑰」送到端點才通得過驗證，
/// 在本機裝一張根憑證用 Fiddler／mitmproxy 攔一次，或直接掛除錯器，就看得到。
/// 所以那條路上的「加密」只能提高門檻，不能保密；而公開倉庫裡的 <c>sk-</c> 金鑰還會被
/// GitHub 的祕密掃描偵測、由供應商自動吊銷。
/// </para>
/// <para>
/// 走中轉之後，這支執行檔裡<b>一個祕密都沒有</b>，只有一個公開網址：真正的金鑰放在
/// Cloudflare Workers 的 Secret 裡，永遠不離開伺服器。限流、模型白名單、每日總量上限與
/// 隨時可關的開關也都在伺服器那一側——被濫用時作者關得掉，而且不必重新發版。
/// 部署方式見 <c>cloudflare/ai-proxy/README.md</c>。
/// </para>
/// <para>
/// <b>刻意的限制</b>：共用額度只開放「一鍵評價」這種一次性請求（<see cref="AiRequestKind.Evaluate"/>），
/// 自由對話、主動診斷與診斷代理（會連續呼叫工具、一次評價可能變成六七次請求）都不走共用額度。
/// 這不是技術限制，是為了讓有限的額度撐得久一點；想要完整體驗的人可以改用本機 Ollama（免費）
/// 或自填金鑰。
/// </para>
/// </remarks>
public static class SharedAiEndpoint
{
    /// <summary>
    /// 中轉端點（OpenAI 相容）。空字串代表作者尚未啟用這條共用額度，介面上該選項會停用。
    /// 部署好 Worker 之後把網址填在這裡即可，例如
    /// <c>https://xinspect-ai.&lt;你的子網域&gt;.workers.dev/v1</c>。
    /// </summary>
    public static readonly string BaseUrl = "";

    /// <summary>
    /// 送給中轉的模型名稱。<b>刻意寫死成一個代號</b>：實際用哪個模型由中轉決定，
    /// 使用者無法從這裡點一個更貴的模型。
    /// </summary>
    public static readonly string Model = "auto";

    /// <summary>共用額度是否可用（作者是否已填入中轉網址）。</summary>
    public static bool IsConfigured => BaseUrl.Trim().Length > 0;

    /// <summary>介面上的選項文字。</summary>
    public static string OptionText => IsConfigured
        ? "免費共用（作者提供・無須金鑰）"
        : "免費共用（作者尚未啟用）";

    /// <summary>這條額度目前允許哪一種請求。</summary>
    public static bool Allows(AiRequestKind kind) => kind == AiRequestKind.Evaluate;

    /// <summary>不允許時顯示給使用者的說明（要說清楚替代方案，不能只說不行）。</summary>
    public const string NotAllowedText =
        "共用額度只開放「一鍵評價」。\n\n" +
        "這條額度是作者自付費用分享的，額度有限，所以自由對話、主動診斷與診斷代理沒有納入——"
        + "代理模式一次提問可能連續發出六、七次請求，很快就會把大家的額度用完。\n\n"
        + "想要不受限的完整體驗，請到「設定 → AI 評價」改成：\n"
        + "・本機 Ollama：免費、離線、資料不出本機（建議）\n"
        + "・OpenAI 相容 API：自填端點與金鑰，額度是你自己的";
}
