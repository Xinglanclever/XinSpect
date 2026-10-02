namespace XinSpect;

/// <summary>
/// I/O 埠唯讀事實（V7 WP1／A38）：POST 代碼（I/O 0x80）。port 不存在或無鎖存時如實三態
/// （0xFF／0x00 都不是可解讀的 POST 代碼，不以它們冒充），語意標示依主機板而異。
/// </summary>
public static class IoPortFactsService
{
    private const string Category = "開機階段";
    private const uint PostCodePort = 0x80;

    public static IReadOnlyList<HardwareFact> Collect(IIoPortAccess io, DateTimeOffset at) => [PostCodeFact(io, at)];

    private static HardwareFact PostCodeFact(IIoPortAccess io, DateTimeOffset at)
    {
        const string key = "boot.post_code", name = "POST 代碼（I/O 0x80）", source = "I/O 埠 0x80 唯讀";
        if (!io.Available)
            return Unav(key, name, source, at, FactAvailability.InsufficientPrivilege,
                io.UnavailableReason ?? "缺 I/O 埠存取");
        byte? v = io.InByte(PostCodePort);
        if (v is null)
            return Unav(key, name, source, at, FactAvailability.ReadError, "埠讀取失敗");
        byte code = v.Value;
        // 誠實界線（V7 §9）：0xFF＝無裝置回應；0x00＝未鎖存或已清——兩者都不解碼成 POST 步驟。
        return code is 0xFF or 0x00
            ? Unav(key, name, source, at, FactAvailability.NotSupported,
                $"埠回 0x{code:X2}——{(code == 0xFF ? "無裝置回應（許多主機板未實作 POST 代碼鎖存）" : "未鎖存或已清除，無法區分")}")
            : new HardwareFact(key, Category, name, $"0x{code:X2}（開機後的殘留鎖存值，語意依主機板而異）", "",
                source, FactTrustLevel.Measured, false, at, code);
    }

    private static HardwareFact Unav(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
