<#
.SYNOPSIS
    Prepara y arranca un entorno DEMO de TPVBar con datos ficticios, sin tocar la base de desarrollo.

.DESCRIPTION
    - Usa el PostgreSQL de desarrollo (scripts\dev-db.ps1) y crea una base propia: tpvbar_demo.
    - Aplica migraciones, catálogo demo, usuarios de prueba (dev.*) e historial ficticio de ventas (--seed-demo).
    - Arranca la web en http://localhost:5142 (puerto distinto al de desarrollo).
    - Es idempotente. Con -Reset se borra y se recrea la base demo.
    SOLO DESARROLLO / DEMOSTRACIÓN: no usar con clientes reales ni como instalación de producción.
#>
[CmdletBinding()]
param(
    [switch]$Reset,
    [switch]$NoRun,
    [int]$WebPort = 5142,
    [string]$PgBin = "C:\Program Files\PostgreSQL\18\bin"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$webProject = Join-Path $root 'src\TPVBar.Web'
$credFile = Join-Path $root '.pgdata\dev-credentials.json'

& (Join-Path $PSScriptRoot 'dev-db.ps1') -PgBin $PgBin | Out-Null
$cred = Get-Content $credFile -Raw | ConvertFrom-Json
$port = $cred.port

$env:PGPASSWORD = $cred.superPassword
function Invoke-Sql([string]$sql) {
    & "$PgBin\psql.exe" -h localhost -p $port -U postgres -d postgres -v ON_ERROR_STOP=1 -tA -c $sql
    if ($LASTEXITCODE -ne 0) { throw "SQL fallido: $sql" }
}
try {
    if ($Reset -and (Invoke-Sql "SELECT 1 FROM pg_database WHERE datname='tpvbar_demo'")) {
        Write-Host 'Eliminando la base demo anterior ...'
        Invoke-Sql "DROP DATABASE tpvbar_demo WITH (FORCE)" | Out-Null
    }
    if (-not (Invoke-Sql "SELECT 1 FROM pg_database WHERE datname='tpvbar_demo'")) {
        Invoke-Sql "CREATE DATABASE tpvbar_demo OWNER tpvbar_app ENCODING 'UTF8' TEMPLATE template0 LOCALE_PROVIDER icu ICU_LOCALE 'es-ES' LOCALE 'C'" | Out-Null
    }
}
finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }

# La cadena de conexión demo sustituye a la de User Secrets solo para este proceso.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__TpvBar = "Host=localhost;Port=$port;Database=tpvbar_demo;Username=tpvbar_app;Password=$($cred.appPassword)"

Write-Host 'Cargando datos de demostración ...'
dotnet run --project $webProject --no-launch-profile -- --seed-demo
if ($LASTEXITCODE -ne 0) { throw 'La carga de datos demo ha fallado.' }

Write-Host ''
Write-Host "Usuarios: dev.admin, dev.encargado, dev.camarero, dev.cocina, dev.caja, dev.comandero"
Write-Host "Contraseña: dotnet user-secrets list --project src\TPVBar.Web   (clave Seed:DevUserPassword)"
if ($NoRun) { return }

$env:ASPNETCORE_URLS = "http://localhost:$WebPort"
Write-Host "Arrancando en http://localhost:$WebPort (Ctrl+C para parar) ..."
dotnet run --project $webProject --no-launch-profile
