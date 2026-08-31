using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>圖上的一個實體插槽。</summary>
public sealed class DimmSlotView
{
    /// <summary>SMBIOS 原始 Locator，例如 <c>DIMM_A1</c>、<c>ChannelA-DIMM0</c>。</summary>
    public required string Locator { get; init; }
    /// <summary>畫在插槽上的短標籤；推得出通道時是「A1」，推不出就用原始名稱。</summary>
    public required string Label { get; init; }
    public required bool Occupied { get; init; }
    /// <summary>
    /// 分組用的通道鍵。單插槽平台就是通道字母（<c>A</c>）；
    /// 雙插槽／多控制器的板子會帶上限定詞（<c>P1-A</c>、<c>C0-A</c>），
    /// 因為兩顆 CPU 各自的「通道 A」是不同的通道，混在一起會導出「把記憶體移到別的通道」這種錯建議。
    /// 推不出來是空字串（不猜）。
    /// </summary>
    public string Channel { get; init; } = "";
    /// <summary>通道字母（不含限定詞）；推不出來是空字串。</summary>
    public string Letter { get; init; } = "";
    /// <summary>同通道內的排序用序號；沒有數字時為 <c>int.MaxValue</c>。</summary>
    public int Index { get; init; } = int.MaxValue;

    public string SizeText { get; init; } = "";
    public string PartText { get; init; } = "";
    public string SpeedText { get; init; } = "";
    public string VendorText { get; init; } = "";

    /// <summary>插槽下方的第二行說明。</summary>
    public string Detail => Occupied
        ? string.Join(" ・ ", new[] { SizeText, SpeedText }.Where(x => x.Length > 0))
        : "空";

    /// <summary>滑過插槽時的完整內容。</summary>
    public string Tip => Occupied
        ? string.Join("\n", new[]
        {
            $"插槽：{Locator}",
            VendorText.Length > 0 ? $"製造商：{VendorText}" : "",
            PartText.Length > 0 ? $"型號：{PartText}" : "",
            SizeText.Length > 0 ? $"容量：{SizeText}" : "",
            SpeedText.Length > 0 ? $"速率：{SpeedText}" : "",
        }.Where(x => x.Length > 0))
        : $"插槽：{Locator}\n狀態：未安裝模組";
}

/// <summary>一個通道（或推不出通道時的單一群組）。</summary>
public sealed class DimmChannelView
{
    public required string Name { get; init; }
    public required List<DimmSlotView> Slots { get; init; }
    public int Occupied => Slots.Count(s => s.Occupied);
}

/// <summary>插槽配置圖的完整資料：分組、統計，以及一段不誇大的判讀。</summary>
public sealed class DimmLayoutView
{
    public List<DimmChannelView> Channels { get; init; } = [];
    /// <summary>攤平後的插槽清單，順序與圖上一致。</summary>
    public List<DimmSlotView> Slots { get; init; } = [];
    /// <summary>是否真的從 Locator／Bank 推出通道編號。false 時 <see cref="Channels"/> 只有一組。</summary>
    public bool ChannelsKnown { get; init; }

