<#
.SYNOPSIS
    Genera el paquete de despliegue de TPVBar (servidor web, agente de impresión y scripts) en artifacts\package.

.DESCRIPTION
    Publica en modo Release, con el runtime de .NET instalado en el servidor (framework-dependent). Ejecútelo en el
    equipo de desarrollo; después copie la carpeta artifacts\package al servidor y ejecute allí install-server.ps1.
    No necesita permisos de administrador y no toca ningún servicio.
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts\package' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null

foreach ($p in @(@{ Project = 'src\TPVBar.Web'; Dir = 'web' }, @{ Project = 'src\TPVBar.PrintAgent'; Dir = 'agent' })) {
    Write-Host "Publicando $($p.Project) ..."
    dotnet publish (Join-Path $root $p.Project) -c Release -o (Join-Path $Output $p.Dir) --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló en $($p.Project)" }
}

# Los secretos de desarrollo no deben viajar: comprobamos que no se han empaquetado archivos de configuración sospechosos.
$suspicious = Get-ChildItem -Path $Output -Recurse -Include 'secrets.json', '*.pfx', 'dev-credentials.json', 'appsettings.Development.json' -ErrorAction SilentlyContinue
foreach ($f in $suspicious) {
    if ($f.Name -eq 'appsettings.Development.json') { Remove-Item $f.FullName -Force; continue }
    throw "El paquete contiene un archivo que no debe desplegarse: $($f.FullName)"
}

Copy-Item -Path (Join-Path $root 'scripts') -Destination (Join-Path $Output 'scripts') -Recurse -Exclude 'dev-db.ps1'
$version = (Get-Item (Join-Path $Output 'web\TPVBar.Web.dll')).VersionInfo.ProductVersion
Set-Content -Path (Join-Path $Output 'VERSION.txt') -Value "TPVBar $version`r`nGenerado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -Encoding utf8

Write-Host "Paquete listo en $Output (versión $version)."
