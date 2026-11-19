<#
.SYNOPSIS
    Comprobación previa al primer envío de TPVBar a la AEAT (preproducción). SOLO LEE: no envía ninguna factura.
.DESCRIPTION
    Revisa la configuración Billing:Aeat del servidor, el certificado (caducidad, clave privada, uso, NIF) y, con -Online,
    que el servicio de pruebas de la AEAT responde a una petición GET con ese certificado (no se envían datos de facturas).
    Muestra APTO / AVISO / FALLO por cada comprobación. No modifica nada.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\check-aeat-preproduccion.ps1
    powershell -ExecutionPolicy Bypass -File scripts\check-aeat-preproduccion.ps1 -Online -EmitterNif B12345674
#>
[CmdletBinding()]
param(
    # Archivo protegido del servidor (el mismo que TPVBAR_CONFIG_FILE).
    [string]$ConfigFile = 'C:\ProgramData\TPVBar\config\secrets.json',
    # NIF del obligado tributario (emisor) para compararlo con el del certificado.
    [string]$EmitterNif,
    # Hace una petición GET al servicio de pruebas con el certificado (sin datos de facturas).
    [switch]$Online
)

$ErrorActionPreference = 'Stop'
$script:fallos = 0
$script:avisos = 0

function Resultado([string]$nivel, [string]$texto) {
    switch ($nivel) {
        'OK' { Write-Host ('[ APTO  ] ' + $texto) -ForegroundColor Green }
        'AVISO' { $script:avisos++; Write-Host ('[ AVISO ] ' + $texto) -ForegroundColor Yellow }
        'FALLO' { $script:fallos++; Write-Host ('[ FALLO ] ' + $texto) -ForegroundColor Red }
    }
}

Write-Host ''
Write-Host 'TPVBar · comprobación previa de preproducción AEAT (solo lectura)' -ForegroundColor Cyan
Write-Host ''

# 1. Configuración ------------------------------------------------------------------------------------------------
$aeat = $null
if (-not (Test-Path $ConfigFile)) {
    Resultado 'FALLO' "No existe el archivo de configuración protegido: $ConfigFile"
}
else {
    try {
        $json = Get-Content -Raw -Path $ConfigFile | ConvertFrom-Json
        $aeat = $json.Billing.Aeat
    }
    catch { Resultado 'FALLO' "El archivo de configuración no es un JSON válido: $($_.Exception.Message)" }

    if ($null -eq $aeat) {
        Resultado 'FALLO' 'Falta la sección Billing:Aeat en el archivo de configuración (vea docs\operations\facturacion.md).'
    }
    else {
        $modo = [string]$aeat.Mode
        if ($modo -eq 'PreProduction') { Resultado 'OK' 'Mode = PreProduction (entorno de pruebas, sin efecto fiscal).' }
        elseif ($modo -eq 'Production') { Resultado 'AVISO' 'Mode = Production: los envíos tendrían EFECTO FISCAL REAL. Para la primera prueba use PreProduction.' }
        else { Resultado 'AVISO' "Mode = '$modo': el envío está desactivado. Para probar, ponga PreProduction." }
    }
}

