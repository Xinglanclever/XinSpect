# 取得協助

本專案由個人維護，沒有客服窗口或支援時效承諾。下面這條路徑能讓問題最快被處理，
也避免把時間花在已經寫在文件裡的答案。

## 1. 先查文件

| 你想知道的 | 去哪裡 |
|---|---|
| 這個數字是什麼、怎麼判讀 | [README](README.md) 第六至九章、附錄 F 詞彙表、附錄 N 數字與單位 |
| 為什麼顯示「不支援／權限不足／讀取失敗」 | README 第二十三章、附錄 D 疑難排解 |
| 某個功能刻意不做什麼 | README 第十五章、第十九章、第二十章 |
| 怎麼用 CLI／匯出／查詢語言 | README 第十一章、`docs/spec/` |
| 怎麼從源碼建置 | README 第十六章、[CONTRIBUTING.md](CONTRIBUTING.md) |

## 2. 再搜 Discussions 與 Issues

- [Discussions](https://github.com/Xinglanclever/XinSpect/discussions)：使用問題、判讀討論、
  硬體覆蓋經驗。適合「這個值正常嗎」「這顆硬體讀不到有人遇過嗎」。
- [Issues](https://github.com/Xinglanclever/XinSpect/issues?q=is%3Aissue)：已確認的缺陷與功能建議。
  開新 Issue 前請先搜尋，重複回報會被合併。

> Discussions 需要維護者在 repository settings 開啟；若連結尚未可用，請先開 Issue。

## 3. 選對回報類型

| 情況 | 通道 |
|---|---|
| 程式當掉、畫面空白、行為與文件不符 | [錯誤回報](https://github.com/Xinglanclever/XinSpect/issues/new?template=bug_report.yml) |
| 某顆硬體讀不到、解碼錯、三態判定可疑 | [硬體判讀回報](https://github.com/Xinglanclever/XinSpect/issues/new?template=hardware_report.yml) |
| 想要新能力 | [功能建議](https://github.com/Xinglanclever/XinSpect/issues/new?template=feature_request.yml) |
| 安全性問題 | [私密安全公告](https://github.com/Xinglanclever/XinSpect/security/advisories/new)（**不要公開**） |
| 行為守則事件 | 見 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) 的通報管道 |

## 4. 回報時請附上

- XinSpect 版本與 Windows 版本；
- 是否以系統管理員執行、深層存取是否啟用；
- 重現步驟；
- 事實鍵、六態與來源（若問題涉及讀值）；
- 已遮蔽的日誌或 `--json` 節錄。

**不要附上**序號、UUID、MAC、資產標籤、使用者名稱或完整路徑。

## 5. 回應預期

- 維護者會盡力回覆，但不保證時間；沒有回應不代表問題不存在。
- 安全性問題優先於功能與一般錯誤。
- 功能建議不保證實作；取捨標準是來源可驗證性、維護成本與是否會把判讀權從使用者手上拿走。
- 贊助不影響排序。詳見 [README 的社群、貢獻與贊助一節](README.md#二十六社群貢獻與贊助)。
