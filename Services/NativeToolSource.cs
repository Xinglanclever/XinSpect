namespace XinSpect;

/// <summary>
/// OS 內建命令／工具的可注入接縫。
/// </summary>
/// <remarks>
/// 形狀照 <see cref="IAcpiTableSource"/>：可用性＋不可用原因＋資料。
/// 真實以命令列取；測試注入固定輸出，不執行任何行程。
/// <para>
/// <b>這一條接縫存在的理由：</b>Windows 已經提供的能力（例如 <c>powercfg</c>）不需要本專案重寫一份，
/// 也不該盲目把工作派給它就算了。留下接口——把命令與原樣輸出帶進畫面，
/// 使用者要自己再跑一次也行。空著的地方由 <see cref="Available"/> 與
/// <see cref="UnavailableReason"/> 誠實填起來，而不是靜靜消失。
/// </para>
/// </remarks>
public interface INativeToolSource
{
    /// <summary>這條接縫在這台機器上能不能用。</summary>
    bool Available { get; }

    /// <summary>不能用的原因。<b>能用時必須是 null；不能用時必須說得出原因。</b></summary>
    string? UnavailableReason { get; }

    /// <summary>取得區段（含原樣輸出）。</summary>
    IReadOnlyList<NativeToolSection> Run();
}

/// <summary>
/// 一段原生輸出：命令、意義說明、以及<b>原樣</b>輸出。
/// </summary>
/// <remarks>
/// <b>為什麼 <see cref="Output"/> 不解析：</b>這些命令的文字隨系統語言翻譯，照關鍵字比對
/// 會在別的語言上靜靜失效，而「解析失敗」與「什麼都沒發現」長得一模一樣——那是最糟的一種錯。
/// 所以原樣呈現，並把命令照抄給使用者，讓他可以自己再跑一次驗證。
/// <para>
/// 這不是新發明：它就是原本的 <c>SleepSection</c>（睡眠診斷那四欄）推廣出來的型別，
/// 讓第二、第三個原生工具沿用它，而不是各自再寫一份一樣的容器。
/// </para>
/// </remarks>
public sealed record NativeToolSection
{
    public required string Title { get; init; }

    /// <summary>實際執行的命令（照抄給使用者，他可以自己再跑一次驗證）。</summary>
    public required string Command { get; init; }

    /// <summary>這一段在講什麼、看到什麼該怎麼辦。</summary>
    public required string What { get; init; }

    /// <summary>命令的原樣輸出。</summary>
    public required string Output { get; init; }
}
