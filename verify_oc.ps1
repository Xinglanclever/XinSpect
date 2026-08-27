# Verify the 超頻 (Overclock) page loads without the read-only-binding crash.
# ASCII-only; matches nav item by INDEX (10), never by Chinese Name.
$ErrorActionPreference = 'Continue'
$exe          = 'C:\Users\Administrator\XinSpect\publish\XinSpect.exe'
$settingsDir  = Join-Path $env:APPDATA 'XinSpect\Overclock'
$settings     = Join-Path $settingsDir 'settings.json'
$backup       = Join-Path $settingsDir 'settings.json.verifybak'

# --- pre-set DontShowRisk so nav goes straight to the crash path (no dialog) ---
if (!(Test-Path $settingsDir)) { New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null }
$hadSettings = Test-Path $settings
if ($hadSettings) { Copy-Item $settings $backup -Force }
'{"DontShowRisk":true,"BootRestore":true,"WatchdogEnabled":false}' | Set-Content -Path $settings -Encoding UTF8

$startTime = Get-Date
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

# --- locate main window for this PID ---
$root = $AE::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
$win = $null
for ($i = 0; $i -lt 24 -and -not $win; $i++) {
    $win = $root.FindFirst($TS::Children, $pidCond)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output 'RESULT=NO_WINDOW'; if(-not $p.HasExited){$p.Kill()}; return }

# --- find the Nav ListBox (x:Name=Nav -> AutomationId) and select index 10 ---
$navCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'Nav')
$nav = $win.FindFirst($TS::Descendants, $navCond)
if (-not $nav) { Write-Output 'RESULT=NO_NAV'; if(-not $p.HasExited){$p.Kill()}; return }
$liCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $nav.FindAll($TS::Children, $liCond)
Write-Output ("NAV_ITEMS=" + $items.Count)
try {
    $target = $items[10]
    $si = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $si.Select()
    Write-Output 'SELECTED_INDEX_10'
} catch {
    Write-Output ('SELECT_ERR=' + $_.Exception.Message)
}
Start-Sleep -Seconds 4

$alive = -not $p.HasExited
Write-Output ("ALIVE=" + $alive)

# --- screenshot for a visual check ---
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $shotDir = 'C:\Users\Administrator\XinSpect\shots'
    if (!(Test-Path $shotDir)) { New-Item -ItemType Directory -Force -Path $shotDir | Out-Null }
    $bmp.Save((Join-Path $shotDir 'oc_fixed.png'))
    Write-Output 'SHOT=shots\oc_fixed.png'
} catch { Write-Output ('SHOT_ERR=' + $_.Exception.Message) }

# --- any new .NET crash for XinSpect since launch? ---
$crash = Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'; StartTime=$startTime} -ErrorAction SilentlyContinue |
         Where-Object { $_.Message -match 'XinSpect' }
if ($crash) { Write-Output 'RESULT=CRASH'; Write-Output ($crash[0].Message.Substring(0, [Math]::Min(500,$crash[0].Message.Length))) }
else        { Write-Output 'RESULT=NO_CRASH' }

# --- cleanup: kill app, restore original settings ---
if (-not $p.HasExited) { $p.Kill() }
if ($hadSettings) { Copy-Item $backup $settings -Force; Remove-Item $backup -Force }
else { Remove-Item $settings -Force -ErrorAction SilentlyContinue }
Write-Output 'DONE'
