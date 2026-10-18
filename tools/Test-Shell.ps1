param([string]$EvidenceDirectory = 'artifacts/phase0a')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repositoryRoot 'src/Pitwall.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/Pitwall.App.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the Release x64 shell first.' }
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = Split-Path -Parent $executable
$process = [System.Diagnostics.Process]::Start($startInfo)
$timer = [System.Diagnostics.Stopwatch]::StartNew()
while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 20) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
}
$handle = $process.MainWindowHandle.ToInt64()
$title = $process.MainWindowTitle
$shown = $handle -ne 0 -and $title -eq 'Pitwall'
$closeRequested = if ($shown) { $process.CloseMainWindow() } else { $false }
while (-not $process.HasExited -and $timer.Elapsed.TotalSeconds -lt 30) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
}
$exited = $process.HasExited
$exitCode = if ($exited) { $process.ExitCode } else { $null }
$result = @{ executable = $executable; processId = $process.Id; shown = $shown; windowTitle = $title; windowHandle = $handle; closeRequested = $closeRequested; exited = $exited; exitCode = $exitCode }
$evidence = Join-Path $repositoryRoot $EvidenceDirectory
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$resultPath = Join-Path $evidence 'shell-smoke.json'
# Completed evidence is immutable. A new run gets a new directory; it never rewrites an archived result.
if (Test-Path -LiteralPath $resultPath) { $process.Dispose(); throw "Shell smoke evidence already exists at $resultPath. Pass a new -EvidenceDirectory." }
$result | ConvertTo-Json | Set-Content -LiteralPath $resultPath
$process.Dispose()
if (-not $shown -or -not $closeRequested -or -not $exited -or $exitCode -ne 0) { throw 'Native shell smoke failed; see shell-smoke.json.' }
