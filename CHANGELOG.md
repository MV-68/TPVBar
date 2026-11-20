# Registro de cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/). Versionado semántico.

## [Sin publicar]

### Cambiado (desarrollo)
- El PostgreSQL de desarrollo pasa del puerto **5433 al 5434**: el 5433 lo usa el servicio `miFactura-PostgreSQL` de otra aplicación, que arranca con Windows, y TPVBar acababa conectándose a él (fallo de contraseña de `tpvbar_app`). `scripts\dev-db.ps1` reinicia el clúster si estaba en otro puerto, mantiene el puerto de `dev-credentials.json` (lo usa `demo.ps1`) y se detiene si el puerto está ocupado por otro programa (D-071).

### Añadido (rol Comandero)
- **Rol «Comandero»** con un único permiso, tomar comandas (`TakeOrders`), pensado para móviles: ve solo «Mesas», abre mesas, añade líneas, envía a cocina/barra y pide la cuenta; **no cobra**, no anula líneas enviadas ni reabre mesas. Se crea solo al arrancar, también en bases de datos existentes.
- Comando `TPVBar.Web --create-user <usuario> --role <rol> [--name "<nombre visible>"]` (contraseña por consola o `TPVBAR_USER_PASSWORD`) para dar de alta usuarios de cualquier rol. Usuario de prueba `dev.comandero` en desarrollo.
- **Barra superior en móviles** (menos de 700 px de ancho): marca, usuario y «Cerrar sesión» arriba; el menú en su propia fila, que se desplaza en horizontal si no cabe; los avisos debajo. Ya no se sale de la pantalla.
- **Barra superior en tablet y escritorio:** si no cabe en una fila, ya no se aprieta (enlaces apilados en vertical, avisos partidos); los avisos, el usuario y «Cerrar sesión» bajan juntos a una segunda fila. Con menú largo (encargado, administrador) el menú ocupa su propia fila a todo el ancho: en tablet la barra del administrador pasa de unos 670 a 256 px de alto. Avisos, usuario y «Cerrar sesión» quedan agrupados en `MainLayout` (`.topbar__end`).
- **Limitaciones:** la restricción es por usuario, no por dispositivo; se ha comprobado en el navegador con tamaño de móvil (375 px), no en un teléfono real (D-070).

### Añadido (entorno demo)
- `scripts\demo.ps1` y comando `TPVBar.Web --seed-demo`: base `tpvbar_demo` con historial ficticio (3 turnos cerrados, devolución, turno abierto con mesas en uso). Solo fuera de producción. Ver [`docs/operations/demo.md`](docs/operations/demo.md).

### Añadido (Fase 9: subsanación de registros)
- **«Reenviar corregido» / «Subsanar»** (Facturación → registros que requieren atención): genera un registro nuevo para la misma factura (nueva huella, al final de la cadena) con los indicadores `Subsanacion` / `RechazoPrevio` del esquema oficial y deja el original como «Sustituido»; cubre altas rechazadas, altas aceptadas con errores y anulaciones rechazadas. Un registro nunca se modifica ni se borra; la cadena sigue siendo verificable.
- El XML de subsanación se valida contra los XSD oficiales (pruebas con base de datos real). La cola salta los registros sustituidos.
- **Limitaciones:** la combinación de indicadores y el encadenamiento tras un rechazo no se han contrastado con la AEAT (D-069).

### Añadido (Fase 8: envío a la AEAT)
- **Envío de registros de facturación a la AEAT** (VERI*FACTU, `RegFactuSistemaFacturacion`) por SOAP con certificado de cliente: servicio en segundo plano y botón «Enviar ahora», cola ordenada por cadena, espera de la AEAT, reintentos con retroceso, estados Pendiente/Aceptado/Aceptado con errores/Rechazado con CSV y mensajes; un rechazo detiene la cola hasta revisarlo.
- Generación del XML desde los registros guardados y **validación contra los XSD oficiales** de la AEAT (embebidos) antes de cada envío.
- Pantalla Facturación: datos del productor del sistema informático, estado del envío, registros que requieren atención y «Reintentar». Leyenda «VERI*FACTU» y QR de producción cuando el envío está activo.
- Configuración `Billing:Aeat:*` solo en el servidor; **desactivado por defecto**.
- **Limitaciones:** nunca se ha ejecutado contra los servidores de la AEAT (falta certificado real); la huella no se ha contrastado con los ejemplos oficiales; sin subsanación automática de rechazos. Ver D-065..D-068 y `docs/operations/facturacion.md`.

