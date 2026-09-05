$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = 'C:\Users\Administrator\XinSpect-spd-smbus\bin\Debug\net10.0-windows\win-x64\XinSpect.exe'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 16

$auto = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $auto.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output 'WINDOW NOT FOUND'; Stop-Process -Id $p.Id -Force; exit 1 }

$memory = [string]::Concat([char]0x8A18, [char]0x61B6, [char]0x9AD4)
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $memory)
$item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if ($item) {
    try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
}
Start-Sleep -Seconds 8

$txtCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)
$out = New-Object System.Collections.ArrayList
[void]$out.Add('TEXT ELEMENTS: ' + $all.Count)
foreach ($e in $all) {
    $n = $e.Current.Name
    if ($n) { [void]$out.Add('| ' + $n) }
}
$out | Out-File -FilePath 'C:\Users\Administrator\XinSpect-spd-smbus\uitext.txt' -Encoding UTF8
Write-Output ('WROTE ' + $out.Count + ' LINES')
Stop-Process -Id $p.Id -Force
