$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.tools/dotnet-home'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repositoryRoot '.tools/nuget-cache'
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.packages'
$env:TEMP = Join-Path $repositoryRoot '.tmp'
$env:TMP = $env:TEMP
$env:MSBUILDDISABLENODEREUSE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_HTTP_CACHE_PATH,$env:TEMP | Out-Null
