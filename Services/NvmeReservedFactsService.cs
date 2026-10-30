namespace XinSpect;

/// <summary>
/// 單顆 NVMe 控制器的保留區掃描結果。
/// </summary>
/// <param name="Label">磁碟標籤（例如 <c>磁碟 0</c>）。</param>
/// <param name="Model">型號（讀得到才有）。</param>
/// <param name="Ranges">逐段結果；資料讀不到時為 <c>null</c>。</param>
/// <param name="Why">讀不到時的原因。</param>
public sealed record NvmeReservedSample(
    string Label, string? Model, IReadOnlyList<ReservedRange>? Ranges, string? Why);

/// <summary>
/// NVMe Identify 保留區非零掃描的事實（U10 第三圈 NV-0029）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>「這些硬碟在規格說『這裡應該沒有東西』的地方，有沒有放東西」。
/// </para>
/// <para>
/// <b>為什麼這比健康分數有用：</b>它是<b>硬體自述的矛盾</b>——可複算、可指出位移、
/// 不依賴任何廠商私有定義。非零不代表硬碟壞了，代表<b>韌體與規格不一致</b>，
/// 而那種不一致正是本專案要蒐集的東西。
/// </para>
/// <para>
/// <b>刻意不做的事：</b>不把 <c>vs</c>（廠商區 3072–4095）算成保留區——
/// 那 1024 位元組本來就是給廠商放的，非零完全正常。把它算進來，
/// 每一顆硬碟都會「有問題」，那這把鍵就廢了。
/// </para>
/// </remarks>
public static class NvmeReservedFactsService
{
    public const string Category = "儲存";
    public const string SummaryKey = "nvme.rsvd.summary";
    public const string BoundaryKey = "nvme.rsvd.boundary";
    public const string DiskPrefix = "nvme.rsvd.disk.";

    private const string Source = "NVMe Identify Controller（4096 位元組，唯讀）＋Linux nvme.h 的 rsvd 欄位邊界";

    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<NvmeReservedSample> samples, DateTimeOffset at)
    {
        if (samples.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "NVMe 保留區非零掃描", "", "段", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "這台機器沒有可讀的 NVMe Identify Controller——「沒有 NVMe」與「讀不到 Identify」是兩件事；"
                    + "前者這一輪沒有東西可掃，後者會在逐顆的事實裡說明"),
                BoundaryFact(at),
            ];

        var facts = new List<HardwareFact>();
        int scanned = 0, dirty = 0, unreadable = 0, totalNonZero = 0;
        var dirtyFaces = new List<string>();

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            string key = DiskPrefix + (i + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

            if (s.Ranges is null)
            {
                unreadable++;
                facts.Add(new HardwareFact(key, Category, $"{s.Label} 保留區掃描", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    (s.Why ?? "Identify Controller 讀不到") + "——讀不到就不掃，不假裝掃過"));
                continue;
            }

            scanned++;
            int nonZero = NvmeReservedScanner.TotalNonZero(s.Ranges);
            totalNonZero += nonZero;
            if (nonZero > 0)
            {
                dirty++;
                dirtyFaces.Add($"{s.Label}（{nonZero} 位元組）");
            }
            facts.Add(DiskFact(key, s, nonZero, at));
        }

        facts.Insert(0, SummaryFact(samples.Count, scanned, dirty, unreadable, totalNonZero, dirtyFaces, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    private static HardwareFact SummaryFact(int total, int scanned, int dirty, int unreadable, int nonZero,
        List<string> faces, DateTimeOffset at)
    {
        string value = $"{total} 顆 NVMe・掃了 {scanned} 顆（{NvmeReservedScanner.TotalScanned} 位元組保留區）"
            + $"・其中 {dirty} 顆有非零保留位元組（合計 {nonZero} 位元組）"
            + (unreadable > 0 ? $"・{unreadable} 顆的 Identify 讀不到（不計入）" : "")
            + (dirty > 0 ? $"・{string.Join("、", faces)}" : "")
            + "・**非零不代表硬碟壞了**：它代表韌體在規格說「這裡應該沒有東西」的地方放了東西——"
            + "那是硬體自述的矛盾，不是故障判定";

        return new HardwareFact(SummaryKey, Category, "NVMe 保留區非零掃描", value, "段", Source,
            FactTrustLevel.Measured, false, at, nonZero);
    }

    private static HardwareFact DiskFact(string key, NvmeReservedSample s, int nonZero, DateTimeOffset at)
    {
        string modelFace = string.IsNullOrWhiteSpace(s.Model) ? "型號讀不到" : $"型號 {s.Model}";
        string dirtyRanges = string.Join("、", s.Ranges!.Where(r => !r.IsZero)
            .Select(r => $"{r.Name}@{r.Offset}（{r.Length} 位元組中 {r.NonZeroCount} 個非零，"
                + $"首個在 0x{r.FirstNonZeroOffset:X2}＝0x{r.FirstNonZeroValue:X2}）"));

        string value = nonZero == 0
            ? $"{s.Label}（{modelFace}）：**{NvmeReservedScanner.TotalScanned} 位元組保留區全為零**——"
              + "規格要求的部分它遵守了・界線：這只證明「保留區為零」，不證明韌體沒有別的怪癖"
            : $"{s.Label}（{modelFace}）：保留區有 {nonZero} 個非零位元組——{dirtyRanges}"
              + $"・這是**硬體自述的矛盾**：規格說這些位移應該為零"
              + "・不代表硬碟壞了；也不代表廠商在藏東西——最常見的原因是韌體把某些內部欄位沿用在那裡"
              + "・本鍵只指出位移與值，**不猜那些位元組是什麼**";

        return new HardwareFact(key, Category, $"{s.Label} 保留區掃描", value, "位元組", Source,
            FactTrustLevel.Measured, false, at, nonZero);
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "NVMe 保留區掃描的界線宣告",
            "① 只掃 **Identify Controller**（4096 位元組），不掃 Namespace 的 Identify——"
            + "後者的保留區佈局在修訂之間變動較大，沒有明確邊界就不掃；"
            + "② 只收 Linux <c>nvme.h</c> 裡欄位名就叫 <c>rsvd*</c> 的區段"
            + $"（{string.Join("、", NvmeReservedScanner.Ranges.Select(r => r.Name))}）；"
            + "③ **<c>vs</c>（3072–4095，廠商區）不是保留區**——那 1024 位元組本來就是給廠商放的，"
            + "非零完全正常，掃進來會讓每一顆硬碟都「有問題」；"
            + "④ 非零**不代表故障**：它是規格與韌體的不一致，最常見原因是韌體沿用內部欄位；"
            + "⑤ **不猜那些位元組是什麼**——沒有公開定義就不解讀，只給位移與值；"
            + "⑥ 讀不到 Identify 時標 <c>ReadError</c> 而不是「全零」——"
            + "「掃過且乾淨」與「沒掃到」是兩種不同的斷言",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
