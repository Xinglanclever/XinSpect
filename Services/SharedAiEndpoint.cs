namespace XinSpect;

/// <summary>
/// 「免費共用」AI 額度的連線設定：模型由 <b>Cloudflare Workers AI</b> 提供，經作者自架的中轉
/// 分享給所有使用者。介面上一律標注上游是誰——這條額度不是作者自己訓練或自己託管的模型。
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
/// 走中轉之後，這支執行檔裡<b>一個祕密都沒有</b>，只有一個公開網址：模型是由 Cloudflare 的
/// Workers AI 綁定在伺服器那一側呼叫的，這台程式從頭到尾拿不到、也不需要任何金鑰
/// （日後若改接外部端點，金鑰放在 Worker 的 Secret 裡，同樣不離開伺服器）。限流、模型選擇、
/// 每日總量上限與隨時可關的開關也都在伺服器那一側——被濫用時作者關得掉，而且不必重新發版。
/// 部署方式見 <c>cloudflare/ai-proxy/README.md</c>。
/// </para>
/// <para>
/// <b>全面開放（2026-10-26 起）</b>：自由對話、一鍵評價、主動診斷與診斷代理都走這條額度。
/// 先前只開放「一鍵評價」，是為了讓有限的免費額度撐久一點；但使用者分不出
/// 「這條額度不能對話」與「AI 壞了」，而限制的代價比它省下的額度更貴。
/// 額度上限、限流與總量控制全部留在中轉那一側——被濫用時作者關得掉、調得動，不必重新發版。
/// </para>
/// </remarks>
public static class SharedAiEndpoint
{
    /// <summary>
    /// 中轉端點（OpenAI 相容）。空字串代表作者尚未啟用這條共用額度，介面上該選項會停用。
    /// 這是<b>公開網址</b>，不是祕密：真正的權限在 Worker 那一側，端點被誰知道都無所謂。
    /// 換一個部署位置就改這裡，另見 <c>cloudflare/ai-proxy/README.md</c>。
    /// </summary>
    public static readonly string BaseUrl = "https://xinspect-ai.xinspect-tools.workers.dev/v1";

    /// <summary>
    /// 送給中轉的模型名稱。<b>刻意寫死成一個代號</b>：實際用哪個模型由中轉決定，
    /// 使用者無法從這裡點一個更貴的模型。
    /// </summary>
    public static readonly string Model = "auto";

    /// <summary>
    /// 中轉接受的提示詞字元上限。<b>這是中轉那一側的限制，不是本程式可以放寬的</b>：
    /// 送超過這個長度，Worker 直接回 413，而使用者看到的只是一句「呼叫失敗」。
    /// 本程式在共用額度下會先把預算夾到這裡，讓「截斷」發生在本機並在提示裡說明，
    /// 而不是把請求丟出去之後才失敗。
    /// <para>
    /// 數字必須與 <c>cloudflare/ai-proxy/worker.js</c> 的 <c>LIMITS.maxPromptChars</c> 一致；
    /// 兩邊各自寫死會漂移，所以有一條測試直接讀那個檔案當守門（<c>RelayLimitParityTests</c>）。
    /// </para>
    /// </summary>
    public const int PromptCharCap = 60_000;

    /// <summary>共用額度是否可用（作者是否已填入中轉網址）。</summary>
    public static bool IsConfigured => BaseUrl.Trim().Length > 0;

    /// <summary>
    /// 介面上的選項文字。已啟用時直接標出上游是 Cloudflare Workers AI：使用者有權知道
    /// 自己的硬體資料交給了誰，而「作者提供」這種說法會讓人誤以為模型是作者自己的。
    /// </summary>
    public static string OptionText => IsConfigured
        ? "免費共用（Cloudflare Workers AI）"
        : "免費共用（作者尚未啟用）";

    /// <summary>
    /// 中國大陸使用者看到的額外說明：Cloudflare 在中國大陸被 GFW 封鎖，
    /// 不透過代理無法連線。本程式不內建任何代理功能。
    /// </summary>
    public const string GfwNotice =
        "⚠ 中國大陸使用者注意：共用額度與留言建議的中轉伺服器架設在 Cloudflare 上，"
        + "而 Cloudflare 的 workers.dev 網域在中國大陸被 GFW（防火長城）封鎖。"
        + "若你在中國大陸且未使用代理，這兩項功能將無法連線。"
        + "本程式不內建任何代理功能——請改用本機 Ollama（完全離線）或自填可連線的 OpenAI 相容端點。";
}
