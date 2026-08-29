using System.Runtime.CompilerServices;
using System.Windows;

// 單元測試專案（Tests\XinSpect.Tests.csproj）需觸及純邏輯的 internal 成員
// （EDID／CPU-Z 報告解析、天梯比對、停止代碼查表）。僅測試組件可見，不影響對外表面。
[assembly: InternalsVisibleTo("XinSpect.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
