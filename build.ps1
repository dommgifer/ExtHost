# 編譯 ExtHost 成單一執行檔，輸出到 dist\
# 可指定版本號：.\build.ps1 -Version 0.2.0（CI 會帶入標籤或編譯編號）
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"

$publishArgs = @((Join-Path $root "src\ExtHost\ExtHost.csproj"), "-c", "Release", "-o", $dist)
if ($Version) { $publishArgs += "-p:Version=$Version" }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "編譯失敗" }

Copy-Item (Join-Path $root "README.md") $dist -Force
$samples = Join-Path $dist "samples"
if (Test-Path $samples) { Remove-Item $samples -Recurse -Force }
Copy-Item (Join-Path $root "samples") $samples -Recurse

Write-Host "完成：$dist\ExtHost.exe" -ForegroundColor Green
