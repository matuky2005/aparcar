# Construir un EXE instalador de pruebas x64 a partir del workspace generado.
# No borrar Aparcar_VisualStudio existente: el script de preparación genera allí la solución.
# La base de clientes se encuentra en %USERPROFILE%\Aparcar\Datos, fuera del instalador.
[CmdletBinding()]
param(
    [string]$Workspace = 'Aparcar_VisualStudio',
    [string]$OutputDir = 'dist',
    [string]$InnoCompiler = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') { throw 'Este instalador solo se genera en Windows.' }
$repo = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Workspace)) { $Workspace = Join-Path $repo $Workspace }
if (-not [IO.Path]::IsPathRooted($OutputDir)) { $OutputDir = Join-Path $repo $OutputDir }
$project = Join-Path $Workspace 'src\Aparcar.Desktop\Aparcar.Desktop.csproj'
$publish = Join-Path $Workspace 'publish\win-x64'
$iss = Join-Path $repo 'installer\Aparcar.iss'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Falta el proyecto generado: $project. Ejecutá primero scripts/Preparar-Aparcar.ps1."
}
if (-not (Test-Path -LiteralPath $iss)) { throw "Falta el script Inno Setup: $iss." }
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish,$OutputDir -Force | Out-Null

Write-Host 'Publicando Aparcar WinUI 3 x64 auto-contenido, SIN MSIX...' -ForegroundColor Cyan
$arguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:WindowsPackageType=None',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:AppxPackageSigningEnabled=false',
    '-p:GenerateAppxPackageOnBuild=false',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-o', $publish
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE): no se genera instalador." }
$exe = Join-Path $publish 'Aparcar.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No existe $exe. Publicación inválida." }
$size = (Get-ChildItem -LiteralPath $publish -File -Recurse | Measure-Object -Property Length -Sum).Sum
if ($size -lt 10000000) { throw "Publicación sospechosamente pequeña ($size bytes). Falta el runtime." }

if (-not $InnoCompiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'No se encontró ISCC.exe de Inno Setup 6. Instalá Inno Setup en Windows.'
}
Write-Host 'Compilando instalador EXE de pruebas...' -ForegroundColor Cyan
& $InnoCompiler "/DAppSourceDir=$publish" "/DInstallerOutputDir=$OutputDir" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló ($LASTEXITCODE): no se genera instalador." }
$installer = Join-Path $OutputDir 'Aparcar-Setup-0.26.0-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw "Instalador no encontrado: $installer" }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Write-Host "Instalador generado: $installer"
Write-Host "SHA256: $hash"
Write-Host 'Versión de pruebas: no apta para cobrar comercialmente ni emitir facturas ARCA de producción.'