# 2. Certificado --------------------------------------------------------------------------------------------------
$cert = $null
if ($null -ne $aeat) {
    try {
        if ($aeat.CertificatePfxPath) {
            if (-not (Test-Path $aeat.CertificatePfxPath)) { throw "No existe el archivo PFX: $($aeat.CertificatePfxPath)" }
            $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($aeat.CertificatePfxPath, [string]$aeat.CertificatePfxPassword, $flags)
        }
        elseif ($aeat.CertificateThumbprint) {
            $ubicacion = if ($aeat.CertificateStore -eq 'CurrentUser') { 'CurrentUser' } else { 'LocalMachine' }
            $huella = ($aeat.CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
            $cert = Get-ChildItem -Path "Cert:\$ubicacion\My" | Where-Object { $_.Thumbprint -eq $huella } | Select-Object -First 1
            if ($null -eq $cert) { throw "El certificado $huella no está en el almacén $ubicacion\Personal." }
        }
        else { throw 'Falta CertificateThumbprint o CertificatePfxPath en Billing:Aeat.' }
    }
    catch { Resultado 'FALLO' "Certificado: $($_.Exception.Message)" }
}

if ($null -ne $cert) {
    Write-Host ('           Titular : ' + $cert.Subject)
    Write-Host ('           Emisor  : ' + $cert.Issuer)
    Write-Host ('           Caduca  : ' + $cert.NotAfter.ToString('dd/MM/yyyy'))
    $dias = [int]($cert.NotAfter - (Get-Date)).TotalDays
    if ($dias -lt 0) { Resultado 'FALLO' "El certificado caducó hace $(-$dias) días." }
    elseif ($dias -lt 30) { Resultado 'AVISO' "El certificado caduca en $dias días: renuévelo pronto." }
    else { Resultado 'OK' "Certificado vigente ($dias días)." }

    if ($cert.HasPrivateKey) { Resultado 'OK' 'El certificado tiene clave privada.' }
    else { Resultado 'FALLO' 'El certificado NO tiene clave privada accesible: no se podrá autenticar.' }

    $eku = $cert.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
    if ($null -eq $eku -or ($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.2' })) { Resultado 'OK' 'Admite autenticación de cliente.' }
    else { Resultado 'AVISO' 'El certificado no declara «autenticación de cliente»; la AEAT podría rechazarlo.' }

    # NIF del titular: los certificados españoles lo llevan en el número de serie del sujeto (IDCES-NIF, o NIF a secas).
    $nifCert = $null
    if ($cert.Subject -match '(?:serialNumber|SERIALNUMBER|OID\.2\.5\.4\.5)=(?:IDCES-)?([A-Za-z0-9]{9})') { $nifCert = $Matches[1].ToUpperInvariant() }
    elseif ($cert.Subject -match '\b([A-Za-z]?\d{7,8}[A-Za-z])\b') { $nifCert = $Matches[1].ToUpperInvariant() }
    if ($nifCert) {
        Write-Host ('           NIF en el certificado: ' + $nifCert)
        if ($EmitterNif) {
            $emisor = ($EmitterNif -replace '[\s-]', '').ToUpperInvariant()
            if ($emisor -eq $nifCert) { Resultado 'OK' 'El NIF del certificado coincide con el del emisor.' }
            else { Resultado 'AVISO' "El NIF del certificado ($nifCert) no es el del emisor ($emisor): debe ser su representante o un certificado de sello autorizado." }
        }
    }
    else { Resultado 'AVISO' 'No se pudo leer el NIF del certificado; compruébelo a mano.' }
}

# 3. Comprobaciones del entorno ---------------------------------------------------------------------------------------
$sello = $false
if ($null -ne $aeat -and $aeat.UseSealCertificate -eq $true) { $sello = $true }
$servidor = if ($sello) { 'prewww10.aeat.es' } else { 'prewww1.aeat.es' }
$url = "https://$servidor/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP"

try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tarea = $tcp.ConnectAsync($servidor, 443)
    if ($tarea.Wait(8000) -and $tcp.Connected) { Resultado 'OK' "Hay conexión de red con ${servidor}:443." }
    else { Resultado 'FALLO' "No se alcanza ${servidor}:443 (cortafuegos, proxy o sin Internet)." }
    $tcp.Close()
}
catch { Resultado 'FALLO' "Sin conexión con ${servidor}: $($_.Exception.Message)" }

$reloj = Get-Date
Write-Host ('           Reloj del servidor: ' + $reloj.ToString('dd/MM/yyyy HH:mm:ss') + ' (debe estar en hora: la AEAT valida la fecha-hora del registro)')

# 4. Petición de prueba con el certificado (opcional, sin datos de facturas) ---------------------------------------
if ($Online) {
    if ($null -eq $cert) { Resultado 'AVISO' 'Se omite la prueba en línea: no hay certificado cargado.' }
    else {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $peticion = [System.Net.HttpWebRequest]::Create($url)
            $peticion.Method = 'GET'
            $peticion.Timeout = 20000
            [void]$peticion.ClientCertificates.Add($cert)
            try {
                $respuesta = $peticion.GetResponse()
                Resultado 'OK' "El servicio de pruebas respondió HTTP $([int]$respuesta.StatusCode) con el certificado."
                $respuesta.Close()
            }
            catch [System.Net.WebException] {
                $r = $_.Exception.Response
                if ($null -ne $r) {
                    # Un 4xx/5xx a un GET del endpoint SOAP es normal (solo admite POST): lo importante es que el TLS con certificado se completó.
                    Resultado 'OK' "El servicio respondió HTTP $([int]$r.StatusCode) (normal para un GET): TLS con el certificado completado."
                    $r.Close()
                }
                else { Resultado 'FALLO' "No se pudo establecer la conexión con el certificado: $($_.Exception.Message)" }
            }
        }
        catch { Resultado 'FALLO' "Prueba en línea: $($_.Exception.Message)" }
    }
}
else {
    Write-Host '           (Use -Online para probar la conexión TLS con el certificado; no envía facturas.)'
}

Write-Host ''
Write-Host ('Resumen: {0} fallo(s), {1} aviso(s).' -f $script:fallos, $script:avisos) -ForegroundColor Cyan
Write-Host 'Siguiente paso: complete «Facturación → Sistema informático», emita una factura de prueba y pulse «Enviar ahora» (docs\operations\pruebas-preproduccion-aeat.md).'
if ($script:fallos -gt 0) { exit 1 } else { exit 0 }
