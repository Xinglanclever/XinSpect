# XsRegProbe 建置・簽章・載入手冊（使用者的刻意步驟）

> 深層暫存器計畫 §6 閘門：**編 .sys → 裝信任 → 載入 → 驗 MMIO** 這一段由使用者刻意執行，
> managed 供建置到這裡為止。本機狀態（已實測）：Secure Boot=關、HVCI=關、testsigning=關、WDK km 標頭在
> （`C:\Program Files (x86)\Windows Kits\10\Include\10.0.28000.0\km\`）。
> 窄路簽章策略：自簽碼簽 CA 進 LocalMachine Root + Trusted Publishers，**只放行我們這張**（不開全機 test-signing）。
> 本機 I219-V 自簽網卡驅動已驗證這條路可行。

## 1. 編譯

```powershell
# 開發者命令提示字元（x64）
msbuild C:\Users\Administrator\XinSpect\XsRegProbe\Driver\XsRegProbe.vcxproj /p:Configuration=Release /p:Platform=x64
# 產出：XsRegProbe\Driver\x64\Release\XsRegProbe.sys
```

若工具集/模板 GUID 與本機 WDK 對不上：VS 新開「Kernel Mode Driver, WDM（空專案）」範本，把
`XsRegProbe.c`、`XrpContract.h`、`XsRegProbe.inf` 加入即可，程式碼不需改。

## 2. 自簽 CA 與簽章憑證（一次性）

```powershell
# CA（進信任庫的根）
$ca = New-SelfSignedCertificate -Subject "CN=XinSpect Driver CA" `
  -KeyUsage KeyCertSign, DigitalSignature -KeyLength 4096 `
  -TextExtension @("2.5.29.19={text}ca=TRUE") -CertStoreLocation Cert:\CurrentUser\My

# 端實體（簽 .sys 用，code signing EKU）
$cert = New-SelfSignedCertificate -Subject "CN=XinSpect Driver Signing" `
  -Signer $ca -KeyUsage DigitalSignature -KeyLength 4096 `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") -CertStoreLocation Cert:\CurrentUser\My

# 匯出：CA 公憑證裝信任庫、簽章憑證連私鑰匯出成 PFX 給 signtool
Export-Certificate -Cert $ca -FilePath C:\Users\Administrator\XinSpect\XsRegProbe\XinSpectCA.cer
$password = ConvertTo-SecureString -String "<自訂密碼>" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath C:\Users\Administrator\XinSpect\XsRegProbe\XinSpectSign.pfx -Password $password
```

> 這兩步未來會由 managed「豁免開關（ICertTrustStore）」接手產生與裝卸；手冊先以 PowerShell 對照。

## 3. 裝信任（窄路：只信這張 CA）＋ 簽章

```powershell
certutil -addstore Root    C:\Users\Administrator\XinSpect\XsRegProbe\XinSpectCA.cer
certutil -addstore TrustedPublisher C:\Users\Administrator\XinSpect\XsRegProbe\XinSpectCA.cer

signtool sign /v /fd sha256 /f XinSpectSign.pfx /p "<自訂密碼>" `
  XsRegProbe\Driver\x64\Release\XsRegProbe.sys
```

## 4. 載入（開發機或刻意執行；這是生產機，載入未驗證核心碼請自評時機）

```cmd
sc create XsRegProbe type= kernel start= demand binPath= C:\Users\Administrator\XinSpect\XsRegProbe\Driver\x64\Release\XsRegProbe.sys
sc start XsRegProbe
```

## 5. 驗證（最小心跳檢查：QUERY_INFO + 讀 SPIBAR HSFSTS）

```csharp
using var dev = File.Open(@"\\.\XsRegProbe", FileMode.Open, FileAccess.Read);
// IOCTL_XRP_QUERY_INFO   = CTL_CODE(0x8338, 0x800, METHOD_BUFFERED, FILE_READ_DATA) = 0x83386000
// IOCTL_XRP_READ_MSR_LIST= CTL_CODE(0x8338, 0x801, METHOD_BUFFERED, FILE_READ_DATA) = 0x83386004
// IOCTL_XRP_READ_MMIO    = CTL_CODE(0x8338, 0x802, METHOD_BUFFERED, FILE_READ_DATA) = 0x83386008
// QUERY_INFO 回覆應為 Magic='XRP1'、IoctlVersion=1、FeatureMask=3；
// 再送 IOCTL_XRP_READ_MMIO 讀 0xFED10000+0x04 取 HSFSTS，bit15=1 表示 FLOCKDN（與韌體安全頁對帳）。
```

（此段 managed 端 `DriverMmioReader : IMmioReader` 為下一輪工作；先以最小 win32 小程式或即時 C# 腳本驗。）

**誠實驗收準則**：
- `IOCTL_XRP_QUERY_INFO` 的 MsrExactCount/MsrRangeCount/MmioRangeCount 應與 `XrpContract.h` 表筆數一致
  （35 條逐列 MSR + 1 條 MCA 範圍、2 條 MMIO）。
- 允許清單外的位址（例：0xDEADBEE0）回 `STATUS_ACCESS_DENIED`，**不得**回全 0。
- 讀 `0xFED10000+0x04`（HSFSTS1）：bit15 FLOCKDN 與韌體安全頁/CHIPSEC 對帳；SPIBAR 以 PCI 0:1F.5+0x10 為準。

## 6. 卸載（退出即卸載）

```cmd
sc stop XsRegProbe
sc delete XsRegProbe
```

## 7. 已知範圍與不做的事

- 本版 IOCTL：QUERY_INFO / READ_MSR_LIST / READ_MMIO。**READ_PCICFG 暫不納入**——BUS_INTERFACE_STANDARD
  查詢的載入風險須在開發機先驗；PCI 設定空間讀取現階段仍由 WinRing0 供應（退役順序見移交文件 §5.7）。
- MMIO 允許清單現有兩條：SPIBAR 固定範圍（FED10000+0x00..0x87）與 ECAM（0xE0000000-0xE7FFFFFF，
  須與平台 MCFG 對帳）。加範圍＝改 `XrpContract.h` 的表，派遣碼不動。
- 驅動唯讀、無常駐回呼；`sc delete` 後不留任何痕跡。
