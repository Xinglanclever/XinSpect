namespace XinSpect;

/// <summary>
/// 五行輪上的一個節點：元素字、掛在它上面的協力模型，以及它在圓周上的角度。
/// </summary>
/// <param name="Element">元素字（金／木／水／火／土）。</param>
/// <param name="Label">這個位置要填入的名字。</param>
/// <param name="AngleDeg">圓周角度，0° 指向正上方、順時針遞增。</param>
public readonly record struct FiveElementNode(string Element, string Label, double AngleDeg)
{
    /// <summary>
    /// 單位圓上的座標。圓心為原點，<b>y 軸向下</b>（螢幕座標系），所以 0° 是 (0, −1)。
    /// </summary>
    public (double X, double Y) Unit
    {
        get
        {
            double rad = AngleDeg * Math.PI / 180;
            return (Math.Sin(rad), -Math.Cos(rad));
        }
    }

    /// <summary>以 <paramref name="cx"/>／<paramref name="cy"/> 為圓心、半徑 <paramref name="r"/> 的實際座標。</summary>
    public (double X, double Y) At(double cx, double cy, double r)
    {
        var (ux, uy) = Unit;
        return (cx + ux * r, cy + uy * r);
    }
}

/// <summary>
/// 「五族共和」五行輪的資料與幾何：哪五個名字、擺在哪五個位置、五角星怎麼連。
/// </summary>
/// <remarks>
/// <para>
/// 排法照使用者給的那張傳統五行圖：<b>金在正上，順時針依序 木、水、火、土</b>。
/// 這裡刻意<b>不宣稱</b>外圈是相生、內星是相剋——那張圖的排列其實對不上標準的
/// 木→火→土→金→水 相生順序（標準排法下正上方是金時，右上角應該是水而不是木）。
/// 坊間的命理插圖多半是這樣的裝飾性排列，本程式只是照著畫，不替它補一套不成立的說法。
/// </para>
/// <para>
/// 幾何抽出來成純函式的理由只有一個：<see cref="StarOrder"/> 跨兩格才是五角星，跨一格會畫成
/// 五邊形。那是這段程式唯一會安靜畫錯的地方，值得一條測試釘死。
/// </para>
/// </remarks>
public static class FiveElements
{
    /// <summary>圓心那四個字。</summary>
    public const string Caption = "五族共和";

    /// <summary>五個節點，順時針，金在正上。</summary>
    public static IReadOnlyList<FiveElementNode> Nodes { get; } =
    [
        new("金", "Claude Opus 5", 0),
        new("木", "Claude Opus 5 Thinking", 72),
        new("水", "Claude Opus 4.8", 144),
        new("火", "Claude Opus 4.8 Thinking", 216),
        new("土", "Claude Opus 4.6", 288),
    ];

    /// <summary>
    /// 五角星的連線順序（節點索引）。每次<b>跨兩格</b>，走五步正好回到起點並且經過每個節點一次
    /// ——這是五角星；跨一格只會得到外圈那個五邊形。
    /// </summary>
    public static IReadOnlyList<int> StarOrder { get; } = [0, 2, 4, 1, 3];
}
