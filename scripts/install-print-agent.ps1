<#
.SYNOPSIS
    Instala el agente de impresión de TPVBar como servicio de Windows en el equipo al que están conectadas las impresoras
    (puede ser el propio servidor). Ejecutar como Administrador.

.DESCRIPTION
    - Copia el agente a C:\TPVBar\agent y registra el servicio «TPVBar.PrintAgent» con reinicio automático.
    - La clave de la API NO se pasa por parámetro: se pide por consola (es la misma que PrintAgent:ApiKey del servidor,
      que está en C:\ProgramData\TPVBar\config\secrets.json del servidor) y se guarda en la variable de entorno del servicio.
    - El agente NO accede a la base de datos: solo habla con el servidor por HTTPS con esa clave.
    - Si el servidor usa un certificado autofirmado, instale antes el .cer como raíz de confianza en ESTE equipo.
    No instala controladores de impresora ni crea impresoras: configúrelas en Windows y en la pantalla «Impresoras» de TPVBar.
    LA IMPRESIÓN REAL NO HA SIDO PROBADA CON HARDWARE: use docs/operations/pruebas-en-entorno-real.md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [string]$AgentName = $env:COMPUTERNAME,
    [string]$PackageDir = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallDir = 'C:\TPVBar'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'TPVBar.PrintAgent'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecute este script como Administrador.' }
if ($ServerUrl -notmatch '^https://') { Write-Warning 'ServerUrl no es HTTPS: la clave del agente viajaría sin cifrar por la red.' }
if (-not (Test-Path (Join-Path $PackageDir 'agent\TPVBar.PrintAgent.exe'))) { throw "No se encuentra agent\TPVBar.PrintAgent.exe en $PackageDir" }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "El servicio $serviceName ya existe." }

$key = Read-Host -AsSecureString 'Clave de la API del agente (PrintAgent:ApiKey del servidor)'
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($key)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

New-Item -ItemType Directory -Force "$InstallDir\agent" | Out-Null
Copy-Item -Path (Join-Path $PackageDir 'agent\*') -Destination "$InstallDir\agent" -Recurse -Force

New-Service -Name $serviceName -DisplayName 'TPVBar agente de impresión' -BinaryPathName "`"$InstallDir\agent\TPVBar.PrintAgent.exe`"" `
    -StartupType Automatic -Description 'Imprime los tickets de TPVBar' | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
$envValues = @("Agent__ServerUrl=$ServerUrl", "Agent__ApiKey=$plain", "Agent__Name=$AgentName", 'DOTNET_ENVIRONMENT=Production')
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value $envValues -Force | Out-Null

Start-Service -Name $serviceName
Write-Host 'Agente instalado. En TPVBar → Diagnóstico debe aparecer «En línea» en unos segundos.'
Write-Host 'La cuenta del servicio es LocalSystem (necesaria para usar las impresoras de Windows); no la use para otros fines.'
