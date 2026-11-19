<#
.SYNOPSIS
    Instala TPVBar en el servidor Windows de producción (ejecutar como Administrador, desde el paquete de publish.ps1).

.DESCRIPTION
    Hace, en este orden, y se detiene al primer error:
      1. Comprueba requisitos (administrador, runtime ASP.NET Core 10, herramientas de PostgreSQL).
      2. Crea los roles y la base de datos (scripts\db-roles.sql) con claves aleatorias.
      3. Copia la aplicación y escribe la configuración protegida (cadena de conexión, clave del agente, certificado)
         en C:\ProgramData\TPVBar\config\secrets.json con permisos restringidos. Nada de esto va al repositorio.
      4. Aplica migraciones con el rol propietario, ajusta permisos (db-grants.sql) y crea el administrador inicial.
      5. Registra el servicio de Windows «TPVBar.Web» (cuenta virtual, reinicio automático ante fallos).
      6. Abre el cortafuegos SOLO para la subred local (puerto HTTPS). PostgreSQL no se expone.
      7. Programa la copia diaria (-RegisterBackupTask).

    NO configura: IP fija o reserva DHCP, DNS local, impresoras ni el agente de impresión (ver docs/operations/produccion.md
    y scripts\install-print-agent.ps1). NO sustituye a la validación en el entorno real.
    Pide por consola (nunca por parámetro): la clave del superusuario de PostgreSQL y la del administrador inicial.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PfxPath,
    [Parameter(Mandatory)][string]$AdminUserName,
    [string]$PackageDir = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallDir = 'C:\TPVBar',
    [string]$DataDir = 'C:\ProgramData\TPVBar',
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PgPort = 5432,
    [int]$HttpsPort = 443,
    [string]$BackupSecondaryDir,
    [switch]$RegisterBackupTask,
    [switch]$SkipFirewall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'TPVBar.Web'

function New-RandomPassword {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    ([Convert]::ToBase64String($bytes)).Replace('+', 'A').Replace('/', 'b').Replace('=', 'c')
}
function ConvertTo-PlainText([securestring]$s) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

