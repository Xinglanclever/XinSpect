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
public static class FiveElements
{
    /// <summary>圓心文字。</summary>
    public const string Caption = "五族共和\n攜手共進";

    /// <summary>五個節點，順時針。</summary>
    public static IReadOnlyList<FiveElementNode> Nodes { get; } =
    [
        new("", "Claude Opus 5\n主力軍", 0),
        new("", "ChatGPT 5.6 Sol\n遠征軍", 72),
        new("", "Claude Opus 4.8\n先遣軍", 144),
        new("", "Claude Opus 4.6\n預備隊", 216),
        new("", "DeepSeek V4.1 Flash\n來做客的大肥魚", 288),
    ];

    /// <summary>GLM 5.3 Flash 獨立球（拉完了湊數的）。</summary>
    public static FiveElementNode GlmExtra { get; } = new("", "GLM 5.3 Flash\n拉完了湊數的", 0);

    /// <summary>
    /// 五角星的連線順序（節點索引）。每次<b>跨兩格</b>，走五步正好回到起點並且經過每個節點一次
    /// ——這是五角星；跨一格只會得到外圈那個五邊形。
    /// </summary>
    public static IReadOnlyList<int> StarOrder { get; } = [0, 2, 4, 1, 3];
}
