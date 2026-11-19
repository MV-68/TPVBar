<#
.SYNOPSIS
    Copia de seguridad de la base de datos de TPVBar: volcado, verificación de integridad, duplicado en otra
    ubicación, retención y estado para la pantalla de diagnóstico.

.DESCRIPTION
    - pg_dump en formato personalizado (-Fc, comprimido) con un rol de SOLO LECTURA (tpvbar_backup).
    - Verifica el volcado con pg_restore --list y guarda su huella SHA-256.
    - Copia el volcado a -SecondaryDir (otro disco, unidad de red o USB externo) y comprueba que la copia es idéntica.
      Una copia en el mismo disco del servidor NO protege frente a un fallo físico, un robo o un cifrado malicioso.
    - Retención: borra volcados con más de -RetentionDays días, conservando siempre los -KeepAtLeast más recientes.
    - Escribe -StatusFile (JSON) con el resultado; la pantalla «Diagnóstico» lo lee.
    - La contraseña NO va por parámetro: use la variable PGPASSWORD o el archivo %APPDATA%\postgresql\pgpass.conf
      del usuario que ejecuta la tarea. Se recomienda un archivo pgpass.conf con permisos restringidos.

    Programarlo a diario (ver docs/operations/copias-y-restauracion.md):
    schtasks /Create /TN "TPVBar copia diaria" /SC DAILY /ST 04:00 /RU SYSTEM /TR "powershell -NoProfile -File C:\TPVBar\scripts\backup.ps1 ..."

.PARAMETER Reason
    Etiqueta que se añade al nombre: «daily» (por defecto), «premigration», «manual».
#>
[CmdletBinding()]
param(
    [string]$BackupDir = 'C:\TPVBar\backups',
    [string]$SecondaryDir,
    [string]$StatusFile = 'C:\TPVBar\backups\backup-status.json',
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [string]$DbHost = 'localhost',
    [int]$Port = 5432,
    [string]$Database = 'tpvbar',
    [string]$User = 'tpvbar_backup',
    [int]$RetentionDays = 30,
    [int]$KeepAtLeast = 3,
    [ValidatePattern('^[a-z0-9-]+$')][string]$Reason = 'daily'
)

$ErrorActionPreference = 'Stop'

function Write-Status([bool]$ok, [string]$file, [long]$size, [bool]$verified, [bool]$copied, [string]$message) {
    $status = [ordered]@{
        success                   = $ok
        finishedAt                = (Get-Date).ToUniversalTime().ToString('o')
        fileName                  = $file
        sizeBytes                 = $size
        verified                  = $verified
        copiedToSecondaryLocation = $copied
        message                   = $message
    }
    $dir = Split-Path -Parent $StatusFile
    New-Item -ItemType Directory -Force $dir | Out-Null
    # Escritura atómica: la aplicación nunca lee un JSON a medias.
    $tmp = "$StatusFile.tmp"
    ($status | ConvertTo-Json) | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $StatusFile -Force
}

function Remove-OldBackups([string]$dir) {
    $all = Get-ChildItem -Path $dir -Filter 'tpvbar_*.dump' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    $cutoff = (Get-Date).AddDays(-$RetentionDays)
    $all | Select-Object -Skip $KeepAtLeast | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
        Remove-Item $_.FullName -Force
        Remove-Item "$($_.FullName).sha256" -Force -ErrorAction SilentlyContinue
    }
}

$name = $null
$size = 0
try {
    if (-not (Test-Path "$PgBin\pg_dump.exe")) { throw "No se encuentra pg_dump en $PgBin" }
    New-Item -ItemType Directory -Force $BackupDir | Out-Null

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $name = "tpvbar_${stamp}_$Reason.dump"
    $final = Join-Path $BackupDir $name
    $partial = "$final.partial"

    & "$PgBin\pg_dump.exe" -h $DbHost -p $Port -U $User -d $Database -Fc -Z 6 --no-password -f $partial
    if ($LASTEXITCODE -ne 0) { throw "pg_dump falló (código $LASTEXITCODE)." }

    # Verificación de integridad: el volcado debe poder leerse entero y no estar vacío.
    $toc = & "$PgBin\pg_restore.exe" --list $partial
    if ($LASTEXITCODE -ne 0 -or -not ($toc | Select-String -SimpleMatch 'TABLE DATA')) {
        throw 'La verificación del volcado falló (pg_restore --list).'
    }

    Move-Item -Path $partial -Destination $final
    $size = (Get-Item $final).Length
    $hash = (Get-FileHash -Path $final -Algorithm SHA256).Hash
    Set-Content -Path "$final.sha256" -Value "$hash *$name" -Encoding ascii

    $copied = $false
    if ($SecondaryDir) {
        New-Item -ItemType Directory -Force $SecondaryDir | Out-Null
        Copy-Item -Path $final, "$final.sha256" -Destination $SecondaryDir -Force
        $copyHash = (Get-FileHash -Path (Join-Path $SecondaryDir $name) -Algorithm SHA256).Hash
        if ($copyHash -ne $hash) { throw 'La copia en la ubicación secundaria no coincide con el original.' }
        $copied = $true
        Remove-OldBackups $SecondaryDir
    }
    Remove-OldBackups $BackupDir

    $msg = if ($copied) { 'Copia correcta y duplicada.' } else { 'Copia correcta, SIN ubicación secundaria configurada.' }
    Write-Status $true $name $size $true $copied $msg
    Write-Host "$msg ($name, $([math]::Round($size / 1KB)) KiB)"
}
catch {
    Get-ChildItem -Path $BackupDir -Filter '*.partial' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Status $false $name $size $false $false $_.Exception.Message
    [Console]::Error.WriteLine("Copia de seguridad fallida: $($_.Exception.Message)")
    exit 1
}
