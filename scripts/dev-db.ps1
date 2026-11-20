<#
.SYNOPSIS
    Crea y arranca un clúster PostgreSQL 18 LOCAL de DESARROLLO para TPVBar.

.DESCRIPTION
    - Usa su propio directorio de datos (.pgdata) y puerto (5434). No toca ningún otro PostgreSQL del equipo.
      (Era 5433 hasta que chocó con el PostgreSQL de otra aplicación: ver D-071.) Si el clúster está en marcha en
      otro puerto, lo para y lo vuelve a arrancar en el indicado; si el puerto lo ocupa otro programa, se detiene.
    - Escucha solo en localhost.
    - Genera contraseñas aleatorias; las guarda en .pgdata\dev-credentials.json (ignorado por Git)
      y las registra como User Secrets de TPVBar.Web. Nunca se escriben en appsettings.
    - Es idempotente: se puede ejecutar varias veces.
    NO es el procedimiento de producción (ver docs/operations).
#>
[CmdletBinding()]
param(
    [int]$Port = 5434,
    [string]$PgBin = "C:\Program Files\PostgreSQL\18\bin"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root '.pgdata'
# El log va FUERA del directorio de datos: dentro provoca "sharing violation" en la recuperación (Windows).
$logDir = Join-Path $root '.pglog'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'server.log'
$credFile = Join-Path $data 'dev-credentials.json'
$webProject = Join-Path $root 'src\TPVBar.Web'

function New-RandomPassword {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    ([Convert]::ToBase64String($bytes)).Replace('+', 'A').Replace('/', 'b').Replace('=', 'c')
}

if (-not (Test-Path "$PgBin\initdb.exe")) { throw "No se encuentra PostgreSQL en $PgBin" }

if (-not (Test-Path (Join-Path $data 'PG_VERSION'))) {
    Write-Host "Inicializando clúster en $data ..."
    New-Item -ItemType Directory -Force $data | Out-Null
    $superPw = New-RandomPassword
    $appPw = New-RandomPassword
    $pwFile = Join-Path $env:TEMP "tpvbar-pw-$([guid]::NewGuid()).txt"
    try {
        [IO.File]::WriteAllText($pwFile, $superPw)
        & "$PgBin\initdb.exe" -D $data -U postgres -A scram-sha-256 --pwfile=$pwFile -E UTF8 `
            --locale-provider=icu --icu-locale=es-ES --locale=C | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'initdb ha fallado' }
    }
    finally { Remove-Item $pwFile -Force -ErrorAction SilentlyContinue }

    @{ port = $Port; superuser = 'postgres'; superPassword = $superPw; appUser = 'tpvbar_app'; appPassword = $appPw } |
        ConvertTo-Json | Set-Content -Path $credFile -Encoding utf8
}

$cred = Get-Content $credFile -Raw | ConvertFrom-Json

# demo.ps1 lee el puerto de este archivo: se mantiene al día si cambia.
if ($cred.port -ne $Port) {
    $cred.port = $Port
    $cred | ConvertTo-Json | Set-Content -Path $credFile -Encoding utf8
}

# Si el clúster ya está en marcha pero en otro puerto (p. ej. el antiguo 5433), se reinicia en el nuevo.
& "$PgBin\pg_ctl.exe" status -D $data | Out-Null
if ($LASTEXITCODE -eq 0) {
    $runningPort = [int](Get-Content (Join-Path $data 'postmaster.pid') -TotalCount 4)[3]
    if ($runningPort -ne $Port) {
        Write-Host "PostgreSQL de desarrollo en marcha en el puerto ${runningPort}: se reinicia en $Port ..."
        & "$PgBin\pg_ctl.exe" stop -D $data -m fast | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "No se pudo parar el PostgreSQL de desarrollo. Revise $log" }
    }
}

& "$PgBin\pg_ctl.exe" status -D $data | Out-Null
if ($LASTEXITCODE -ne 0) {
    # Si otro programa (p. ej. el PostgreSQL de otra aplicación) ya escucha en el puerto, TPVBar acabaría
    # conectándose a él y fallaría la contraseña: mejor detenerse con un mensaje claro.
    $busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($busy) {
        $owner = (Get-Process -Id $busy[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
        throw "El puerto $Port ya lo usa otro programa ($owner, PID $($busy[0].OwningProcess)). Use -Port con otro puerto libre."
    }

    Write-Host "Arrancando PostgreSQL en localhost:$Port ..."
    # Proceso desacoplado: el servidor sigue vivo al cerrar esta consola y no retiene su salida.
    Start-Process -FilePath "$PgBin\pg_ctl.exe" -WindowStyle Hidden `
        -ArgumentList @('start', '-D', "`"$data`"", '-l', "`"$log`"", '-o', "`"-p $Port -c listen_addresses=localhost`"")
    $ready = $false
    for ($i = 0; $i -lt 40 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 500
        & "$PgBin\pg_isready.exe" -h localhost -p $Port | Out-Null
        $ready = ($LASTEXITCODE -eq 0)
    }
    if (-not $ready) { throw "No arranca. Revise $log" }
}

$env:PGPASSWORD = $cred.superPassword
function Invoke-Sql([string]$sql, [string]$db = 'postgres') {
    & "$PgBin\psql.exe" -h localhost -p $Port -U postgres -d $db -v ON_ERROR_STOP=1 -tA -c $sql
    if ($LASTEXITCODE -ne 0) { throw "SQL fallido: $sql" }
}

if (-not (Invoke-Sql "SELECT 1 FROM pg_roles WHERE rolname='tpvbar_app'")) {
    Invoke-Sql "CREATE ROLE tpvbar_app LOGIN PASSWORD '$($cred.appPassword)' CREATEDB"  | Out-Null
}
foreach ($db in 'tpvbar', 'tpvbar_test') {
    if (-not (Invoke-Sql "SELECT 1 FROM pg_database WHERE datname='$db'")) {
        Invoke-Sql "CREATE DATABASE $db OWNER tpvbar_app ENCODING 'UTF8' TEMPLATE template0 LOCALE_PROVIDER icu ICU_LOCALE 'es-ES' LOCALE 'C'" | Out-Null
    }
}
Remove-Item Env:\PGPASSWORD

$cs = "Host=localhost;Port=$Port;Database=tpvbar;Username=tpvbar_app;Password=$($cred.appPassword)"
$csTest = "Host=localhost;Port=$Port;Database=tpvbar_test;Username=tpvbar_app;Password=$($cred.appPassword)"
dotnet user-secrets set "ConnectionStrings:TpvBar" $cs --project $webProject | Out-Null
dotnet user-secrets set "ConnectionStrings:TpvBarTest" $csTest --project $webProject | Out-Null

# Contraseña de los usuarios de prueba (solo desarrollo). Aleatoria, se crea una sola vez.
$secrets = dotnet user-secrets list --project $webProject
if (-not ($secrets | Select-String -SimpleMatch 'Seed:DevUserPassword')) {
    dotnet user-secrets set "Seed:DevUserPassword" ("Dev-" + (New-RandomPassword)) --project $webProject | Out-Null
    Write-Host "Contraseña de usuarios de prueba creada en User Secrets (clave Seed:DevUserPassword)."
}

# Clave de la API del agente de impresión: la MISMA en el servidor y en el agente (ambos en User Secrets).
$agentProject = Join-Path $root 'src\TPVBar.PrintAgent'
$webSecrets = dotnet user-secrets list --project $webProject
$keyLine = $webSecrets | Select-String -Pattern '^PrintAgent:ApiKey = (.+)$'
if ($keyLine) {
    $agentKey = $keyLine.Matches[0].Groups[1].Value
}
else {
    $agentKey = "agent-" + (New-RandomPassword)
    dotnet user-secrets set "PrintAgent:ApiKey" $agentKey --project $webProject | Out-Null
    Write-Host "Clave de la API del agente creada en User Secrets (PrintAgent:ApiKey)."
}
dotnet user-secrets set "Agent:ApiKey" $agentKey --project $agentProject | Out-Null

Write-Host "PostgreSQL de desarrollo listo en localhost:$Port (bases: tpvbar, tpvbar_test)."
Write-Host "Cadenas de conexión guardadas en User Secrets de TPVBar.Web."
