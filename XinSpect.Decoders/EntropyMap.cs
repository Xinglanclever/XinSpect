using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>一段位元組的熵分析結果。</summary>
/// <param name="Offset">在輸入中的起始位移。</param>
/// <param name="Length">涵蓋位元組數。</param>
/// <param name="ShannonBitsPerByte">Shannon 熵（每個位元組的位元，0–8）。</param>
/// <param name="AllSame">整段皆為同一個位元組值。</param>
/// <param name="AllErased">整段皆為 0xFF（已抹除的快閃區）或整段皆為 0x00——兩者皆為「沒有內容」的指紋。</param>
/// <param name="ZeroFraction">值為 0x00 的比例。</param>
/// <param name="ErasedFraction">值為 0xFF 的比例。</param>
public readonly record struct EntropyBlock(
    int Offset, int Length, double ShannonBitsPerByte,
    bool AllSame, bool AllErased, double ZeroFraction, double ErasedFraction)
{
    /// <summary>壓縮／加密內容的實務門檻：真實壓縮資料每個位元組約 7.5 位元以上。</summary>
    public const double HighEntropyThreshold = 7.5;

    /// <summary>低熵門檻：低於此通常代表空白、填充或高度規律的資料。</summary>
    public const double LowEntropyThreshold = 2.0;

    /// <summary>高熵＝看起來像壓縮或加密的內容。</summary>
    public bool IsHighEntropy => !AllSame && ShannonBitsPerByte >= HighEntropyThreshold;

    /// <summary>低熵＝空白、填充或高度規律。</summary>
    public bool IsLowEntropy => AllSame || ShannonBitsPerByte < LowEntropyThreshold;

    /// <summary>抹除區（整段 0xFF）——快閃尚未寫入的標準指紋。</summary>
    public bool IsErased => AllErased && ErasedFraction >= 0.999;
}

/// <summary>
/// 位元組序列的 Shannon 熵分析：把一段內容切成等長區塊，逐塊算熵，用來分辨
/// 「壓縮／加密的內容」與「空白／填充／規律的內容」。
/// </summary>
/// <remarks>
/// <para>
/// <b>這個方法回答什麼、不回答什麼：</b>熵只描述「位元組值的分布有多均勻」。
/// 高熵代表這段內容看起來像壓縮或加密過的資料，低熵代表空白、填充或高度規律。
/// <b>它不能判斷內容是好是壞、是否為原廠、是否被篡改</b>——原廠 BIOS 裡本來就有大量
/// 壓縮區段（UEFI 韌體體積的常態），高熵完全正常。判讀留給使用者，搭配 BIOS 區雜湊
/// 與原廠映像比對才有意義。
/// </para>
/// <para>
/// <b>為何不用樣本熵／Kolmogorov 複雜度：</b>那些方法對已抹除區與壓縮區的區分並不更好，
/// 但計算成本高得多、且難以對「這個數字代表什麼」給出可稽核的說明。Shannon 熵逐位元組
/// 計算、定義明確、可手算驗證，對「分辨內容類型」這個用途已經足夠。
/// </para>
/// </remarks>
public static class EntropyMap
{
    /// <summary>預設區塊大小（4 KiB）：與快閃抹除粒度一致，也讓報告的區段數落在可讀範圍。</summary>
    public const int DefaultBlockSize = 4096;

    /// <summary>
    /// 逐區塊熵分析。最後一個不足一整塊的尾段<b>會被丟棄而不是補零</b>——
    /// 補零會把尾段的人為低熵算進來，讓報告出現不存在的規律。
    /// </summary>
    [SpecRef("Shannon, C.E. (1948) A Mathematical Theory of Communication, Bell System Technical Journal 27:379–423（熵的定義 H = −Σ pᵢ log₂ pᵢ；本方法對每個位元組值取 256 個可能符號）；實務門檻 7.5 bits/byte 對應「已壓縮資料」的經驗值（gzip／xz 等一般壓縮輸出每個位元組約 7.5 位元以上），2.0 以下對應高度規律或空白內容——門檻為工程經驗值，非規格常數。")]
    public static IReadOnlyList<EntropyBlock> Analyze(
        ReadOnlySpan<byte> data, int blockSize = DefaultBlockSize)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        int blocks = data.Length / blockSize;
        if (blocks == 0) return [];

