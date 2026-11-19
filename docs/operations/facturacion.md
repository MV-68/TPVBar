# Facturación (Fase 7)

> **Estado: NO apto fiscalmente.** TPVBar emite facturas numeradas y genera su registro de facturación encadenado; el envío a la AEAT (Fase 8, abajo) está
> implementado pero **nunca se ha ejecutado contra la AEAT** y el formato de la huella **no se ha contrastado con los ejemplos oficiales**.
> Valídelo con su asesor antes de facturar a clientes.
> Este documento describe el funcionamiento técnico; no es asesoramiento jurídico.

## Puesta en marcha

1. Un administrador abre **Facturación** (`/configuracion/facturacion`) y rellena nombre o razón social, NIF y domicilio del emisor y el
   límite de la factura simplificada (400 € por defecto). Sin esos datos no se puede emitir ninguna factura.
2. Al actualizar una instalación existente hay que aplicar la migración `Phase7Invoicing` y volver a ejecutar `scripts\db-grants.sql`
   (ya incluye las tablas nuevas). Los roles *Administrador* y *Encargado* reciben `IssueInvoices` y `RectifyInvoices`; *Caja* recibe `IssueInvoices`.

## Flujo

| Acción | Dónde | Qué hace |
|---|---|---|
| Factura simplificada (F2) | Cobro → comanda cobrada → «Emitir factura simplificada» | Solo si el total no supera el límite configurado. Sin datos del cliente. |
| Factura completa (F1) | Cobro → «Factura completa…» | Nombre, NIF/NIE/CIF y domicilio del cliente (el NIF se valida por formato y dígito de control). |
| Rectificativa (R5 de simplificada, R1 de completa) | Factura → «Rectificar» | Por diferencias, importes en negativo, de las cantidades indicadas. **No devuelve dinero.** |
| Anulación | Factura → «Anular» | Registro de anulación para una factura emitida por error y sin rectificativas. |
| Consultar / imprimir | Facturas → factura | Vista imprimible con desglose por IVA, QR, huella y estado de envío. |
| Informes | Facturas → exportar | Libro de facturas emitidas y resumen de IVA por tipo (CSV es-ES). |

Reglas: una factura vigente por comanda; la comanda debe estar **totalmente cobrada**; para pasar de simplificada a completa se rectifica la
simplificada y se emite la completa. Las series son `S` (simplificada), `F` (completa) y `R` (rectificativa) + año, con número correlativo de seis cifras
(`S2026-000001`).

## Garantías (probadas)

- Facturas, líneas y registros son **inmutables** (trigger en base de datos; el rol de la aplicación ni siquiera tiene UPDATE/DELETE/TRUNCATE sobre ellos).
- Numeración sin duplicados ni huecos, también con emisiones simultáneas (índice único + repetición automática).
- La cadena no se puede bifurcar (índices únicos) y `Comprobar integridad` detecta registros alterados, eliminados o reordenados.
- Una factura no puede emitirse dos veces para la misma comanda, ni por doble clic ni por reintento (clave de idempotencia).
- Los totales de la factura deben coincidir con los de la comanda; base + IVA = total en cada línea y en cada factura.

## Lo que NO hace

- **El envío a la AEAT no se ha probado contra la AEAT** (ver «Envío a la AEAT»): viene desactivado y necesita certificado y pruebas en preproducción.
- **No contrasta la huella ni el QR con los ejemplos oficiales** (D-060). Con el envío desactivado el QR apunta a la URL de preproducción y las facturas no llevan la leyenda «VERI*FACTU».
- No implementa registro de eventos en el formato del reglamento, ni la declaración responsable del productor.
- Sin facturas F3 (sustitutivas), rectificación por sustitución, identificadores extranjeros ni descuentos.
- La anulación y la rectificación **no tocan la caja**: las devoluciones de efectivo siguen siendo una operación aparte.

## Pruebas realizadas

Pruebas automáticas de dominio, aplicación e integración contra PostgreSQL real (emisión, rectificaciones parciales y totales, anulación, cadena,
manipulación simulada, concurrencia, triggers, restricciones, permisos, páginas, QR, exportaciones y protección de fórmulas CSV), más una comprobación
manual de permisos con el rol restringido de la aplicación. **No** se ha probado contra la AEAT, ni con impresora real, ni revisado por un asesor.

---

## Envío a la AEAT (Fase 8)

