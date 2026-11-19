<#
.SYNOPSIS
    Restaura una copia de TPVBar. Por defecto restaura en una base TEMPORAL, comprueba los datos y la borra
    (prueba periódica de restauración). Para recuperar producción hay que pedirlo de forma explícita.

.DESCRIPTION
    Modo prueba (por defecto):  restore.ps1 -BackupFile C:\TPVBar\backups\tpvbar_...dump
        Crea la base tpvbar_restoretest, restaura, compara tablas clave, aplica los permisos y la elimina.
    Modo recuperación:          restore.ps1 -BackupFile ... -RestoreToProduction
        PARA LA APLICACIÓN ANTES (servicio TPVBar.Web). Renombra la base actual a tpvbar_replaced_<fecha>
        (no la borra), crea una nueva «tpvbar» y restaura en ella. Después arranque el servicio.

    Se conecta como superusuario (-AdminUser, por defecto postgres). La contraseña va en PGPASSWORD o pgpass.conf.
    Verifica la huella SHA-256 si existe el archivo .sha256 junto al volcado.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [switch]$RestoreToProduction,
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [string]$DbHost = 'localhost',
    [int]$Port = 5432,
    [string]$AdminUser = 'postgres',
    [string]$ProductionDatabase = 'tpvbar',
    [string]$ScriptsDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$env:PGOPTIONS = '-c client_min_messages=warning'   # sin avisos «NOTICE» de DROP ... IF EXISTS

if (-not (Test-Path $BackupFile)) { throw "No existe el archivo $BackupFile" }

function Invoke-Psql([string]$db, [string]$sql) {
    $out = & "$PgBin\psql.exe" -h $DbHost -p $Port -U $AdminUser -d $db -v ON_ERROR_STOP=1 -tA --no-password -c $sql
    if ($LASTEXITCODE -ne 0) { throw "SQL fallido: $sql" }
    $out
}

# 1. Integridad del archivo.
$shaFile = "$BackupFile.sha256"
if (Test-Path $shaFile) {
    $expected = ((Get-Content $shaFile -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -Path $BackupFile -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw 'La huella SHA-256 NO coincide: el archivo está dañado o alterado. No se restaura.' }
    Write-Host 'Huella SHA-256 correcta.'
}
else { Write-Warning 'No hay archivo .sha256: no se puede comprobar la integridad del archivo.' }

& "$PgBin\pg_restore.exe" --list $BackupFile | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'El volcado no se puede leer (pg_restore --list).' }

if ($RestoreToProduction) {
    $svc = Get-Service -Name 'TPVBar.Web' -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') { throw 'Pare antes el servicio TPVBar.Web (Stop-Service TPVBar.Web).' }
    $target = $ProductionDatabase
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $replaced = "${ProductionDatabase}_replaced_$stamp"
    Invoke-Psql 'postgres' "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$ProductionDatabase' AND pid <> pg_backend_pid()" | Out-Null
    if (Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$ProductionDatabase'") {
        Invoke-Psql 'postgres' "ALTER DATABASE $ProductionDatabase RENAME TO $replaced" | Out-Null
        Write-Host "La base anterior se conserva como $replaced (bórrela a mano cuando confirme la recuperación)."
    }
}
else {
    $target = 'tpvbar_restoretest'
    Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS $target" | Out-Null
}

Invoke-Psql 'postgres' "CREATE DATABASE $target OWNER tpvbar_owner ENCODING 'UTF8' TEMPLATE template0 LOCALE_PROVIDER icu ICU_LOCALE 'es-ES' LOCALE 'C'" | Out-Null

try {
    # --no-owner/--no-privileges: la propiedad y los permisos se reaplican con los scripts del proyecto.
    & "$PgBin\pg_restore.exe" -h $DbHost -p $Port -U $AdminUser -d $target --no-owner --no-privileges --exit-on-error --no-password $BackupFile
    if ($LASTEXITCODE -ne 0) { throw "pg_restore falló (código $LASTEXITCODE)." }

    # Propiedad al rol de migraciones (las secuencias de identidad la siguen con su tabla) y permisos de la aplicación.
    Invoke-Psql $target 'ALTER SCHEMA tpv OWNER TO tpvbar_owner' | Out-Null
    $tables = Invoke-Psql $target "SELECT format('ALTER TABLE tpv.%I OWNER TO tpvbar_owner', tablename) FROM pg_tables WHERE schemaname='tpv'"
    foreach ($stmt in $tables) { Invoke-Psql $target $stmt | Out-Null }
    $funcs = Invoke-Psql $target "SELECT format('ALTER FUNCTION %s OWNER TO tpvbar_owner', p.oid::regprocedure) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname='tpv'"
    foreach ($stmt in $funcs) { Invoke-Psql $target $stmt | Out-Null }
    Invoke-Psql $target "GRANT CONNECT ON DATABASE $target TO tpvbar_app, tpvbar_backup" | Out-Null
    Invoke-Psql $target 'GRANT USAGE ON SCHEMA tpv TO tpvbar_backup' | Out-Null
    & "$PgBin\psql.exe" -h $DbHost -p $Port -U $AdminUser -d $target -v ON_ERROR_STOP=1 --no-password -q -f (Join-Path $ScriptsDir 'db-grants.sql')
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron aplicar los permisos (db-grants.sql).' }

    # Comprobación de coherencia con datos reales.
    $report = [ordered]@{
        migraciones = Invoke-Psql $target 'SELECT count(*) FROM tpv.__ef_migrations_history'
        usuarios    = Invoke-Psql $target 'SELECT count(*) FROM tpv.users'
        comandas    = Invoke-Psql $target 'SELECT count(*) FROM tpv.orders'
        cobros      = Invoke-Psql $target 'SELECT count(*) FROM tpv.payments'
        auditoria   = Invoke-Psql $target 'SELECT count(*) FROM tpv.audit_events'
        # Coherencia contable: lo pagado de cada comanda coincide con cobros menos devoluciones.
        descuadres  = Invoke-Psql $target 'SELECT count(*) FROM tpv.orders o WHERE o.paid_total <> COALESCE((SELECT sum(p.amount) FROM tpv.payments p WHERE p.order_id = o.id), 0) - COALESCE((SELECT sum(r.amount) FROM tpv.payment_refunds r WHERE r.order_id = o.id), 0)'
    }
    $report.GetEnumerator() | ForEach-Object { Write-Host ('  {0,-12} {1}' -f $_.Key, $_.Value) }
    if ([int]$report.descuadres -ne 0) { Write-Warning "Hay $($report.descuadres) comanda(s) cuyo pagado no cuadra con cobros y devoluciones: revisar." }

    if ($RestoreToProduction) {
        Write-Host "Recuperación completada en la base '$target'. Arranque el servicio (Start-Service TPVBar.Web) y compruebe /health/ready."
    }
    else {
        Write-Host 'PRUEBA DE RESTAURACIÓN CORRECTA.'
    }
}
finally {
    if (-not $RestoreToProduction) {
        Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS $target" | Out-Null
    }
}
