<#
.SYNOPSIS
    Crea un certificado AUTOFIRMADO para HTTPS en la red local y lo exporta a .pfx (servidor) y .cer (para instalar en los dispositivos).

.DESCRIPTION
    Solo para LAN sin autoridad certificadora propia. Los navegadores de PC y tablets mostrarán una advertencia hasta que
    se instale el .cer como «entidad de certificación raíz de confianza» en cada dispositivo (ver docs/operations/produccion.md).
    El .pfx contiene la clave privada: protéjalo (no lo suba al repositorio ni lo comparta). La contraseña se pide por
    consola; no se guarda en este script.
    Incluye como nombres alternativos el nombre del servidor y las IP indicadas: acceda siempre por uno de ellos.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerName,
    [string[]]$IpAddress = @(),
    [string]$OutDir = 'C:\ProgramData\TPVBar\certs',
    [int]$ValidYears = 2
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$dns = @($ServerName)
$san = @($dns | ForEach-Object { "DNS=$_" }) + @($IpAddress | ForEach-Object { "IPAddress=$_" })
$cert = New-SelfSignedCertificate -Subject "CN=$ServerName" -DnsName $dns `
    -TextExtension @("2.5.29.17={text}$($san -join '&')") `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($ValidYears) -CertStoreLocation Cert:\LocalMachine\My

$password = Read-Host -AsSecureString 'Contraseña para proteger el archivo .pfx'
$pfx = Join-Path $OutDir 'tpvbar.pfx'
$cer = Join-Path $OutDir 'tpvbar.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
# El almacén del equipo ya no hace falta: Kestrel leerá el .pfx.
Remove-Item -Path "Cert:\LocalMachine\My\$($cert.Thumbprint)" -Force

Write-Host "Certificado creado. Caduca el $($cert.NotAfter.ToString('dd/MM/yyyy')) (apunte la fecha: al caducar los dispositivos dejarán de conectar)."
Write-Host "  Servidor : $pfx"
Write-Host "  Dispositivos : $cer  (instalar como entidad de certificación raíz de confianza)"