# --- 1. Requisitos ---------------------------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecute este script como Administrador.' }
if (-not (Test-Path (Join-Path $PackageDir 'web\TPVBar.Web.dll'))) { throw "No se encuentra web\TPVBar.Web.dll en $PackageDir (use el paquete de publish.ps1)." }
if (-not (Test-Path $PfxPath)) { throw "No se encuentra el certificado $PfxPath (cree uno con new-lan-certificate.ps1 o use el de su autoridad)." }
foreach ($tool in 'psql.exe', 'pg_dump.exe', 'pg_restore.exe') { if (-not (Test-Path (Join-Path $PgBin $tool))) { throw "Falta $tool en $PgBin" } }
if (-not ((& dotnet --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 10\.')) {
    throw 'Falta el runtime «ASP.NET Core Runtime 10» (Hosting Bundle). Instálelo desde dotnet.microsoft.com y repita.'
}
$pfxPassword = Read-Host -AsSecureString 'Contraseña del archivo .pfx'
$superPassword = Read-Host -AsSecureString "Contraseña del superusuario 'postgres' de PostgreSQL"
$adminPassword = Read-Host -AsSecureString "Contraseña inicial del administrador '$AdminUserName' (mínimo 12 caracteres)"

# --- 2. Roles y base de datos ----------------------------------------------------------------------------------
$ownerPw = New-RandomPassword; $appPw = New-RandomPassword; $backupPw = New-RandomPassword; $agentKey = 'agent-' + (New-RandomPassword)
$env:PGPASSWORD = ConvertTo-PlainText $superPassword
try {
    & "$PgBin\psql.exe" -h localhost -p $PgPort -U postgres -v ON_ERROR_STOP=1 -q `
        -v owner_pw=$ownerPw -v app_pw=$appPw -v backup_pw=$backupPw -f (Join-Path $PSScriptRoot 'db-roles.sql')
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron crear los roles y la base de datos.' }
}
finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }

# --- 3. Archivos y configuración protegida ---------------------------------------------------------------------
foreach ($d in "$InstallDir\app", "$InstallDir\scripts", "$InstallDir\backups", "$DataDir\config", "$DataDir\logs", "$DataDir\keys") {
    New-Item -ItemType Directory -Force $d | Out-Null
}
Copy-Item -Path (Join-Path $PackageDir 'web\*') -Destination "$InstallDir\app" -Recurse -Force
Copy-Item -Path (Join-Path $PackageDir 'scripts\*') -Destination "$InstallDir\scripts" -Recurse -Force

$pfxTarget = "$DataDir\config\tpvbar.pfx"
Copy-Item -Path $PfxPath -Destination $pfxTarget -Force
$statusFile = "$InstallDir\backups\backup-status.json"
$secretsFile = "$DataDir\config\secrets.json"
$secrets = [ordered]@{
    ConnectionStrings = [ordered]@{
        TpvBar           = "Host=localhost;Port=$PgPort;Database=tpvbar;Username=tpvbar_app;Password=$appPw"
        TpvBarMigrations = "Host=localhost;Port=$PgPort;Database=tpvbar;Username=tpvbar_owner;Password=$ownerPw"
    }
    PrintAgent        = [ordered]@{ ApiKey = $agentKey }
    Security          = [ordered]@{ RequireHttps = $true }
    DataProtection    = [ordered]@{ KeysDirectory = "$DataDir\keys" }
    Backup            = [ordered]@{ StatusFile = $statusFile }
    Logging           = [ordered]@{ File = [ordered]@{ Directory = "$DataDir\logs"; RetainedDays = 30 } }
    Kestrel           = [ordered]@{
        Endpoints = [ordered]@{
            Https = [ordered]@{
                Url         = "https://0.0.0.0:$HttpsPort"
                Certificate = [ordered]@{ Path = $pfxTarget; Password = (ConvertTo-PlainText $pfxPassword) }
            }
        }
    }
}
$secrets | ConvertTo-Json -Depth 6 | Set-Content -Path $secretsFile -Encoding utf8

# Permisos: solo SYSTEM, Administradores y la cuenta del servicio pueden leer la configuración y los certificados.
$svcAccount = "NT SERVICE\$serviceName"
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

# --- 4. Migraciones, permisos y administrador ------------------------------------------------------------------
$env:TPVBAR_CONFIG_FILE = $secretsFile
$env:ASPNETCORE_ENVIRONMENT = 'Production'
try {
    & dotnet "$InstallDir\app\TPVBar.Web.dll" --migrate
    if ($LASTEXITCODE -ne 0) { throw 'Falló la aplicación de migraciones.' }

    $env:PGPASSWORD = $ownerPw
    & "$PgBin\psql.exe" -h localhost -p $PgPort -U tpvbar_owner -d tpvbar -v ON_ERROR_STOP=1 -q -f "$InstallDir\scripts\db-grants.sql"
    if ($LASTEXITCODE -ne 0) { throw 'Falló el ajuste de permisos (db-grants.sql).' }

    $env:TPVBAR_ADMIN_PASSWORD = ConvertTo-PlainText $adminPassword
    & dotnet "$InstallDir\app\TPVBar.Web.dll" --create-admin $AdminUserName
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el administrador.' }
}
finally {
    Remove-Item Env:\PGPASSWORD, Env:\TPVBAR_ADMIN_PASSWORD, Env:\TPVBAR_CONFIG_FILE, Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
}

# --- 5. Servicio de Windows ------------------------------------------------------------------------------------
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "El servicio $serviceName ya existe: use update-server.ps1 para actualizar." }
New-Service -Name $serviceName -DisplayName 'TPVBar (punto de venta)' -BinaryPathName "`"$InstallDir\app\TPVBar.Web.exe`"" `
    -StartupType Automatic -Description 'Servidor web de TPVBar' | Out-Null
sc.exe config $serviceName obj= $svcAccount | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
$envValues = @("TPVBAR_CONFIG_FILE=$secretsFile", 'ASPNETCORE_ENVIRONMENT=Production')
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value $envValues -Force | Out-Null
icacls $DataDir /grant "${svcAccount}:(OI)(CI)RX" | Out-Null
icacls "$DataDir\logs" /grant "${svcAccount}:(OI)(CI)M" | Out-Null
icacls "$DataDir\keys" /grant "${svcAccount}:(OI)(CI)M" | Out-Null
icacls "$InstallDir\backups" /grant "${svcAccount}:(OI)(CI)RX" | Out-Null
icacls "$InstallDir\app" /grant "${svcAccount}:(OI)(CI)RX" | Out-Null

# --- 6. Cortafuegos --------------------------------------------------------------------------------------------
if (-not $SkipFirewall) {
    New-NetFirewallRule -DisplayName 'TPVBar HTTPS (solo red local)' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort `
        -RemoteAddress LocalSubnet -Action Allow -Profile Private, Domain | Out-Null
    Write-Host "Cortafuegos: puerto $HttpsPort abierto solo a la subred local. PostgreSQL ($PgPort) NO se abre."
}

# --- 7. Copia diaria -------------------------------------------------------------------------------------------
if ($RegisterBackupTask) {
    $pgpass = "$DataDir\config\pgpass.conf"
    Set-Content -Path $pgpass -Value "localhost:${PgPort}:tpvbar:tpvbar_backup:$backupPw" -Encoding ascii
    $secondary = if ($BackupSecondaryDir) { " -SecondaryDir '$BackupSecondaryDir'" } else { '' }
    $cmd = "`$env:PGPASSFILE='$pgpass'; & '$InstallDir\scripts\backup.ps1' -BackupDir '$InstallDir\backups' -StatusFile '$statusFile' -Port $PgPort$secondary"
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"$cmd`""
    Register-ScheduledTask -TaskName 'TPVBar copia diaria' -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At 4:00) `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Write-Host 'Tarea programada «TPVBar copia diaria» creada (04:00).'
    if (-not $BackupSecondaryDir) { Write-Warning 'No ha indicado -BackupSecondaryDir: las copias quedarán SOLO en el disco del servidor.' }
}

Start-Service -Name $serviceName
Write-Host ''
Write-Host "Instalación terminada. Compruebe: https://<nombre-del-servidor>:$HttpsPort/health/ready  (debe responder 200)."
Write-Host "Guarde en un lugar seguro y SEPARADO del servidor las claves de la base de datos si necesita recuperarlas: están en $secretsFile."
Write-Host 'Pendiente a mano: IP fija/reserva DHCP, nombre DNS local, instalar el .cer en los dispositivos, impresoras y agente (install-print-agent.ps1).'
