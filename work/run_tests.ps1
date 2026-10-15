param(
    [string]$Repo = 'C:\Users\Administrator\XinSpect',
    [string]$RunName = 'fullrun',
    [string]$Filter = ''
)
Set-Location $Repo

$logDir = Join-Path $Repo 'work\testlogs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "$RunName.log"
Remove-Item $log -ErrorAction SilentlyContinue

function Say([string]$m) { ((Get-Date).ToString('HH:mm:ss') + '  ' + $m) | Tee-Object -FilePath $log -Append }

Say "=== dotnet test start ($RunName); filter='$Filter' ==="
$cmdArgs = @('test', 'Tests\XinSpect.Tests.csproj', '-c', 'Debug', '--no-build',
             '--logger', 'console;verbosity=normal')
if ($Filter -ne '') { $cmdArgs += @('--filter', $Filter) }

& dotnet @cmdArgs 2>&1 | ForEach-Object {
    $line = $_.ToString()
    if ($line -match '失敗|Failed|error|Error|通過!|Passed!|測試數總計|通過:|失敗:|時間總計|已取消|no test') {
        Say $line
    }
}
Say "=== exit code: $LASTEXITCODE ==="
