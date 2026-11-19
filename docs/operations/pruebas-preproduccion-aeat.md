# Primer envío de prueba a la AEAT (preproducción)

> Guía y hoja de resultados. **Todavía no se ha hecho ninguna prueba real**: la columna «Resultado» está vacía a propósito.
> El entorno de preproducción **no tiene efecto fiscal**, pero sí exige un certificado válido y un NIF reconocido por la AEAT en ese entorno.

## 0. Qué necesita (lo que no puedo hacer yo)

| Necesidad | Quién | Estado |
|---|---|---|
| Certificado electrónico válido del obligado tributario, de su representante o de sello autorizado | Propietario / asesor | Pendiente |
| Comprobar en la sede de la AEAT cómo se prueba en preproducción (portal de pruebas externas) y que el NIF del emisor es válido allí | Asesor | Pendiente |
| Datos del **productor** del software (nombre, NIF, identificador de 2 caracteres) | Quien firma la declaración responsable | Pendiente |
| Servidor con hora correcta y salida a Internet por el puerto 443 hacia `prewww1.aeat.es` (o `prewww10.aeat.es` con sello) | Propietario | Por comprobar |

## 1. Instalar el certificado

- **Almacén de Windows (recomendado):** importar el certificado en *Equipo local → Personal* con la clave privada, y dar permiso de lectura de la
  clave privada a la cuenta con la que corre el servicio `TPVBar.Web` (administrar claves privadas).
- **Archivo PFX:** guardarlo fuera del repositorio y con permisos restringidos; la contraseña solo en el archivo protegido del servidor.

## 2. Configurar el servidor

En el archivo protegido (`TPVBAR_CONFIG_FILE`, por defecto `C:\ProgramData\TPVBar\config\secrets.json`), añadir la sección (ejemplo con huella):

```json
"Billing": {
  "Aeat": {
    "Mode": "PreProduction",
    "UseSealCertificate": false,
    "CertificateThumbprint": "HUELLA-DEL-CERTIFICADO",
    "CertificateStore": "LocalMachine"
  }
}
```

Reiniciar el servicio `TPVBar.Web`. **No usar `Production` para esta prueba.**

## 3. Comprobación previa (solo lectura)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\check-aeat-preproduccion.ps1 -Online -EmitterNif B12345674
```

Revisa configuración, certificado (caducidad, clave privada, uso, NIF), red hacia la AEAT y, con `-Online`, el TLS con el certificado. No envía facturas.

## 4. Preparar TPVBar

1. **Facturación → Datos del emisor:** nombre, NIF y domicilio reales del obligado tributario.
2. **Facturación → Sistema informático:** datos del productor.
3. Comprobar que el estado del envío muestra «pruebas (preproducción)» y sin problemas de configuración.

## 5. Primer envío

1. Emitir una **factura simplificada de prueba** desde Cobro (con caja abierta y una comanda cobrada).
2. **Facturación → Enviar ahora.** Mirar el resultado y la factura (estado, CSV, mensaje).
3. Repetir con: factura completa, rectificativa parcial y una anulación, **esperando el tiempo de espera** que indica la AEAT entre envíos.

## 6. Hoja de resultados (rellenar)

| Prueba | Fecha | Resultado | CSV / código de error | Observaciones |
|---|---|---|---|---|
| Conexión TLS con el certificado (script `-Online`) | | | | |
| Alta de factura simplificada (F2) | | | | |
| Alta de factura completa (F1) con destinatario | | | | |
| Rectificativa por diferencias (R5/R1) | | | | |
| Anulación | | | | |
| Dos envíos seguidos (cadena: `RegistroAnterior`) | | | | |
| Reintento tras cortar la red a propósito | | | | |

## 7. Si algo falla

| Síntoma | Probable causa | Qué hacer |
|---|---|---|
| Estado «Configuración» con el motivo | Certificado no encontrado, sin clave privada o caducado | Revisar paso 1 y 2; ejecutar el script |
| Mensajes de SOAP Fault o TLS | Certificado no aceptado por la AEAT, puerto equivocado (sello o personal) | Probar `UseSealCertificate` al revés; confirmar con el asesor |
| Registro **Rechazado** con error de huella | El formato de `BillingHash` (D-060) no coincide con el oficial | Comparar con el documento oficial de la AEAT y corregir `BillingHash`; **no** reenviar mientras no se corrija |
| Registro Rechazado por NIF/emisor/titular | NIF no dado de alta en preproducción o certificado de otra persona | Resolver con el asesor y pulsar «Reintentar» |
| Rechazado por datos del sistema informático | Productor/identificador no coinciden con la declaración | Corregir en «Sistema informático» y «Reintentar» |
| La cola se detiene | Un rechazo bloquea los siguientes a propósito | Revisar el primer registro rechazado |

Cuando una prueba falle por una causa de TPVBar, anote el **código de error y el mensaje** y se corrige antes de repetir. Solo cuando todas las pruebas
de la hoja estén en verde y el asesor lo apruebe se puede valorar `Production`.
