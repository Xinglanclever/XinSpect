using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 嵌入式控制器（EC）的界線與存在性（ULTIMATE-1000 階段二 P2-092／P2-093）。
///
/// <para>
/// P2-092 要求陳述 EC 埠不讀寫的界線；P2-093 要求只用 ACPI/PnP 線索間接判斷存在性。
/// </para>
///
/// <para>
/// <b>為什麼不碰：</b>EC 的 I/O 埠（0x62／0x66）走的是有狀態的握手
/// （寫命令 → 等 IBF／OBF → 讀資料），而在現代機器上那個埠通常由 ACPI 的
/// EC 驅動持有。<b>直接讀會與韌體／OS 打架</b>——那不是「讀到錯的值」，
/// 而是「讓別人的值變錯」。<b>所以本專案不做，而那件事要被說出來。</b>
/// </para>
///
/// <para>
/// <b>存在性只使用作業系統可列舉的線索：</b>ACPI ECDT 表簽章與 PnP 的
/// <c>ACPI\PNP0C09</c> 裝置清單。這不代表 EC 暫存器或 EC RAM 可直接讀取。
/// </para>
///
/// <para>
/// <b>界線：沒有 ECDT 或 PnP 列舉結果，不足以宣告沒有 EC。</b>
/// 本服務不讀寫 0x62／0x66 埠，也不讀 EC RAM；只報告 ACPI/PnP 提供的裝置線索。
/// </para>
/// </summary>
public static class EmbeddedControllerFactsService
{
    private const string Category = "平台";
    private const string Source = "ACPI 表簽章清單（既有的 acpi.table.* 逐表列舉）";

    public const string PresentKey = "ec.present";
    public const string BoundaryKey = "ec.boundary";

    public sealed record PnpEcDevice(string Name, string? Status, uint? ConfigManagerErrorCode);
    public sealed record PnpEcState(bool Available, IReadOnlyList<PnpEcDevice> Devices, string? Failure = null);

    /// <summary>ECDT 的 ACPI 簽章。</summary>
    public const string EcdtSignature = "ECDT";

    /// <summary>
    /// <paramref name="acpiSignatures"/> 是既有的逐表列舉結果（簽章清單）。
    /// <c>null</c> 代表那條路讀不到。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<string>? acpiSignatures, PnpEcState? pnpState = null)
    {
        if (pnpState is not null)
            return [CollectWithPnp(at, acpiSignatures, pnpState), Boundary(at)];

        if (acpiSignatures is null)
            return
            [
                new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "**ACPI 表清單讀不到，所以無從間接判斷。**"
                    + "**「表清單讀不到」與「表清單裡沒有 ECDT」是不同的斷言**——"
                    + "前者代表那條路不通，後者才代表平台沒有提供 ECDT"),
                Boundary(at),
            ];

        var hits = acpiSignatures
            .Where(s => string.Equals(s, EcdtSignature, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return
        [
            new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）",
                hits.Count == 0
                    ? "沒有從 ACPI 表看到 ECDT"
                    : $"有 ECDT（{hits.Count} 張）",
                hits.Count == 0 ? "" : "張", Source, FactTrustLevel.Derived, false, at,
                // **沒有 ECDT 時不帶數字。**「沒有」不是一個量測結果，而是一個否定——
                // 傳 0 會讓它看起來像「找到 0 張」。型別不變式會把它收斂成 null，
                // 而這裡直接寫對，因為那個收斂是安全網、不是設計。
                hits.Count == 0 ? null : hits.Count,
                hits.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
                // **Present 的事實不得帶 unavailableReason**（HardwareSnapshotService 的不變式）。
                // 我第一版在兩邊都寫了說明，而整合測試把它抓出來：
                // 「可用的事實不得帶 unavailableReason」。
                //
                // 那些話沒有消失——它們在**這把鍵不 Present 時的理由**裡
                // （「沒有 ECDT」與「沒有 EC」之別），以及上面的 XML 文件註解裡
                // （「不直接讀寫 EC 埠或 RAM」那一句）。**`UnavailableReason` 是
                // 「為什麼讀不到」的欄位，而 Present 的事實沒有「讀不到」可言。**
                hits.Count == 0
                    ? "**「沒有 ECDT」與「沒有 EC」是不同的斷言。**"
                      + "ECDT 是可選的表——ACPI 規格允許以 DSDT 裡的 PNP0C09 裝置描述 EC "
                      + "而不提供 ECDT。**所以這裡只說「沒有從 ACPI 表看到它」。**"
                    : null),
            Boundary(at),
        ];
    }

