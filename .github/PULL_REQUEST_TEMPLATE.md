<!--
  PR 規矩（與 CONTRIBUTING.md 同步）：
  不吹噓、不虛帳。改了什麼就寫什麼，驗過什麼就標什麼。
  「宣稱」與「代碼」脫節是本專案審計裡最重的罪——你的 PR 描述就是第一份審計對象。
-->

## 這個 PR 做了什麼

<!-- 一句話總結；逐項列出實際改動，不寫「改進了大量模組」這種話。 -->

## 動機與依據

<!-- 對應哪個 issue／審計條目（P0-x／patch-xx）／使用者回饋。沒有依據就寫「無」。 -->

## 驗證

- [ ] `dotnet build XinSpect.csproj` 綠（Bridge 內嵌鏈包含在內）
- [ ] 本機實測：8,048 條 綠（`dotnet test`，數字以 `Tests/TestSuiteBaseline.cs` 為準；加了測試就要改基線）
- [ ] 若動了 Docs／README／Changelog：`ChangelogTests`＋`ReadmeStatsTests`＋`CommunityDocsTests` 綠
- [ ] 若動了 Services／UI：在真機上跑過受影響的頁面，沒有只靠測試綠
- [ ] 若動了驅動（.c／.h）：說明 testsigning 實機驗證狀態（沒驗就如實寫「未驗」）

## 誠實申報

<!-- 本 PR 沒做／沒驗的部分，直說。寧可缺一段，不要補一段想像。 -->

-
