namespace XinSpect;

/// <summary>
/// 內建瀏覽器的自訂起始頁（硬體導航）。以 NavigateToString 載入，完全離線、不依賴外部檔案；
/// 搜尋框以 GET 表單導向 Bing，快捷連結為一般 &lt;a&gt; 導覽。全繁體中文。
/// </summary>
internal static class BrowserHome
{
    public const string Html = """
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>曦覽 ・ 硬體導航</title>
<style>
  :root { --bg:#1e2023; --card:#26282c; --ink:#eef1f5; --sub:#9aa3ad; --line:#3a3d42; --accent:#4c8dff; }
  * { box-sizing:border-box; }
  html,body { margin:0; height:100%; }
  body { background:var(--bg); color:var(--ink);
         font-family:"Microsoft JhengHei UI","Microsoft JhengHei",-apple-system,"Segoe UI",sans-serif;
         display:flex; flex-direction:column; align-items:center; }
  .wrap { width:100%; max-width:920px; padding:48px 24px 40px; }
  .brand { font-size:30px; font-weight:800; letter-spacing:1px; }
  .brand .dim { color:var(--sub); font-weight:600; font-size:20px; }
  .tag { color:var(--sub); margin:6px 0 26px; font-size:14px; }
  form { display:flex; gap:10px; margin-bottom:34px; }
  input[type=search] { flex:1; background:var(--card); border:1px solid var(--line); border-radius:10px;
         color:var(--ink); font-size:15px; padding:13px 16px; outline:none; }
  input[type=search]:focus { border-color:var(--accent); }
  button { background:var(--accent); border:0; border-radius:10px; color:#fff; font-size:15px;
         padding:0 22px; cursor:pointer; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(210px,1fr)); gap:16px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px 18px; }
  .card h3 { margin:0 0 10px; font-size:14px; color:var(--accent); font-weight:700; }
  .card a { display:block; color:var(--ink); text-decoration:none; font-size:14px; padding:6px 0;
         border-bottom:1px solid transparent; }
  .card a:hover { color:var(--accent); }
  .foot { color:var(--sub); font-size:12px; margin-top:30px; text-align:center; }
</style>
</head>
<body>
  <div class="wrap">
    <div class="brand">曦覽 <span class="dim">・ 硬體導航</span></div>
    <div class="tag">內建瀏覽器起始頁 ・ 常用硬體檢測、跑分與驅動資源一站直達</div>
    <form action="https://www.bing.com/search" method="get">
      <input type="search" name="q" placeholder="以 Bing 搜尋，或於上方網址列輸入網址…" autofocus>
      <button type="submit">搜尋</button>
    </form>
    <div class="grid">
      <div class="card">
        <h3>硬體檢測</h3>
        <a href="https://www.cpuid.com/softwares/cpu-z.html">CPU-Z</a>
        <a href="https://www.techpowerup.com/gpuz/">GPU-Z</a>
        <a href="https://www.hwinfo.com/download/">HWiNFO</a>
        <a href="https://crystalmark.info/en/software/crystaldiskinfo/">CrystalDiskInfo</a>
        <a href="https://www.aida64.com/downloads">AIDA64</a>
      </div>
      <div class="card">
        <h3>跑分 ・ 天梯</h3>
        <a href="https://www.topcpu.net/">TopCPU 天梯榜</a>
        <a href="https://www.maxon.net/en/cinebench">Cinebench</a>
        <a href="https://benchmarks.ul.com/3dmark">3DMark</a>
        <a href="https://www.geekbench.com/">Geekbench</a>
      </div>
      <div class="card">
        <h3>驅動下載</h3>
        <a href="https://www.intel.com/content/www/us/en/download-center/home.html">Intel 驅動</a>
        <a href="https://www.amd.com/zh-hant/support">AMD 驅動</a>
        <a href="https://www.nvidia.com/zh-tw/drivers/">NVIDIA 驅動</a>
      </div>
      <div class="card">
        <h3>系統 ・ 工具</h3>
        <a href="https://github.com/luolangaga/tubatools">圖吧工具箱</a>
        <a href="https://www.ventoy.net/">Ventoy</a>
        <a href="https://rufus.ie/">Rufus</a>
        <a href="https://apps.microsoft.com/detail/9pm860492szd">Microsoft PC Manager</a>
      </div>
    </div>
    <div class="foot">曦覽 XinSpect ・ 連結於本分頁內開啟</div>
  </div>
</body>
</html>
""";
}
