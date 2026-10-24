# Aparcar v0.1 - Prepare a packaged WinUI 3 solution on Windows.
# Creates a generated workspace (ignored by Git) without touching customer data.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$workspace = Join-Path $repo 'Aparcar_VisualStudio'
$src = Join-Path $workspace 'src'
$testsDir = Join-Path $workspace 'tests'
$solution = Join-Path $workspace 'Aparcar.sln'

function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fallo dotnet $($Arguments -join ' ') (codigo $LASTEXITCODE)." }
}

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'Esta solución WinUI 3 se prepara en Windows, no Linux/macOS.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No está instalado .NET SDK. Instalá Visual Studio con WinUI y .NET 10 SDK.'
}
$sdk = & dotnet --version
if ([int]($sdk.Split('.')[0]) -lt 10) {
    throw "Se requiere .NET 10 SDK o superior. Detectado: $sdk"
}
if (Test-Path $workspace) {
    throw "Ya existe '$workspace'. No se sobrescribe para preservar tus cambios. Abrí la solución existente o mové la carpeta primero."
}

Write-Host 'Instalando las plantillas oficiales WinUI 3...'
Invoke-Dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
New-Item -ItemType Directory -Path $src -Force | Out-Null
New-Item -ItemType Directory -Path $testsDir -Force | Out-Null
Invoke-Dotnet new sln -n Aparcar -o $workspace --format sln

Invoke-Dotnet new winui -n Aparcar.Desktop -o (Join-Path $src 'Aparcar.Desktop')
Invoke-Dotnet new classlib -n Aparcar.Domain -f net10.0 -o (Join-Path $src 'Aparcar.Domain')
Invoke-Dotnet new classlib -n Aparcar.Infrastructure -f net10.0 -o (Join-Path $src 'Aparcar.Infrastructure')
Invoke-Dotnet new xunit -n Aparcar.Tests -f net10.0 -o (Join-Path $testsDir 'Aparcar.Tests')

$desktopProj = Join-Path $src 'Aparcar.Desktop\Aparcar.Desktop.csproj'
$domainProj = Join-Path $src 'Aparcar.Domain\Aparcar.Domain.csproj'
$infraProj = Join-Path $src 'Aparcar.Infrastructure\Aparcar.Infrastructure.csproj'
$testProj = Join-Path $testsDir 'Aparcar.Tests\Aparcar.Tests.csproj'

foreach ($project in @($desktopProj,$domainProj,$infraProj,$testProj)) {
    Invoke-Dotnet sln $solution add $project
}
Invoke-Dotnet add $infraProj reference $domainProj
Invoke-Dotnet add $desktopProj reference $infraProj $domainProj
Invoke-Dotnet add $testProj reference $infraProj $domainProj
Invoke-Dotnet add $infraProj package Microsoft.Data.Sqlite

foreach ($name in @('Aparcar.Desktop','Aparcar.Domain','Aparcar.Infrastructure')) {
    $origin = Join-Path $repo "src\$name"
    $destination = Join-Path $src $name
    Get-ChildItem -LiteralPath $origin -Filter '*' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
    }
}
$testSource = Join-Path $repo 'tests\Aparcar.Tests'
Get-ChildItem -LiteralPath $testSource -Filter '*.cs' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $testsDir 'Aparcar.Tests') -Force
}

Write-Host 'Compilando el dominio y la persistencia...'
Invoke-Dotnet build $infraProj -c Debug
Write-Host 'Ejecutando pruebas automatizadas...'
Invoke-Dotnet test $testProj -c Debug
Write-Host 'Compilando la aplicación WinUI 3 (x64)...'
Invoke-Dotnet build $desktopProj -c Debug -p:Platform=x64
Write-Host ''
Write-Host "Preparación terminada. Abrí $solution en Visual Studio, seleccioná x64 y presioná F5."
Write-Host 'IMPORTANTE: Versión de prueba. No apta para cobros reales.'