        var result = new List<EntropyBlock>(blocks);
        for (int b = 0; b < blocks; b++)
        {
            int start = b * blockSize;
            result.Add(AnalyzeBlock(data.Slice(start, blockSize), start));
        }
        return result;
    }

    /// <summary>單一區塊的熵分析。</summary>
    [SpecRef("Shannon, C.E. (1948) A Mathematical Theory of Communication, Bell System Technical Journal 27:379–423（熵 H = −Σ pᵢ log₂ pᵢ）；本方法對位元組的 256 個可能值取分布，故上界為 8 bits/byte，均勻分布與單一值分別對應 8 與 0——這兩個極值在測試中以手算答案釘住。")]
    public static EntropyBlock AnalyzeBlock(ReadOnlySpan<byte> block, int offset = 0)
    {
        int n = block.Length;
        if (n == 0) return new EntropyBlock(offset, 0, 0, false, false, 0, 0);

        Span<int> counts = stackalloc int[256];
        int zeros = 0, erased = 0;
        for (int i = 0; i < n; i++)
        {
            byte v = block[i];
            counts[v]++;
            if (v == 0x00) zeros++;
            else if (v == 0xFF) erased++;
        }

        int distinct = 0;
        double h = 0;
        for (int v = 0; v < 256; v++)
        {
            int c = counts[v];
            if (c == 0) continue;
            distinct++;
            double p = (double)c / n;
            h -= p * Math.Log2(p);
        }

        bool allSame = distinct == 1;
        // 「整段皆為 0xFF」與「整段皆為 0x00」都代表沒有內容：前者是抹除態，
        // 後者常見於填充；兩者都算 allSame，另外用 ErasedFraction 區分。
        bool allErased = allSame && erased == n;
        return new EntropyBlock(
            offset, n, h, allSame, allErased,
            (double)zeros / n, (double)erased / n);
    }

    /// <summary>
    /// 把逐塊結果壓縮成可讀摘要：高熵、低熵、抹除各佔幾塊與其位移範圍。
    /// </summary>
    /// <remarks>
    /// 目的是讓使用者一眼看出「這顆快閃的內容組成」：一堆抹除區、一片高熵（壓縮／加密）、
    /// 一片低熵（填充或程式碼常數表）。個別區塊的細節留在原清單裡供繪圖與逐塊檢視。
    /// 分類門檻沿用 <see cref="EntropyBlock"/> 上的三個常數，不另立一套——門檻散在兩處
    /// 遲早會漂移，屆時同一份資料在兩張卡片上會有不同說法。
    /// </remarks>
    [SpecRef("Shannon, C.E. (1948) A Mathematical Theory of Communication（熵的定義）；分類門檻（7.5／2.0 bits/byte）為壓縮資料與高度規律內容的工程經驗值，定義於 EntropyBlock 的常數並由本方法沿用——單一來源，避免兩處門檻漂移。")]
    public static EntropySummary Summarize(IReadOnlyList<EntropyBlock> blocks)
    {
        int high = 0, low = 0, erased = 0, mid = 0;
        double entropySum = 0;
        foreach (var b in blocks)
        {
            entropySum += b.ShannonBitsPerByte;
            if (b.IsErased) erased++;
            else if (b.IsHighEntropy) high++;
            else if (b.IsLowEntropy) low++;
            else mid++;
        }
        return new EntropySummary(
            blocks.Count, high, mid, low, erased,
            blocks.Count == 0 ? 0 : entropySum / blocks.Count);
    }
}

/// <summary>逐塊熵分析的摘要統計。</summary>
/// <param name="Blocks">總區塊數。</param>
/// <param name="HighEntropyBlocks">高熵區塊數（像壓縮或加密）。</param>
/// <param name="MediumEntropyBlocks">中間區塊數（一般程式碼與資料）。</param>
/// <param name="LowEntropyBlocks">低熵區塊數（規律或空白）。</param>
/// <param name="ErasedBlocks">抹除區塊數（整段 0xFF）。</param>
/// <param name="MeanEntropyBitsPerByte">全體平均熵。</param>
public readonly record struct EntropySummary(
    int Blocks, int HighEntropyBlocks, int MediumEntropyBlocks, int LowEntropyBlocks,
    int ErasedBlocks, double MeanEntropyBitsPerByte)
{
    /// <summary>抹除區佔比（0–1）。</summary>
    public double ErasedFraction => Blocks == 0 ? 0 : (double)ErasedBlocks / Blocks;
}