> **No probado contra la AEAT.** El envío está implementado y verificado con los esquemas oficiales y con respuestas simuladas, pero **nunca se ha
> ejecutado contra los servidores de la AEAT** porque hace falta un certificado electrónico real. El primer envío real debe hacerse en **preproducción**.

### Qué hace

- Envía los registros de facturación (alta y anulación) al servicio `RegFactuSistemaFacturacion` (VERI*FACTU) por SOAP con **certificado de cliente**,
  en el orden de la cadena y por lotes consecutivos (hasta 100).
- Genera el XML a partir de los registros guardados (nunca recalcula huellas) y lo **valida contra los XSD oficiales** (embebidos en
  `src/TPVBar.Infrastructure/Billing/Schemas`) antes de enviar: un XML que no valida no sale.
- Un servicio en segundo plano lo hace solo; «Facturación → Enviar ahora» lo fuerza. Respeta la espera que indica la AEAT (`TiempoEsperaEnvio`).
- Estados por registro: *Pendiente*, *Aceptado*, *Aceptado con errores*, *Rechazado*; guarda el CSV (código seguro de verificación), el código de error y el mensaje.
- Fallos de comunicación (red, HTTP, SOAP Fault, certificado): el registro sigue pendiente y se reintenta con retroceso (1 a 30 min).
- **Un rechazo detiene la cola** para no romper la cadena; hay que revisarlo (Facturación → «Registros que requieren atención») y, si procede, «Reintentar».
- Las facturas llevan la leyenda «VERI*FACTU · Factura verificable en la sede electrónica de la AEAT» y el QR de producción solo cuando el modo no es *Desactivado*.

### Configuración (solo en el servidor; por defecto DESACTIVADO)

En el archivo protegido del servidor (`TPVBAR_CONFIG_FILE`, p. ej. `C:\ProgramData\TPVBar\config\secrets.json`), sección `Billing:Aeat`:

| Clave | Valor |
|---|---|
| `Mode` | `Disabled` (por defecto), `PreProduction` (pruebas, sin efecto fiscal) o `Production` (**efecto fiscal real**) |
| `UseSealCertificate` | `true` para certificado de sello (puerto www10/prewww10); `false` para personal o de representante (www1/prewww1) |
| `CertificateThumbprint` + `CertificateStore` | Huella del certificado en el almacén de Windows (`LocalMachine`, por defecto, o `CurrentUser`). La cuenta del servicio debe poder leer la clave privada. |
| `CertificatePfxPath` + `CertificatePfxPassword` | Alternativa: archivo PFX y su contraseña (la contraseña solo en este archivo protegido) |
| `TimeoutSeconds`, `PollSeconds` | Tiempo máximo por envío (60) y frecuencia del servicio (30 s) |

Además, en **Facturación → Sistema informático** hay que indicar los datos del **productor** del software (nombre, NIF, identificador de 2 caracteres):
se declaran en cada registro y deben coincidir con la declaración responsable. Sin ellos no se envía nada.

### Cómo probar en preproducción

1. Obtener un certificado válido para el obligado tributario (o representante) y configurarlo con `Mode = PreProduction`.
2. Completar «Sistema informático» y los datos del emisor (el NIF debe estar dado de alta en el entorno de pruebas de la AEAT).
3. Emitir una factura de prueba y pulsar «Enviar ahora». Comprobar el CSV y el estado en la factura.
4. **Contrastar la huella**: si la AEAT rechaza por huella, el formato de `BillingHash` (D-060) debe corregirse con el documento oficial y los ejemplos.
5. Solo después valorar `Production`, con el asesor, y tras revisar la declaración responsable.

### Limitaciones del envío

- Sin pruebas reales con la AEAT; el modo `Production` envía datos fiscales reales.
- Rechazos: «Reenviar corregido / Subsanar» genera un registro nuevo con `Subsanacion`/`RechazoPrevio` (D-069), pero la combinación exacta y el encadenamiento tras un rechazo **no están contrastados con la AEAT**; la decisión de subsanar la toma una persona.
- Sin consulta de facturas en la AEAT, ni firma electrónica de los registros (`ds:Signature` es opcional en VERI*FACTU), ni representante en la cabecera.
- La descripción de la operación es fija («Servicios de hostelería: venta de comidas y bebidas»), régimen general (IVA, clave 01, operación sujeta no exenta S1).
- Solo identificadores españoles de destinatario.

Guía paso a paso, comprobación previa (`scripts\check-aeat-preproduccion.ps1`) y hoja de resultados del primer envío: [`pruebas-preproduccion-aeat.md`](pruebas-preproduccion-aeat.md).
