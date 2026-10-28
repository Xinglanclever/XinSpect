namespace XinSpect;

/// <summary>
/// 安全開機四環（U10 第三圈 SE-0001／SE-0002，第 314 輪）：PK→KEK→db→dbx 一次講完，並與 Setup Mode 交叉。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼四環要一起看：</b>既有事實已經分別讀了每一環（<c>uefi.pk</c>、<c>uefi.kek.count</c>、
/// <c>uefi.db.count</c>、<c>uefi.dbx.count</c>），但讀者要自己拼：PK 有了、KEK 空著、
/// db 空著而 dbx 一堆——這三個組合的意義完全不同。這一支只做<b>併起來陳述</b>，
/// 不重讀任何變數（輸入就是前面幾組已經產生的事實）。
/// </para>
/// <para>
/// <b>「空」與「讀不到」不同：</b>變數讀得到但零條簽章＝已知的空（寫成 <c>secboot.empty.*</c>）；
/// 變數讀不到＝不知道，四環就不能說「齊備」（總覽轉 Unknown 並指名哪一環不知道）。
/// 把兩者混成一句是這一頁最容易犯的錯——空著是證據，讀不到是缺口。
/// </para>
/// <para>
/// <b>不做的判斷：</b>不評價金鑰內容好壞、不推斷「這樣能不能開機」（那取決於韌體實作）、
/// 不假裝看過 VendorKeys（本版沒有讀那個變數）。這些界線寫在事實值裡，不是只寫在註解裡。
/// </para>
/// </remarks>
public static class SecureBootChainFactsService
{
    /// <summary>四環總覽（靜態鍵）。</summary>
    public const string ChainKey = "secboot.chain";

    /// <summary>逐環「存在但零條目」家族：<c>secboot.empty.{環}</c>（成員＝這一台真的空著的環）。</summary>
    public const string EmptyPrefix = "secboot.empty.";

    private const string Category = "安全開機";
    private const string Source = "併既有 UEFI 事實（uefi.pk／uefi.kek.count／uefi.db.count／uefi.dbx.count／uefi.setup_mode）";

    /// <summary>一個環：代號、名稱、它管什麼、以及「條數事實」與「讀不到事實」的鍵。</summary>
    private sealed record Ring(string Id, string Name, string Role, string? CountKey, string AbsentKey);

    private static readonly Ring[] Rings =
    [
        new("pk", "PK（平台金鑰）", "信任根：PK 在，信任根才算立起來（Setup Mode 可據此關閉）",
            null, "uefi.pk"),
        new("kek", "KEK（金鑰交換金鑰）", "更新授權：沒有它，db／dbx 無法由作業系統更新",
            "uefi.kek.count", "uefi.kek"),
        new("db", "db（允許簽章資料庫）", "允許清單：哪些簽章可以在 Secure Boot 下開機",
            "uefi.db.count", "uefi.db"),
        new("dbx", "dbx（撤銷簽章資料庫）", "撤銷清單：哪些簽章不准開機",
            "uefi.dbx.count", "uefi.dbx"),
    ];

    /// <summary>把四環與 Setup Mode 併成事實；不做 I/O、不重讀變數。</summary>
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<HardwareFact> facts, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var factsList = new List<HardwareFact>();
        var described = new List<string>(Rings.Length);
        var empty = new List<Ring>();
        var undetermined = new List<string>();

        foreach (var ring in Rings)
        {
            var count = ring.CountKey is null ? null : Present(facts, ring.CountKey);
            if (ring.CountKey is not null)
            {
                if (count?.NumericValue is { } n)
                {
                    int signatures = (int)n;
                    described.Add($"{ring.Name} {signatures} 條");
                    if (signatures == 0) empty.Add(ring);
                }
                else
                {
                    undetermined.Add(ring.Name);
                    described.Add($"{ring.Name} 讀不到");
                }
                continue;
            }

            // PK 沒有「條數」：Present 就代表讀到一把有效長度的平台金鑰（>16 bytes 才會是 Present）。
            var pk = Present(facts, ring.AbsentKey);
            if (pk is not null)
                described.Add($"{ring.Name} 有（{pk.Value}）");
            else
            {
                undetermined.Add(ring.Name);
                described.Add($"{ring.Name} 讀不到");
            }
        }

        var setupMode = Present(facts, "uefi.setup_mode");
        int withContent = Rings.Length - empty.Count - undetermined.Count;

        factsList.Add(ChainFact(described, empty, undetermined, setupMode, withContent, at));

        foreach (var ring in empty)
            factsList.Add(EmptyFact(ring, at));

        return factsList.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact ChainFact(
        List<string> described,
        List<Ring> empty,
        List<string> undetermined,
        HardwareFact? setupMode,
        int withContent,
        DateTimeOffset at)
    {
        string setupText = setupMode is null
            ? "Setup Mode 讀不到（不能判斷信任根是在安裝模式還是已鎖定）"
            : $"Setup Mode＝{setupMode.Value}";

        var parts = new List<string> { string.Join("／", described), setupText };

        if (empty.Count > 0)
            parts.Add($"{empty.Count} 環空著（{string.Join("、", empty.Select(r => r.Name))}；逐環說明見 secboot.empty.*）");

        if (undetermined.Count > 0)
        {
            parts.Add($"**但不能說四環齊備**：{string.Join("、", undetermined)} 讀不到");
            return new HardwareFact(ChainKey, Category, "安全開機四環", string.Join("；", parts), "環",
                Source, FactTrustLevel.Unknown, false, at, withContent, FactAvailability.Unknown,
                $"{string.Join("、", undetermined)} 沒有讀到——「讀不到」不是「空著」，四環是否完整無法判斷。");
        }

        if (setupMode is null)
        {
            return new HardwareFact(ChainKey, Category, "安全開機四環", string.Join("；", parts), "環",
                Source, FactTrustLevel.Unknown, false, at, withContent, FactAvailability.Unknown,
                "uefi.setup_mode 讀不到：四環的內容有了，但它們是不是已經鎖定（信任根是否已立）不知道。");
        }

        parts.Add(empty.Count == 0
            ? "四環齊備——「齊備」只說四環都有內容，不評價金鑰內容好壞，也不等於設定正確"
            : $"四環缺 {empty.Count} 環——缺的環各自少了什麼，見 secboot.empty.*");
        parts.Add("本版沒有讀 VendorKeys，不對廠商金鑰狀態下結論");

        return new HardwareFact(ChainKey, Category, "安全開機四環", string.Join("；", parts), "環",
            Source, FactTrustLevel.Derived, false, at, withContent, FactAvailability.Present, null);
    }

    private static HardwareFact EmptyFact(Ring ring, DateTimeOffset at)
        => new(EmptyPrefix + ring.Id, Category, $"{ring.Name}：空著", 
            $"{ring.Name}：變數讀得到，但一條簽章都沒有（不是讀不到）。這一環的角色：{ring.Role}。"
            + "空著是證據，它代表什麼風險取決於韌體怎麼用這一環——本版不替它下「能不能開機」的結論。",
            "", Source, FactTrustLevel.Derived, false, at, 0, FactAvailability.Present, null);

    private static HardwareFact? Present(IReadOnlyList<HardwareFact> facts, string key)
        => facts.FirstOrDefault(f => f.Key == key && f.Availability == FactAvailability.Present);
}
