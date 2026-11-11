# 審計指控核查（2026-11-12，v3.26 工作樹）

本文件是程式碼與本機 WDK 建置核查，不是獨立第三方審計，也不是實機驅動認證。

| 指控 | 核查結果 |
|---|---|
| Stryker 82% 只覆蓋純解碼器，不是全應用 | 成立。`stryker-config.json` 只指向 `XinSpect.Decoders.csproj`；82% 是歷史純解碼器突變分數，不涵蓋 GUI、CLI、服務與核心驅動，也未在本次重測。已在三語徽章旁明示。 |
| 1,178 鍵混合治理與其他項目，不能代表硬體讀取深度 | 成立。`FactKeyCatalog` 是跨硬體、OS、安全、診斷、來源與治理的目錄，既不是成功讀值數，也不是準確率。但 `LoadZeroTrust`、`LoadDataSecurity` 實際有登錄檔、憑證店等採集來源，不能全稱為 UI 自報。已修正數字旁的口徑。 |
| 四項核心列項中，生產接入主要是 WinRing0 與自研 XsRegProbe | 成立。PawnIO 的 `ModulePins` 空且 Native 未綁定；BlueSquadron Bridge 是已建置的 usermode 程式，核心驅動不在發版資產矩陣。 |
| BlueSquadron 從未編譯 | 修改前找不到 `.sys`，部分檔頭明示未編譯，不能由此證明所有歷史。此次實際建置四個 `.c` 成功編譯後連結失敗：核心連結庫誤寫 `bcrypt.lib`。同時發現 `IoCreateDeviceSecure` 把 UNICODE_STRING 當作 GUID 傳入、`_snwprintf` 未宣告。改用 `cng.lib`、專用 GUID 與 `RtlStringCchPrintfW` 後，本機編譯連結成功；仍未封裝、簽章、載入或做執行驗證，沒有因此增加生產能力。 |
| WinRing0 是借用的通用核心存取介面，會話可能長駐 | 成立。`WinRing0Bridge` 載入 LHM 0.9.4 內嵌驅動，提供 MSR/PCI/I/O/MMIO；`WinRing0KernelBackend` 持有橋接到路由鏈退役。`WriteGate` 是本應用防線，不能約束其他已取得驅動裝置存取權的程序。本機 LHM 0.9.4 反編譯顯示 Close 會依引用計數關閉／嘗試刪除服務；不能寫成固定到重開機才卸載。 |
| 「優秀」來自作者與 AI 自審，並非獨立第三方認證 | 成立。報告是作者與 AI 代理輔助檢視。已移除首頁的「優秀」「可窮舉證明」用語；歷史報告保留原文，不能作為認證。誤報估計不能推算漏報率。 |

## XsRegProbe MMIO 真缺陷與指控中的數值錯誤

原呼叫 `MmMapIoSpaceEx(pa, length, MmNonCached)` 確實傳錯旗標類型。
本機 WDK `10.0.28000.0/km/wdm.h` 定義 `MmNonCached = FALSE`（0），
不是 4；4 是 `MmNonCachedUnordered`，而 `PAGE_READWRITE = 0x04`。
所以「原碼實際映射成可寫」的推論沒有成立。

`MmMapIoSpaceEx` 的第三參數是頁面保護旗標，原值 0 不能支持唯讀保護宣稱。
修正為 `PAGE_READONLY | PAGE_NOCACHE`，增加對實際呼叫參數的回歸測試並重建 `.sys`。
唯讀旗標只保護這個虛擬映射，白名單限制可讀位址；兩者不等同整台硬體的全域唯讀保障。
目前未載入新驅動做實機驗證，因此不斷言原值 0 在執行時一定如何失敗，也不斷言此修正已消除所有漏洞。

## 未經驗證的比較

本次未獲 HWiNFO/AIDA64 的完整核心原始碼、認證清單或同平台對照數據，
不對「覆蓋不到其零頭」給出比例結論。現有證據足以確認 XinSpect 自研核心覆蓋有限，
不能用後端列項數或解碼器分數宣稱已達同等覆蓋或精度。