### Añadido (Fase 7: facturación)
- **Facturas** simplificada (F2), completa (F1) y rectificativa por diferencias (R1/R5), emitidas desde la pantalla de cobro de una comanda cobrada; series por tipo y año con numeración correlativa; emisor y cliente congelados en cada factura; validación de NIF/NIE/CIF.
- **Registros de facturación** (alta y anulación) encadenados con huella SHA-256 y **código QR** de cotejo (URL de preproducción). Comprobación de integridad de la cadena (Facturación → «Comprobar integridad»).
- **Anulación** de facturas emitidas por error (con registro de anulación) y **rectificación** parcial o total con control de cantidades.
- Pantallas `/facturas`, `/facturas/{id}` (imprimible, con QR) y `/configuracion/facturacion` (datos del emisor, límite de simplificada, estado, verificación). Permisos nuevos `IssueInvoices` y `RectifyInvoices`.
- **Informes para el asesor** (CSV): libro de facturas emitidas y resumen de IVA por tipo, calculados sobre las facturas.
- Base de datos: tablas `invoices`, `invoice_lines`, `billing_records` (inmutables por trigger y sin UPDATE/DELETE para el rol de la aplicación), `billing_chain_head`, `billing_submissions`.
- **Limitaciones:** el envío a la AEAT (VERI*FACTU) **no está implementado**; todos los registros quedan «Pendiente». El formato de huella y QR no se ha contrastado con los ejemplos oficiales. Sin validación fiscal. Ver D-059..D-064 y `docs/operations/facturacion.md`.

### Añadido (Fase 6: preparación de producción)
- **Pantalla Diagnóstico** (`/configuracion/diagnostico`, solo administrador): versión, PostgreSQL (estado, versión, latencia, migraciones), pantallas conectadas, agente de impresión, trabajos en cola/fallidos, último error de impresión y estado de la última copia, con avisos. Sin secretos ni datos de clientes.
- **Salud:** `/health` (vivo) y `/health/ready` (PostgreSQL y esquema al día; 503 si no), anónimos y sin detalles.
- **Roles de base de datos de mínimos privilegios** (`scripts\db-roles.sql`, `db-grants.sql`): propietario (migraciones), aplicación (sin DDL; solo lee/inserta en los registros inmutables) y copias (solo lectura). `--migrate` usa `ConnectionStrings:TpvBarMigrations` si existe.
- **Seguridad HTTP:** cabeceras (`nosniff`, `X-Frame-Options: DENY`, CSP, `Referrer-Policy`, `Permissions-Policy`), cookie `Secure` con `Security:RequireHttps`, claves de sesión persistentes y cifradas (DPAPI), configuración protegida por archivo (`TPVBAR_CONFIG_FILE`).
- **Servicio de Windows** (`TPVBar.Web`) y **registro estructurado** JSON en archivo diario (`Logging:File:Directory`).
- **Scripts:** `publish.ps1`, `install-server.ps1`, `update-server.ps1` (copia previa y comprobación), `install-print-agent.ps1`, `new-lan-certificate.ps1`, `backup.ps1` (volcado verificado, SHA-256, duplicado, retención, estado JSON) y `restore.ps1` (prueba en base temporal o recuperación, con comprobación contable).
- Documentación: `docs/operations/produccion.md`, `copias-y-restauracion.md`, `pruebas-en-entorno-real.md` y `docs/legal/revision-fiscal-y-legal.md`.
- 483 pruebas en total (123 dominio, 182 aplicación, 178 integración con PostgreSQL real). Probado a mano además: roles y permisos denegados, copia/restauración con archivo alterado, retención, fallo de copia y arranque del paquete publicado en modo Producción con el rol restringido.