    public int SlotCount => Slots.Count;
    public int OccupiedCount => Slots.Count(s => s.Occupied);
    public bool HasData => Slots.Count > 0;

    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>值得一提但不影響能不能開機的觀察；沒有就是空清單。</summary>
    public List<string> Notes { get; init; } = [];
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>
/// 把 SMBIOS Type 17 的記憶體裝置清單整理成一張插槽配置圖。
/// </summary>
/// <remarks>
/// <para>
/// 通道編號是從 Locator／Bank Locator 的命名<b>推斷</b>的，不是量到的：韌體怎麼寫，這裡就怎麼讀。
/// 推不出來時就明講「看不出通道編號」，不會拿插槽順序硬湊成 A/B——插錯槽的代價是頻寬砍半，
/// 猜錯了比不猜更糟。真正走幾個通道要看「記憶體真實面貌」量到的數字。
/// </para>
/// <para>純函式，沒有任何硬體存取，因此可以完整單元測試。</para>
/// </remarks>
public static class DimmLayout
{
    /// <summary>Locator／Bank 裡明寫 CHANNEL 的形式：<c>ChannelA-DIMM0</c>、<c>P0 CHANNEL A</c>。</summary>
    private static readonly Regex ChannelWord =
        new(@"CHANNEL\s*[_\-]?\s*([A-L])(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>去掉 DIMM／SLOT 字樣後剩下的「字母＋數字」形式：<c>DIMM_A1</c>、<c>A1</c>、<c>DIMMB2</c>。</summary>
    private static readonly Regex LetterIndex =
        new(@"(?<![A-Z0-9])([A-L])\s*[_\-]?\s*(\d{1,2})(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingDigits = new(@"(\d{1,2})(?!.*\d)", RegexOptions.Compiled);

    /// <summary>只剩一個孤立字母的形式：<c>DIMM_A</c>。前後都不能再接字母或數字，免得抓到單字裡的字母。</summary>
    private static readonly Regex LoneLetter =
        new(@"(?<![A-Z0-9])([A-L])(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 插槽／控制器限定詞：<c>Controller0-…</c>、<c>P1-DIMMA1</c>、<c>CPU0_DIMMA1</c>、<c>Node1…</c>。
    /// 一定要在推通道字母<b>之前</b>抽掉，否則 <c>P1</c> 的 P 會被當成通道編號。
    /// </summary>
    private static readonly Regex Qualifier =
        new(@"(?<![A-Z0-9])(CONTROLLER|SOCKET|NODE|CPU|IMC|P)\s*[_\-]?\s*(\d{1,2})(?![A-Z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>從一列 SMBIOS 記憶體裝置建出配置圖。傳空清單就得到 <see cref="DimmLayoutView.HasData"/> 為 false 的結果。</summary>
    public static DimmLayoutView Build(IReadOnlyList<SmbiosDimmRow> rows)
    {
        var slots = new List<DimmSlotView>();
        foreach (var r in rows)
        {
            string locator = Clean(r.Locator);
            if (locator.Length == 0) locator = "（未命名插槽）";
            var (ch, idx) = ParseChannel(locator, Clean(r.Bank));
            string letter = ch.Contains('-') ? ch[(ch.IndexOf('-') + 1)..] : ch;
            bool occupied = IsOccupied(r.Size);

            slots.Add(new DimmSlotView
            {
                Locator = locator,
                Label = letter.Length > 0 && idx != int.MaxValue ? $"{letter}{idx}" : locator,
                Occupied = occupied,
                Channel = ch,
                Letter = letter,
                Index = idx,
                SizeText = occupied ? Clean(r.Size) : "",
                PartText = occupied ? Clean(r.Part) : "",
                SpeedText = occupied ? Clean(r.Speed) : "",
                VendorText = occupied ? Clean(r.Manufacturer) : "",
            });
        }

        bool known = slots.Count > 0 && slots.All(s => s.Channel.Length > 0);
        var channels = known
            ? slots.GroupBy(s => s.Channel).OrderBy(g => g.Key, StringComparer.Ordinal)
                   .Select(g => new DimmChannelView
                   {
                       Name = ChannelName(g.Key),
                       Slots = g.OrderBy(s => s.Index).ThenBy(s => s.Locator, StringComparer.Ordinal).ToList(),
                   }).ToList()
            : slots.Count > 0
                ? [new DimmChannelView { Name = "插槽", Slots = slots.ToList() }]
                : [];

        var ordered = channels.SelectMany(c => c.Slots).ToList();
        var notes = Notes(ordered, known);

        return new DimmLayoutView
        {
            Channels = channels,
            Slots = ordered,
            ChannelsKnown = known,
            Headline = Headline(ordered, channels, known),
            Detail = Detail(ordered, channels, known),
            Notes = notes,
        };
    }

    /// <summary>把通道鍵寫成人看的列名：<c>A</c> → 「通道 A」、<c>C0-A</c> → 「通道 A（C0）」。</summary>
    private static string ChannelName(string key)
    {
        int dash = key.IndexOf('-');
        return dash < 0 ? $"通道 {key}" : $"通道 {key[(dash + 1)..]}（{key[..dash]}）";
    }

    /// <summary>「未安裝」與「—」都不算裝了模組；其餘（有容量字樣）算裝了。</summary>
    private static bool IsOccupied(string size)
    {
        string s = Clean(size);
        return s.Length > 0 && s != "未安裝" && s != "0" && s != "No Module Installed";
    }

    private static string Clean(string? s)
    {
        s = s?.Trim() ?? "";
        // 韌體常留下這些佔位字樣，當成沒填。「—」是本程式自己在讀不到時填的破折號，不是型號
        return s is "Unknown" or "unknown" or "To Be Filled By O.E.M." or "N/A" or "None" or "NO DIMM"
                 or "—" or "-" ? "" : s;
    }

    /// <summary>
    /// 從 Locator（優先）或 Bank 推斷通道與序號；推不出通道時回空字串。
    /// 回傳的通道帶插槽／控制器限定詞（<c>C0-A</c>），這樣兩顆 CPU 的通道 A 不會被併成一個。
    /// </summary>
    internal static (string Channel, int Index) ParseChannel(string locator, string bank)
    {
        int idx = int.MaxValue;

        // 0) 先抽掉「哪一顆 CPU／哪個控制器」，剩下的才是通道命名。P1 的 P 不是通道編號
        string qual = "";
        var q = Qualifier.Match(locator);
        if (q.Success)
        {
            string head = q.Groups[1].Value.ToUpperInvariant();
            qual = (head == "P" ? "P" : head[..1]) + q.Groups[2].Value;
        }
        string rest = Qualifier.Replace(locator, " ");

        // 1) 明寫 CHANNEL 的最可信
        string letter = "";
        var m = ChannelWord.Match(rest);
        if (m.Success) letter = m.Groups[1].Value.ToUpperInvariant();

        // 2) 沒明寫就找「字母＋數字」，但要先把 DIMM／SLOT 字樣拿掉，免得把 M 當通道
        string bare = Regex.Replace(rest, @"DIMM|SLOT|CHANNEL|BANK", " ", RegexOptions.IgnoreCase);
        var li = LetterIndex.Match(bare);
        if (li.Success)
        {
            if (letter.Length == 0) letter = li.Groups[1].Value.ToUpperInvariant();
            if (int.TryParse(li.Groups[2].Value, out int n)) idx = n;
        }
        else
        {
            // 3) 只有數字（DIMM0／DIMM1）：序號拿得到，通道拿不到就不猜
            var d = TrailingDigits.Match(bare);
            if (d.Success && int.TryParse(d.Groups[1].Value, out int n)) idx = n;

            // 4) 只有字母（DIMM_A）：通道拿得到，序號沒有就留 MaxValue
            if (letter.Length == 0)
            {
                var lone = LoneLetter.Match(bare);
                if (lone.Success) letter = lone.Groups[1].Value.ToUpperInvariant();
            }
        }

        // 5) Locator 推不出來才退到 Bank；兩邊都有卻互相矛盾時寧可不給
        var bm = ChannelWord.Match(bank);
        string fromBank = bm.Success ? bm.Groups[1].Value.ToUpperInvariant() : "";
        if (letter.Length == 0) letter = fromBank;
        else if (fromBank.Length > 0 && fromBank != letter) letter = "";

        if (letter.Length == 0) return ("", idx);
        return (qual.Length > 0 ? $"{qual}-{letter}" : letter, idx);
    }

    private static string Headline(List<DimmSlotView> slots, List<DimmChannelView> channels, bool known)
    {
        if (slots.Count == 0) return "韌體沒有回報任何記憶體插槽。";
        int used = slots.Count(s => s.Occupied);
        string ch = known ? $"，分屬 {channels.Count} 個通道" : "";
        return $"{slots.Count} 個插槽{ch}，其中 {used} 個裝了模組。";
    }

    private static string Detail(List<DimmSlotView> slots, List<DimmChannelView> channels, bool known)
    {
        if (slots.Count == 0)
            return "SMBIOS 沒有 Type 17 結構，或這台機器的記憶體是焊在板上的（焊死的模組沒有插槽可報）。";

        int used = slots.Count(s => s.Occupied);
        if (used == 0)
            return "所有插槽都回報「未安裝」——機器顯然是開著的，所以這幾乎一定是韌體沒有填寫，"
                 + "而不是真的沒有記憶體。這種板子的插槽資訊不可信，請看上面的 SPD／CPU-Z 讀值。";

        if (!known)
        {
            string example = slots[0].Locator;
            return $"這塊主機板的插槽命名（例如「{example}」）看不出通道編號，所以圖上只按插槽順序排、不分通道。"
                 + "想知道實際走幾個通道，看下面「記憶體真實面貌」量到的數字，不要用插槽位置反推。";
        }

        int usedChannels = channels.Count(c => c.Occupied > 0);
        if (channels.Count >= 2 && usedChannels == 1)
        {
            string only = channels.First(c => c.Occupied > 0).Name;
            return $"模組全部集中在{only}。多通道主機板這樣插，記憶體頻寬通常只有成對插的一半——"
                 + "如果手上有兩條以上，把第二條移到另一個通道的插槽（通常是同號的那一個）就能改善。"
                 + "這是依命名推斷，實際交錯狀況以「記憶體真實面貌」為準。";
        }
        if (usedChannels >= 2)
            return $"模組分布在 {usedChannels} 個通道上，這是多通道該有的插法。"
                 + "通道數只代表插對位置；實際頻寬還要看時序與 XMP／EXPO 有沒有啟用。";

        return "依 Locator 命名推斷的通道分組；插槽位置只說明實體排列，不代表記憶體控制器怎麼交錯。";
    }

    private static List<string> Notes(List<DimmSlotView> slots, bool known)
    {
        var notes = new List<string>();
        var used = slots.Where(s => s.Occupied).ToList();
        if (used.Count == 0) return notes;

        var sizes = used.Select(s => s.SizeText).Where(x => x.Length > 0).Distinct().ToList();
        if (sizes.Count > 1)
            notes.Add($"模組容量不一致（{string.Join("、", sizes)}）：多數平台照樣能開，"
                    + "但只有成對的那部分走得到交錯存取，剩下的容量以單通道速度運作。");

        var parts = used.Select(s => s.PartText).Where(x => x.Length > 0).Distinct().ToList();
        if (parts.Count > 1)
            notes.Add($"模組型號不同（{string.Join("、", parts)}）：時序會退到所有模組都吃得下的最低標準，"
                    + "XMP／EXPO 也可能因此無法套用。");

        var speeds = used.Select(s => s.SpeedText).Where(x => x.Length > 0).Distinct().ToList();
        if (speeds.Count > 1)
            notes.Add($"回報速率不一致（{string.Join("、", speeds)}）：全部模組最後會跑在最慢的那個速率上。");

        int empty = slots.Count - used.Count;
        if (empty > 0)
            notes.Add(known
                ? $"還有 {empty} 個空插槽。要加記憶體時，先把成對的模組放在不同通道，再考慮把單一通道插滿。"
                : $"還有 {empty} 個空插槽。");

        return notes;
    }
}
