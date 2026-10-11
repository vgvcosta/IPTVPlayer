<#
.SYNOPSIS
    Publica o IPTV Player como aplicação self-contained (não precisa do .NET instalado no PC de destino).

.EXAMPLE
    .\publish.ps1
    Corre os testes e publica para .\publish\win-x64

.EXAMPLE
    .\publish.ps1 -Zip -SkipTests
    Publica sem correr os testes e cria também um .zip pronto a distribuir.

.EXAMPLE
    .\publish.ps1 -Msi
    Publica e cria o instalador .msi (WiX, descarregado automaticamente por NuGet).
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$Zip,
    [switch]$Msi,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# (No Windows PowerShell 5.1 o $PSScriptRoot não existe nos valores por omissão do param)
if (-not $Output) { $Output = Join-Path $PSScriptRoot "publish\$Runtime" }

$solution = Join-Path $PSScriptRoot "IPTVPlayer.sln"
$project = Join-Path $PSScriptRoot "src\IPTVPlayer\IPTVPlayer.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK não encontrado. Instala o .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (-not $SkipTests) {
    Write-Host "► A correr os testes…" -ForegroundColor Cyan
    dotnet test $solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Os testes falharam. Publicação cancelada." }
}

if (Test-Path $Output) {
    Write-Host "► A limpar $Output" -ForegroundColor Cyan
    Remove-Item $Output -Recurse -Force
}

Write-Host "► A publicar ($Runtime, self-contained)…" -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -o $Output `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

$exe = Join-Path $Output "IPTVPlayer.exe"
if (-not (Test-Path $exe)) { throw "IPTVPlayer.exe não foi gerado." }
if (-not (Test-Path (Join-Path $Output "libvlc\win-x64\libvlc.dll"))) {
    throw "As bibliotecas do VLC (libvlc\win-x64) não foram copiadas para a publicação."
}

$sizeMb = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("✔ Publicado em {0} ({1:N0} MB)" -f $Output, $sizeMb) -ForegroundColor Green

if ($Zip) {
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $zipPath = Join-Path (Split-Path $Output -Parent) "IPTVPlayer-$version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "► A criar $zipPath…" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("✔ Zip criado: {0} ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

if ($Msi) {
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    $installer = Join-Path $PSScriptRoot "installer\IPTVPlayer.Installer.wixproj"
    $outDir = Split-Path $Output -Parent
    Write-Host "► A criar o instalador MSI…" -ForegroundColor Cyan
    dotnet build $installer -c $Configuration -p:Version=$version -p:PublishDir="$Output" -o $outDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "A criação do MSI falhou." }
    $msiPath = Join-Path $outDir "IPTVPlayer-$version-$Runtime.msi"
    Write-Host ("✔ Instalador criado: {0} ({1:N0} MB)" -f $msiPath, ((Get-Item $msiPath).Length / 1MB)) -ForegroundColor Green
}

Write-Host "Para executar: $exe"