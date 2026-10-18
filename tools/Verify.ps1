param([switch]$SkipShellSmoke, [string]$EvidenceDirectory)
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'This script requires PowerShell 7 or later. Run it with .tools/pwsh/pwsh.exe -NoProfile -ExecutionPolicy Bypass -File tools/Verify.ps1 (Windows PowerShell 5.1 is not supported).'
}
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-BuildEnvironment.ps1')
Set-Location $repositoryRoot
# Completed evidence is immutable (see below), so the DEFAULT must be a directory that does not exist yet.
# A default pointing at a committed directory made the documented no-argument invocation refuse immediately,
# which trains a caller to reach for the overwrite they should never have. The refusal itself is unchanged.
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = 'artifacts/verification/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + 'Z'
}
$dotnet=(Get-Command dotnet -CommandType Application).Source
$output=Join-Path $repositoryRoot $EvidenceDirectory
# Completed evidence is immutable. A verification run writes into a directory of its own rather than
# rewriting an archived one, so an earlier phase's logs cannot be silently replaced by a later run.
if (Test-Path -LiteralPath (Join-Path $output 'restore.log')) {
    throw "Verification evidence already exists in $output. Pass a new -EvidenceDirectory."
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
function Invoke-VerificationDotnet([string]$Log,[string[]]$Arguments) {
    . (Join-Path $PSScriptRoot 'Initialize-BuildEnvironment.ps1')
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $output $Log)
    if ($LASTEXITCODE -ne 0) { throw "$Log failed with exit $LASTEXITCODE." }
}
Invoke-VerificationDotnet 'restore.log' @('restore','Pitwall.slnx','--locked-mode','--disable-build-servers','-m:1')
Invoke-VerificationDotnet 'build.log' @('build','Pitwall.slnx','--no-restore','-c','Release','-warnaserror','--disable-build-servers','-m:1')
Invoke-VerificationDotnet 'foundation.log' @('test','tests/Pitwall.Tests/Pitwall.Tests.csproj','--no-build','--no-restore','-c','Release',
    '--logger','trx;LogFileName=foundation.trx','--results-directory',(Join-Path $output 'foundation'),'--disable-build-servers')
$lint=Join-Path $repositoryRoot 'tools/Pitwall.ResourceLint/bin/Release/net10.0-windows10.0.26100.0/Pitwall.ResourceLint.dll'
Invoke-VerificationDotnet 'resource-lint.log' @($lint,(Join-Path $repositoryRoot 'src'),(Join-Path $output 'resource-lint-results.json'))
$fixture=Join-Path $repositoryRoot '.tmp/lint-negative'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
Set-Content (Join-Path $fixture 'Bad.cs') 'class Bad { void Render() { view.Text = "Hardcoded user-facing text"; } }'
. (Join-Path $PSScriptRoot 'Initialize-BuildEnvironment.ps1')
& $dotnet $lint $fixture (Join-Path $output 'resource-lint-negative.json') *> (Join-Path $output 'resource-lint-negative.log')
$negative=$LASTEXITCODE
@{positiveExitCode=0;negativeExitCode=$negative} | ConvertTo-Json | Set-Content (Join-Path $output 'resource-lint-exits.json')
if ($negative -ne 1) { throw 'Resource lint failed to reject negative fixture.' }
$covenant=Join-Path $repositoryRoot 'tools/Pitwall.CovenantHarness/bin/Release/net10.0-windows10.0.26100.0/Pitwall.CovenantHarness.dll'
$target=Join-Path $repositoryRoot 'src/Pitwall.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/Pitwall.App.exe'
. (Join-Path $PSScriptRoot 'Initialize-BuildEnvironment.ps1')
& $dotnet $covenant $target (Join-Path $output 'covenant-result.json') --sample-seconds 10 *> (Join-Path $output 'covenant.log')
$healthy=$LASTEXITCODE
. (Join-Path $PSScriptRoot 'Initialize-BuildEnvironment.ps1')
& $dotnet $covenant $target (Join-Path $output 'covenant-result-regressed.json') --sample-seconds 10 --fail-commitment idle-working-set *> (Join-Path $output 'covenant-regressed.log')
$regressed=$LASTEXITCODE
@{healthyExitCode=$healthy;regressedExitCode=$regressed} | ConvertTo-Json | Set-Content (Join-Path $output 'covenant-exits.json')
if ($healthy -ne 0) { throw "Covenant harness failed on healthy build with exit $healthy." }
if ($regressed -ne 1) { throw 'Covenant harness failed to reject regressed fixture.' }
if (-not $SkipShellSmoke) { & (Join-Path $PSScriptRoot 'Test-Shell.ps1') -EvidenceDirectory $EvidenceDirectory }
