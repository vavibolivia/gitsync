<#
.SYNOPSIS
  Windows에서 원격 Linux 서버로 GitSync를 배포합니다 (소스 복사 → 서버에서 podman 빌드/실행).

.EXAMPLE
  .\deploy\deploy.ps1 -Server user@10.0.0.5 -Name gitsync -Tag 1.0.0 -Port 8080
#>
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$Name = "gitsync",
    [string]$Tag = "latest",
    [int]$Port = 8080,
    [string]$DataDir = "",
    [string]$AccessKey = "",
    [string]$RemoteDir = ""
)
$ErrorActionPreference = "Stop"
if ($Name -notmatch '^[a-z0-9][a-z0-9_.-]*$') { throw "이름은 소문자/숫자/-/_/. 만 사용할 수 있습니다: $Name" }
if (-not $RemoteDir) { $RemoteDir = "~/$Name-src" }
$root = Split-Path -Parent $PSScriptRoot
$archive = Join-Path $env:TEMP "$Name-src.tgz"

Write-Host "▶ 소스 압축" -ForegroundColor Magenta
tar -C $root --exclude=./.git --exclude=*/bin --exclude=*/obj --exclude=./publish -czf $archive .
if ($LASTEXITCODE -ne 0) { throw "압축 실패" }

Write-Host "▶ $Server 로 업로드" -ForegroundColor Magenta
ssh $Server "mkdir -p $RemoteDir"
scp $archive "${Server}:$RemoteDir/src.tgz"
Remove-Item $archive

$deployArgs = "-n $Name -t $Tag -p $Port"
if ($DataDir) { $deployArgs += " -d $DataDir" }
if ($AccessKey) { $deployArgs += " -k $AccessKey" }

Write-Host "▶ 원격 빌드 및 실행" -ForegroundColor Magenta
ssh -t $Server "cd $RemoteDir && tar -xzf src.tgz && rm src.tgz && bash deploy/deploy.sh $deployArgs"