    public static PnpEcState ReadPnpState()
    {
        try
        {
            var devices = new List<PnpEcDevice>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI%'");
            using var rows = searcher.Get();
            foreach (ManagementObject item in rows)
            {
                using (item)
                {
                    string? id = item["PNPDeviceID"] as string;
                    if (id is null || !id.StartsWith("ACPI\\PNP0C09", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = item["Name"] as string ?? "ACPI EC 裝置";
                    string? status = item["Status"] as string;
                    uint? code = item["ConfigManagerErrorCode"] is { } raw
                        ? Convert.ToUInt32(raw, System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                    devices.Add(new PnpEcDevice(name, status, code));
                }
            }
            return new PnpEcState(true, devices);
        }
        catch (Exception exception)
        {
            return new PnpEcState(false, [], exception.Message);
        }
    }

    private static HardwareFact CollectWithPnp(DateTimeOffset at,
        IReadOnlyList<string>? signatures, PnpEcState pnp)
    {
        bool acpiAvailable = signatures is not null;
        bool pnpAvailable = pnp.Available;
        bool hasEcdt = signatures?.Any(signature =>
            string.Equals(signature, EcdtSignature, StringComparison.OrdinalIgnoreCase)) == true;
        PnpEcDevice[] devices = [.. pnp.Devices];
        string source = Source + "＋Win32_PnPEntity ACPI\\PNP0C09（唯讀列舉）";

        if (hasEcdt || devices.Length > 0)
        {
            string[] evidence =
            [
                hasEcdt ? "ACPI 表含 ECDT" : "",
                devices.Length > 0
                    ? "PnP 已列舉 " + string.Join("、", devices.Select(device =>
                        device.Name + (device.ConfigManagerErrorCode is uint error && error != 0
                            ? $"（裝置管理員錯誤碼 {error}）"
                            : device.Status is { Length: > 0 } status ? $"（{status}）" : "")))
                    : "",
                "只列舉作業系統資訊，不直接讀寫 EC 埠或 EC RAM",
            ];
            return new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）",
                string.Join("；", evidence.Where(value => value.Length > 0)), "", source,
                FactTrustLevel.Derived, false, at, devices.Length > 0 ? devices.Length : null,
                FactAvailability.Present, null);
        }

        if (acpiAvailable && pnpAvailable)
            return new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）",
                "ACPI 未列出 ECDT；Windows PnP 未列出 ACPI\\PNP0C09 EC 裝置。",
                "", source, FactTrustLevel.Derived, false, at, null,
                FactAvailability.NotApplicable,
                "這只代表本次 ACPI/PnP 列舉沒有 EC 線索；不能據此斷言平台沒有 EC。");

        string acpiFailure = acpiAvailable ? "ACPI 表已列舉" : "ACPI 表清單讀取失敗";
        string pnpFailure = pnpAvailable ? "PnP 已列舉" : "PnP 列舉失敗（" + (pnp.Failure ?? "原因未提供") + "）";
        return new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）", "", "", source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            $"存在性來源不完整：{acpiFailure}；{pnpFailure}；未找到可用的 EC 存在性線索。");
    }

    private static HardwareFact Boundary(DateTimeOffset at)
        => new(BoundaryKey, Category, "EC 存取界線", "本專案不讀寫 EC 埠", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
            "**EC 的 I/O 埠（0x62／0x66）走的是有狀態的握手**"
            + "（寫命令 → 等 IBF／OBF → 讀資料），而在現代機器上那個埠"
            + "通常由 ACPI 的 EC 驅動持有。**直接讀會與韌體／OS 打架**——"
            + "那不是「讀到錯的值」，而是**「讓別人的值變錯」**。"
            + "所以本專案不做。"
            + "**而這把鍵存在的理由就是說出那件事**——"
            + "一個沒說出口的界線，會被讀成「還沒做」而不是「刻意不做」。"
            + "P2-092 要求陳述 EC 埠不讀寫的界線；P2-093 只用 ACPI/PnP 裝置線索間接陳述存在性");
}