### Cambiado
- La página de inicio ya no dice «Fase 1 en curso»: describe el estado real y que no es un sistema de facturación.
- Se fija `Microsoft.EntityFrameworkCore.Relational` 10.0.15 (había una mezcla 10.0.4/10.0.15).
- EF Core registra solo avisos (no cada consulta SQL).

### Limitaciones (Fase 6)
- **Los scripts de instalación no se han ejecutado de punta a punta en un Windows de producción** (solo sintaxis y sus piezas por separado).
- **Sin pruebas con tablets, dispositivos ni impresoras reales.** Sin revisión de seguridad externa.
- **Revisión fiscal y legal sin hacer: no hay facturas, registros de facturación ni VERI*FACTU. No usar para cobrar a clientes sin ella.**
- CSP con `script-src 'unsafe-inline'` (Blazor), copias sin cifrar, sin alertas automáticas ni segundo servidor.

### Añadido (Fase 5: almacén e informes)
- **Almacén** (`/almacen`): control de stock por unidades (mínimo y unidad en la ficha del producto), entradas, salidas y ajustes a recuento con motivo, historial por producto y estados Correcto / Stock bajo / Agotado / Negativo.
- **Política de stock**: se **descuenta al enviar** a cocina o barra y se **repone al anular** una línea enviada; quitar una línea sin enviar no mueve stock. Sin existencias suficientes el envío se rechaza entero; el administrador puede permitir stock negativo (auditado).
- El stock es **siempre la suma de sus movimientos**: inmutables (trigger), con saldo resultante y únicos por línea (un reintento no descuenta dos veces). Concurrencia resuelta con `xmin`.
- **Avisos de reposición**: indicador «Stock bajo: N» en la barra superior, evento `StockAlertRaised` y sección en Estadísticas.
- **Estadísticas** (`/estadisticas`): ventas por día/semana/mes y por franja horaria, ticket medio, cobros por método, productos y categorías, anulaciones y devoluciones, diferencias de caja y stock bajo, con **días y horas locales** y definiciones explícitas (ventas ≠ cobros).
- **Exportación CSV** (ventas por periodo, por producto y cobros por método): formato español con BOM, protección frente a inyección de fórmulas, sesión y permiso `ViewReports`, y auditoría de cada descarga.
- Ajustes generales (`app_settings`). Existencias de demostración solo en desarrollo.
- 464 pruebas en total (123 dominio, 182 aplicación, 159 integración con PostgreSQL real: 12 terminales sobre 10 unidades de stock, envíos simultáneos con negativo permitido, informes contra la base de datos, exportación HTTP y permisos).

### Limitaciones (Fase 5)
- **XLSX no está implementado** (pendiente de aprobación) ni la exportación a un programa contable.
- Sin descuentos todavía, sin costes ni márgenes, sin compras/proveedores, lotes ni recuento completo de inventario.
- Los informes agregan en memoria (suficiente para un bar).

### Añadido (Fase 4: caja y pagos)
- **Cobro** (`/cobro/{comanda}`): método, importe, efectivo entregado y cambio, referencia opcional/obligatoria y confirmación previa. Cobro total, parcial y **dividido entre varios métodos**; el saldo y los cobros se ven en pantalla. Al saldar, la comanda queda cobrada y la mesa libre.
- **Caja** (`/caja`): apertura con fondo inicial, entradas/salidas manuales, cobros por método, devoluciones y **cierre con recuento a ciegas** (esperado, contado y diferencia calculados en servidor). Una sola caja abierta a la vez.
- **Devoluciones** como operación compensatoria (mismo método, motivo obligatorio, permiso `PerformRefunds`); el cobro original no se modifica.
- **Dinero inmutable**: `payments`, `payment_refunds` y `cash_movements` rechazan `UPDATE`/`DELETE`/`TRUNCATE` por trigger. `orders.paid_total` con CHECK de coherencia.
- **Justificante de pago** con desglose de IVA, pagos y cambio (leyenda «No es una factura ni un documento fiscal») y **resumen interno de cierre de caja**, ambos por el agente de impresión; reimpresión auditada.
- **Métodos de pago configurables** (`/configuracion/metodos-pago`): Efectivo, Tarjeta y Otros de serie. «Tarjeta» es un registro manual, sin integración con datáfono.
- Indicador de caja (abierta/cerrada) en la barra superior; permiso nuevo `TakePayments` (migración de datos para roles existentes).
- Idempotencia (clave conservada hasta confirmar), concurrencia entre terminales y entre cierre y cobros resueltas y probadas con PostgreSQL. 371 pruebas en total (112 dominio, 126 aplicación, 133 integración).

