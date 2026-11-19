<#
.SYNOPSIS
    Actualiza TPVBar a una versión nueva con copia previa y marcha atrás posible (ejecutar como Administrador).

.DESCRIPTION
    1. Copia de seguridad «premigration» (si falla, NO se actualiza).
    2. Para el servicio y guarda la versión actual en C:\TPVBar\previous (solo la inmediatamente anterior).
    3. Copia la versión nueva, aplica migraciones con el rol propietario y reajusta permisos.
    4. Arranca el servicio y comprueba /health/ready. Si no responde 200, avisa y deja la versión anterior disponible.

    Marcha atrás: si las migraciones NO llegaron a aplicarse, basta con volver a copiar la carpeta previous\app.
    Si SE aplicaron, hay que restaurar también la copia «premigration» (docs/operations/copias-y-restauracion.md),
    porque las migraciones no se deshacen automáticamente.
    Pide por consola la contraseña del rol tpvbar_backup solo si no existe pgpass.conf en la configuración.
#>
[CmdletBinding()]
param(
    [string]$PackageDir = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallDir = 'C:\TPVBar',
    [string]$DataDir = 'C:\ProgramData\TPVBar',
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PgPort = 5432,
    [int]$HttpsPort = 443,
    [string]$BackupSecondaryDir
)

$ErrorActionPreference = 'Stop'
$serviceName = 'TPVBar.Web'
$secretsFile = "$DataDir\config\secrets.json"
$statusFile = "$InstallDir\backups\backup-status.json"

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecute este script como Administrador.' }
if (-not (Test-Path (Join-Path $PackageDir 'web\TPVBar.Web.dll'))) { throw "No se encuentra web\TPVBar.Web.dll en $PackageDir" }
if (-not (Test-Path $secretsFile)) { throw "No hay instalación previa (falta $secretsFile): use install-server.ps1." }

# 1. Copia previa a la migración.
$env:PGPASSFILE = "$DataDir\config\pgpass.conf"
$secondary = @{}
if ($BackupSecondaryDir) { $secondary.SecondaryDir = $BackupSecondaryDir }
& "$InstallDir\scripts\backup.ps1" -BackupDir "$InstallDir\backups" -StatusFile $statusFile -Port $PgPort -Reason premigration @secondary
if ($LASTEXITCODE -ne 0) { throw 'La copia previa falló: no se actualiza.' }

# 2. Parar y guardar la versión anterior.
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $serviceName; $svc.WaitForStatus('Stopped', '00:00:30') }
$previous = "$InstallDir\previous"
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
New-Item -ItemType Directory -Force "$previous" | Out-Null
Copy-Item "$InstallDir\app" "$previous\app" -Recurse
Copy-Item "$InstallDir\scripts" "$previous\scripts" -Recurse

# 3. Versión nueva + migraciones con el rol propietario.
Remove-Item "$InstallDir\app\*" -Recurse -Force
Copy-Item -Path (Join-Path $PackageDir 'web\*') -Destination "$InstallDir\app" -Recurse -Force
Copy-Item -Path (Join-Path $PackageDir 'scripts\*') -Destination "$InstallDir\scripts" -Recurse -Force

$config = Get-Content $secretsFile -Raw | ConvertFrom-Json
$ownerPw = ([regex]::Match($config.ConnectionStrings.TpvBarMigrations, 'Password=([^;]+)')).Groups[1].Value
$env:TPVBAR_CONFIG_FILE = $secretsFile
$env:ASPNETCORE_ENVIRONMENT = 'Production'
try {
    & dotnet "$InstallDir\app\TPVBar.Web.dll" --migrate
    if ($LASTEXITCODE -ne 0) { throw 'Falló la aplicación de migraciones. La copia premigration permite volver atrás.' }
    $env:PGPASSWORD = $ownerPw
    & "$PgBin\psql.exe" -h localhost -p $PgPort -U tpvbar_owner -d tpvbar -v ON_ERROR_STOP=1 -q -f "$InstallDir\scripts\db-grants.sql"
    if ($LASTEXITCODE -ne 0) { throw 'Falló el ajuste de permisos (db-grants.sql).' }
}
finally { Remove-Item Env:\PGPASSWORD, Env:\TPVBAR_CONFIG_FILE, Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }

# 4. Arrancar y comprobar.
Start-Service -Name $serviceName
$ok = $false
for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
    Start-Sleep -Seconds 2
    try {
        # Certificado autofirmado: la comprobación local no valida la cadena (solo mira el estado de salud).
        $response = & curl.exe -sk -o NUL -w '%{http_code}' "https://localhost:$HttpsPort/health/ready"
        $ok = ($response -eq '200')
    } catch { }
}
if ($ok) { Write-Host 'Actualización correcta: /health/ready responde 200.' }
else { Write-Warning "El servicio no responde 200 en /health/ready. Revise los registros en $DataDir\logs. La versión anterior está en $previous." }
