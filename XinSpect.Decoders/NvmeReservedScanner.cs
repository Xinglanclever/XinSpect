namespace XinSpect;

/// <summary>
/// 一段「規格要求為零」的位元組區間，以及它在這份 Identify 資料裡實際的內容。
/// </summary>
/// <param name="Name">區域名（照 Linux nvme.h 的欄位名，例如 <c>rsvd352</c>）。</param>
/// <param name="Offset">起始位移。</param>
/// <param name="Length">長度（位元組）。</param>
/// <param name="NonZeroCount">這段裡非零的位元組數。</param>
/// <param name="FirstNonZeroOffset">第一個非零位元組的位移；全零時為 <c>null</c>。</param>
/// <param name="FirstNonZeroValue">第一個非零位元組的值；全零時為 <c>null</c>。</param>
public sealed record ReservedRange(
    string Name,
    int Offset,
    int Length,
    int NonZeroCount,
    int? FirstNonZeroOffset,
    byte? FirstNonZeroValue)
{
    /// <summary>這段是否完全遵守「保留區為零」。</summary>
    public bool IsZero => NonZeroCount == 0;
}

/// <summary>
/// NVMe Identify Controller 保留區的非零掃描（U10 第三圈 NV-0029）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼這件事值得做：</b>NVMe 規格要求 Identify Controller 的保留位元組為零。
/// 非零<b>不會讓硬碟壞掉</b>，但它是一個<b>硬體自述的矛盾</b>：
/// 韌體在「規格說這裡應該沒有東西」的地方放了東西。這種矛盾正是本專案要蒐集的東西——
/// 它比任何「健康分數」都更具體，而且可複算。
/// </para>
/// <para>
/// <b>為什麼只掃 Controller 不掃 Namespace：</b>Namespace 的保留區佈局在不同修訂之間變動較大，
/// 而 Controller 的 <c>rsvd</c> 區段在 Linux <c>nvme.h</c> 裡有明確的欄位名與長度
/// （見 <see cref="Ranges"/> 的每一列）。<b>沒有明確邊界就不掃</b>——
/// 掃錯區間會把「有定義的欄位」報成「非零的保留區」，那是製造假訊號。
/// </para>
/// <para>
/// <b>界線：<c>vs</c>（Vendor Specific，3072–4095）不是保留區。</b>
/// 那 1024 位元組本來就是給廠商放的，非零完全正常——把它算進來會讓每一顆硬碟都「有問題」。
/// 這一條寫在值裡，也寫在測試裡。
/// </para>
/// </remarks>
public static class NvmeReservedScanner
{
    /// <summary>Identify Controller 的固定長度。</summary>
    public const int IdentifyControllerBytes = 4096;

    /// <summary>
    /// 保留區清單。每一列的位移與長度照 <c>nvme.h</c> 的 <c>struct nvme_id_ctrl</c>
    /// 欄位宣告順序累加而來，<b>不是憑印象寫的</b>。
    /// </summary>
    /// <remarks>
    /// 這張表刻意只收「欄位名就叫 rsvd*」的區段。
    /// <c>psd[32]</c>（2048–3071）是電源狀態描述元，<c>vs</c>（3072–4095）是廠商區，
    /// 兩者都不是保留區，不收。
    /// </remarks>
    public static readonly (string Name, int Offset, int Length)[] Ranges =
    [
        ("rsvd100", 100, 11),     // ctratt 之後、cntrltype 之前
        ("rsvd134", 134, 122),    // crdt3 之後、oacs 之前
        ("rsvd352", 352, 160),    // nanagrpid 之後、sqes 之前
        ("rsvd534", 534, 2),      // acwu 之後、sgls 之前
        ("rsvd544", 544, 224),    // mnan 之後、subnqn 之前
        ("rsvd1024", 1024, 768),  // subnqn 之後、ioccsz 之前
        ("rsvd1804", 1804, 2),    // msdbd 之後、dctype 之前
        ("rsvd1807", 1807, 241),  // dctype 之後、psd 之前
    ];

    /// <summary>廠商區（<b>不是</b>保留區）：非零完全正常，掃描時排除。</summary>
    public const int VendorSpecificOffset = 3072;
    public const int VendorSpecificLength = 1024;

    /// <summary>資料不足 4096 位元組時回 <c>null</c>——不拿不足的資料去掃。</summary>
    public static IReadOnlyList<ReservedRange>? Scan(byte[] identify)
    {
        if (identify.Length < IdentifyControllerBytes) return null;
        var list = new List<ReservedRange>(Ranges.Length);
        foreach (var (name, offset, length) in Ranges)
        {
            int count = 0;
            int? firstAt = null;
            byte? firstValue = null;
            for (int i = 0; i < length; i++)
            {
                byte b = identify[offset + i];
                if (b == 0) continue;
                count++;
                if (firstAt is null)
                {
                    firstAt = offset + i;
                    firstValue = b;
                }
            }
            list.Add(new ReservedRange(name, offset, length, count, firstAt, firstValue));
        }
        return list;
    }

    /// <summary>這份 Identify 資料的保留區總非零位元組數。</summary>
    public static int TotalNonZero(IReadOnlyList<ReservedRange> ranges) => ranges.Sum(r => r.NonZeroCount);

    /// <summary>保留區總位元組數（＝掃了多少位元組）。</summary>
    public static int TotalScanned => Ranges.Sum(r => r.Length);
}