### Corregido
- El host web de las pruebas arrancaba de forma diferida y podía omitir el seed de demostración según el orden de ejecución.

### Limitaciones (Fase 4)
- **No hay integración con datáfonos ni cajón automático.** El justificante no es una factura: sin cumplimiento fiscal.
- Sin propinas, cobro por producto/persona, varias cajas ni umbral de tolerancia en el cierre.

### Añadido (Fase 3: cocina e impresión)
- **Pantalla de cocina** (`/cocina`): tres columnas (Pendiente / Preparando / Preparado), filtro cocina/barra, antigüedad con aviso de retraso (texto, no solo color), líneas anuladas tachadas con «ANULADO» y aviso sonoro opcional. Cada cambio de estado se refleja en la comanda y queda auditado con usuario y hora.
- **Cola de impresión persistente** en PostgreSQL, creada en la misma transacción que el envío; reintentos con espera incremental (5 s → 5 min, 5 intentos), registro de cada intento y del último error, trabajos fallidos sin bloquear a los demás, reintento/cancelación/reimpresión manual auditados y clave de deduplicación.
- **Agente de impresión** (`TPVBar.PrintAgent`, servicio de Windows): render ESC/POS (CP858, 80/58 mm) y envío por cola de Windows, red (TCP) o archivo simulado. Habla con el servidor por una API con clave; no accede a la base de datos.
- **Nivel de entrega explícito**: nunca se afirma «impreso» sin confirmación del hardware; las impresoras simuladas se marcan como tales.
- **SignalR** (`/hubs/operations`, con grupos por permiso) y bus en proceso para las pantallas; los avisos llevan solo identificadores y se publican tras confirmar la transacción. Mesas y comanda se actualizan en tiempo real.
- **Pantalla Impresoras** (`/configuracion/impresoras`): alta/edición, prueba de impresión, destino de cada ticket, estado del agente y cola; **indicador de impresión** en la barra superior.
- La anulación de una línea enviada llega al ticket de cocina e imprime un aviso. Pre-cuenta (no fiscal) al pedir la cuenta.
- Permisos `UseKitchen` y `ManagePrinting` (migración de datos para roles existentes). Nuevo proyecto `TPVBar.PrintContracts`.
- `scripts/dev-db.ps1` crea también la clave de la API del agente. 278 pruebas en total (87 dominio, 84 aplicación, 107 integración).

### Corregido
- La API del agente devolvía 400 en lugar de 401 si la petición traía un cuerpo roto o pasaba por las páginas de error: ahora la clave se comprueba antes de leer el cuerpo.
- El render de la pre-cuenta perdía la alineación de importes al ajustar líneas.

### Limitaciones (Fase 3)
- **La impresión por Windows y por red no se ha probado con impresoras físicas.** USB directo no está implementado.
- Sin confirmación de papel (falta de papel, tapa abierta) y sin instalador del agente (Fase 6).

