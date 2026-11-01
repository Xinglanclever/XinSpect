# XsRegProbe 建置・簽章・載入手冊

> **一般使用者不必看這份。** 部署器（XinSpectDeploy）會把 `XsRegProbe.sys` 放進
> `C:\ProgramData\XinSpect\Driver\`；程式裡的「深層核心存取 → 開啟」會用本機產生的 CA
> 簽這支 `.sys` 再裝服務。這份手冊是**開發者**改驅動時用的（或部署器沒把檔案放進去時的備援）。

## 手動建置・簽章・載入（開發者）

> 深層暫存器計畫 §6 閘門：**編 .sys → 裝信任 → 載入 → 驗 MMIO** 這一段由使用者刻意執行，
> managed 供建置到這裡為止。本機狀態（已實測）：Secure Boot=關、HVCI=關、testsigning=關、WDK km 標頭在
> （`C:\Program Files (x86)\Windows Kits\10\Include\10.0.28000.0\km\`）。
> **更正（2026-11-01 實測）**：先前寫「自簽 CA 進 Root＋TrustedPublisher 就能載入」是錯的。
> 那條路只對**使用者模式**的 Authenticode 有效；Windows 10/11 x64 的**核心模式程式碼完整性**
> 只接受鏈到 Microsoft 根的簽章。用本專案自簽的葉憑證簽 `.sys`，`signtool verify /pa` 會過，
> 但 `sc start` 回 **577 (ERROR_INVALID_IMAGE_HASH)**，事件記錄是 CodeIntegrity **3004**
> 「unable to verify the image integrity」。要讓核心收下這支驅動只有兩條路：
> **（a）開 testsigning 並重開機**（全域、浮水印、削弱防護），或
> **（b）送 Microsoft attestation／WHQL 簽章**（EV 憑證＋Partner Center）。
> 在那之前 XsRegProbe 是**開發機專用**；WinRing0 只提供 MSR 與 PCI 設定空間（實體記憶體讀取實測失敗），MMIO 沒有替代來源。

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

**建議：先複製 .sys 到程式認得的部署目錄，再從程式內「韌體安全 → 深層核心存取 → 開啟」啟用**
（程式只認 `%ProgramData%\XinSpect\Driver\XsRegProbe.sys`；從程式內開啟會一併裝 CA 信任、建服務、回報握手狀態）：

```powershell
New-Item -ItemType Directory -Force "$env:ProgramData\XinSpect\Driver" | Out-Null
Copy-Item XsRegProbe\Driver\x64\Release\XsRegProbe.sys "$env:ProgramData\XinSpect\Driver\"
```

也可手動建服務（注意：此法服務能跑、握手能過，但程式狀態列會顯示「.sys 未部署」，因為它只檢查上面的部署目錄）：

```cmd
sc create XsRegProbe type= kernel start= demand binPath= C:\Users\Administrator\XinSpect\XsRegProbe\Driver\x64\Release\XsRegProbe.sys
sc start XsRegProbe
```

## 5. 驗證（最小心跳檢查：QUERY_INFO + 讀 SPIBAR HSFSTS）

隨 repo 附驗證器（引用主程式專案，沿用已單測的 `DriverMmioReader` 邏輯，不重複實作契約）：

```powershell
cd C:\Users\Administrator\XinSpect
dotnet run --project XsRegProbe/Verifier/DriverVerifier.csproj -c Release
```

三項檢查：
1. `IOCTL_XRP_QUERY_INFO` 能力協商＋允許清單筆數對帳（35+1 條 MSR、2 條 MMIO）。
2. 讀 `0xFED10000+0x04`（HSFSTS1）：bit15 FLOCKDN、bit11 WRSDIS。若平台 SPIBAR 非預設位址，以韌體安全頁（經 PCI BAR 對帳）的值為準。
3. 讀清單外位址 `0xDEADBEE0`——**必須如實被拒**；若竟讀到，代表 .sys 允許清單失效，請勿使用。

全部通過印「驗證通過（3/3 項如實）」；任何一項讀不到都會說原因，不以 0 頂替。

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
