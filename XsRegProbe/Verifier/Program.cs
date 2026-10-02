using XinSpect;

// XsRegProbe 驗證器：載入驅動後的誠實心跳檢查（BUILD-給使用者.md 第 5 節）。
// 只讀：QUERY_INFO 能力協商對帳 → 讀 SPIBAR HSFSTS 與 ECAM 主機橋標頭。邏輯沿用已單測的 DriverMmioReader。

Console.WriteLine("XsRegProbe 驗證器（唯讀）");
Console.WriteLine($"契約：magic=0x{XsRegProbeContract.Magic:X8} version={XsRegProbeContract.IoctlVersion}，" +
                  $"允許清單 {XsRegProbeContract.MsrAllow.Count}+1 條 MSR／{XsRegProbeContract.MmioAllow.Length} 條 MMIO");

using var reader = new DriverMmioReader();
if (!reader.Available)
{
    Console.WriteLine($"驅動不可用：{reader.UnavailableReason}");
    Console.WriteLine("（載入方式見 XsRegProbe/BUILD-給使用者.md；需管理員執行）");
    return 1;
}
Console.WriteLine("握手成功：能力協商與允許清單筆數對帳通過。");

int failures = 0;

// SPIBAR：契約固定範圍 0xFED10000 起（Intel PCH EDS）。若平台 SPIBAR 非預設位址，這裡讀到的不會對——
// 以韌體安全頁（經 PCI BAR 對帳）為準，此工具只是最小心跳。
Console.WriteLine();
Console.WriteLine("[1] SPIBAR 0xFED10000+0x04 HSFSTS1");
if (reader.ReadBlock(0xFED10000, 0x88) is { } spi)
{
    uint hsfsts = BitConverter.ToUInt32(spi, 0x04);
    Console.WriteLine($"    原始 0x{hsfsts:X8}；FLOCKDN(bit15)={((hsfsts & 0x8000) != 0 ? 1 : 0)}，" +
                      $"WRSDIS(bit11)={((hsfsts & 0x800) != 0 ? 1 : 0)}");
}
else
{
    failures++;
    Console.WriteLine($"    讀不到：{reader.LastFailReason}");
}

// ECAM：主機橋 0:0.0 標頭前 16 bytes（vendor/device/revid）。
Console.WriteLine("[2] ECAM 0xE0000000（bus 0 dev 0 fn 0）標頭");
if (reader.ReadBlock(0xE0000000, 16) is { } head)
{
    uint vendorDevice = BitConverter.ToUInt32(head, 0);
    if (vendorDevice == 0xFFFFFFFF)
        Console.WriteLine("    0:0.0 無回應（全 F）——ECAM 基底可能與本機 MCFG 不符，以韌體安全頁為準");
    else
        Console.WriteLine($"    vendor=0x{vendorDevice & 0xFFFF:X4} device=0x{vendorDevice >> 16:X4} rev=0x{head[8]:X2}");
}
else
{
    failures++;
    Console.WriteLine($"    讀不到：{reader.LastFailReason}");
}

// 允許清單外位址必須被拒——這是驅動安全性的反向驗證。
Console.WriteLine("[3] 清單外位址 0xDEADBEE0（應被拒）");
if (reader.ReadBlock(0xDEADBEE0, 4) is null)
    Console.WriteLine($"    如實被拒：{reader.LastFailReason}");
else
{
    failures++;
    Console.WriteLine("    竟然讀到了——驅動允許清單失效，這是安全問題，請勿使用該 .sys");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "驗證通過（3/3 項如實）" : $"驗證有 {failures} 項失敗");
return failures == 0 ? 0 : 1;