### Añadido (Fase 2: mesas, productos y comandas)
- Catálogo: zonas, mesas, categorías, tipos impositivos con vigencia y productos (precio con IVA incluido, destino cocina/barra).
- Comanda: apertura por mesa (una viva por mesa), líneas con fotografía de nombre/precio/impuesto, observaciones, cantidades, quitar, anular con permiso y motivo, pedir la cuenta, reabrir y cancelar.
- Totales calculados siempre en servidor (base, IVA y total que cuadran al céntimo) y comprobados por restricciones en PostgreSQL.
- Envío a cocina/barra: un ticket de preparación por destino, en una transacción; el doble clic o el reintento no duplican (claves de idempotencia).
- Pantallas en español, táctiles y responsive: **Mesas** (plano con estados libre/ocupada/pendiente de cobro, texto + color), **Comanda**, **Productos** (con categorías), **Zonas y mesas** y **Tipos impositivos**. Menú según permisos.
- Permisos nuevos `ManageCatalog` y `TakeOrders` (con migración de datos para roles existentes).
- `UseCaseRunner`: un ámbito de servicios por acción en Blazor Server, con reintento automático de conflictos de concurrencia.
- Datos de demostración **solo en desarrollo**: 3 zonas, 14 mesas, 6 categorías, 18 productos ficticios y 3 tipos impositivos de ejemplo (no validados fiscalmente).
- Formatos es-ES (moneda EUR, fechas) y estrategia horaria UTC + Europe/Madrid.
- 180 pruebas en total (67 dominio, 57 aplicación, 56 integración con PostgreSQL real: 10 terminales abriendo la misma mesa, 6 añadiendo a la vez sin perder unidades, envíos simultáneos, restricciones de base de datos y permisos de cada pantalla).

### Limitaciones (Fase 2)
- Sin cobro (Fase 4), sin pantalla de cocina ni impresión (Fase 3) y sin stock (Fase 5).
- Las pantallas se actualizan por sondeo cada 8–10 s; SignalR llega en la Fase 3.

### Añadido (Fase 1: fundamentos)
- Dominio: `AppUser` (bloqueo por intentos, concurrencia optimista), `Role`, permisos configurables, roles iniciales y `AuditEvent` inmutable.
- Aplicación: `LoginService`, `UserAdministrationService`, política de contraseñas y abstracciones de persistencia.
- Infraestructura: `TpvBarDbContext` (EF Core 10 + Npgsql, esquema `tpv`, `snake_case`), migración `InitialCreate` con trigger de inmutabilidad de la auditoría, hasher de ASP.NET Core Identity y seeders.
- Web: inicio y cierre de sesión reales (cookie), autorización por permisos, límite de intentos por IP, identificador de correlación, layout con usuario y botón de salida, páginas en español.
- Comandos de administración: `--migrate` y `--create-admin <usuario>`.
- `scripts/dev-db.ps1`: PostgreSQL 18 local de desarrollo (puerto 5433) con secretos en User Secrets.
- Herramienta local `dotnet-ef` (`dotnet-tools.json`) y script SQL revisable en `docs/database`.
- 80 pruebas: dominio, aplicación (con dobles) e integración contra PostgreSQL real (migraciones, seed idempotente, concurrencia, inmutabilidad de la auditoría, flujo HTTP completo).

### Cambiado
- La página de inicio y los errores exigen o no sesión según corresponda (política de reserva: todo requiere sesión salvo lo marcado).

### Limitaciones
- Sin gestión de usuarios desde la interfaz, sin cambio de contraseña, sin PIN y sin revalidación periódica de la sesión.
- Todavía no hay mesas, comandas, cobros ni impresión.

## [0.1.0] - 2026-11-16 · Fase 0 (preparación)

### Añadido
- Solución `TPVBar.sln` con proyectos `Domain`, `Application`, `Infrastructure`, `Web` (Blazor Web App, Interactive Server) y `PrintAgent` (worker).
- Proyectos de prueba `Domain.Tests`, `Application.Tests` e `Integration.Tests` (xUnit v3).
- Pruebas de arquitectura que vigilan la regla de dependencias entre capas.
- Gestión central de paquetes, análisis estático con avisos como errores, `.editorconfig`, `nuget.config` y `global.json`.
- Documentación base: `README.md`, `DECISIONS.md` y este registro.
