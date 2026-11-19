# Decisiones técnicas

Estado: Fase 0 · Última actualización: 2026-11-16

## Decisiones tomadas

### D-001 · Monolito modular en capas
Domain ← Application ← Infrastructure; Web compone; PrintAgent aparte. Sin microservicios. Una prueba de arquitectura vigila las dependencias de Domain y Application.

### D-002 · Línea base .NET 10 LTS, C# 14
Verificado el 2026-11-16 en la política oficial de soporte: .NET 10 es LTS (publicado 2025-11-11, soporte hasta 2028-11-14); .NET 11 es STS, publicado 2026-11-10 (6 días antes).
El equipo tiene SDK 11.0.100 instalado, que compila `net10.0` sin problema. `global.json` fija SDK ≥10.0.100 con `rollForward: latestMajor`.
**Descartado:** .NET 11 (STS, recién publicado, 2 años de soporte frente a 3). Revisar al aproximarse el fin de soporte de .NET 10.

### D-003 · EF Core 10.0.15 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 + PostgreSQL 18
EF Core 10 apunta a .NET 10 (soporte hasta 2028-11-10). Existe EF Core 11.0.0 en NuGet, pero Npgsql solo publica 10.0.x estable, por lo que **no se debe subir EF Core a 11 de forma aislada**. PostgreSQL 18 es la rama soportada (final 2030-11-14). Paquetes de la familia EF/ASP.NET se actualizan en bloque (`Directory.Packages.props`).

### D-004 · Pruebas con xUnit v3 sobre Microsoft.Testing.Platform
`dotnet test` en SDK 10+ exige activar el runner en `global.json`. Las pruebas de integración usarán PostgreSQL real (no SQLite), como exige el prompt maestro.

### D-005 · Calidad de compilación
Avisos tratados como errores, `AnalysisMode=Recommended`, estilo forzado en compilación, versiones centralizadas.

### D-006 · Reglas transversales (recordatorio para fases siguientes)
Importes en `decimal`; instantes en UTC/`DateTimeOffset` con zona `Europe/Madrid` solo para presentación; nada confirmado se borra (anulaciones y compensaciones); idempotencia en pagos y envíos.

### D-007 · `nuget.config` propio con origen único
El entorno del desarrollador tiene un segundo origen privado; con gestión central de paquetes provoca NU1507. El repositorio fija nuget.org con mapeo de origen para que la compilación sea reproducible.

## Cuestiones pendientes (no bloquean)

1. **PostgreSQL local:** existe PostgreSQL 18.2 instalado, pero su único servicio (`miFactura-PostgreSQL`, datos en `D:\MIFACTURA`) pertenece a otra aplicación y está parado. **No se reutiliza ni se toca.** Para la Fase 1 hace falta un clúster propio de TPVBar (otro puerto, p. ej. 5433, y otro directorio de datos) o una instalación dedicada. Decisión para la persona propietaria.
2. Docker no está instalado; las pruebas de integración con PostgreSQL usarán el clúster local de desarrollo en lugar de Testcontainers, salvo que se instale Docker.
3. Preguntas del apartado 18 del prompt maestro (hardware del servidor, modelos de impresora, cuentas divididas, propinas, turnos, terminal bancario, régimen fiscal…): se resolverán con valores por defecto documentados al llegar a cada fase.
4. Análisis fiscal (RD 1007/2023, VERI*FACTU): pendiente, `docs/legal/`. TPVBar no es apto fiscalmente.
5. Estrategia PWA: evaluar por separado; no se promete trabajo sin conexión al servidor.

## Fase 1 (2026-11-16)

### D-008 · PostgreSQL de desarrollo propio
Se creó un clúster independiente (puerto 5433, datos en `.pgdata`, log en `.pglog`) con `scripts/dev-db.ps1`. El servicio `miFactura-PostgreSQL` no se toca. Resuelve la cuestión pendiente 1 de la Fase 0 **para desarrollo**; el servidor de producción sigue sin decidir.

### D-009 · Usuarios propios + hasher de Identity, sin las tablas de Identity
Se usa `PasswordHasher<TUser>` de ASP.NET Core Identity (PBKDF2-HMAC-SHA512, sal por contraseña, versionado, rehash automático) sobre una entidad `AppUser` propia. **Descartado:** las tablas y la UI de Identity completas (claims, tokens, 2FA, proveedores externos que un TPV local no necesita) y bcrypt/Argon2 de terceros (dependencia adicional sin necesidad demostrada).

### D-010 · Autenticación por cookie y autorización por permisos
Cookie HttpOnly, SameSite=Strict, de sesión (no persistente). Inactividad de 30 min deslizante y caducidad absoluta de 12 h. Los permisos viajan como claims y cada uno tiene una política (`Policies.For(permiso)`). Política de reserva: todo exige sesión salvo `[AllowAnonymous]`. El login es una página **estática** (no interactiva) porque la cookie solo puede escribirse en una petición HTTP normal.

### D-011 · Bloqueo de cuenta y anti-enumeración
5 intentos fallidos bloquean 15 min (`LockoutPolicy`, lógica en el dominio). Durante el bloqueo no se comprueba la contraseña. Usuario inexistente, contraseña errónea y cuenta inactiva dan la misma respuesta y un coste equivalente (verificación contra hash ficticio). Límite adicional por IP en `/auth/login` (10/min, configurable). Los intentos simultáneos se reintentan con concurrencia optimista (`xmin`) para no perder incrementos.

### D-012 · Auditoría inmutable por trigger
`audit_events` rechaza `UPDATE`/`DELETE`/`TRUNCATE` en la propia base de datos. La auditoría de login se guarda en una operación aparte tras actualizar el estado del usuario (no en la misma transacción): si fallara su escritura, el estado de bloqueo ya está a salvo. Los detalles nunca incluyen contraseñas ni hashes (hay pruebas).

### D-013 · Roles y permisos iniciales
Cinco roles (Administrador, Encargado, Camarero, Cocina, Caja) con una matriz por defecto en `DefaultRoles`. Los permisos se asignan solo al **crear** el rol: lo que el encargado cambie después no se revierte al reiniciar (hay prueba). La matriz concreta es una **propuesta pendiente de validar** con el propietario.

### D-014 · Migraciones en producción
En Desarrollo se aplican solas al arrancar. En otros entornos solo con `--migrate` (y copia de seguridad previa, Fase 6). La app en producción exige el esquema ya migrado.

## Cuestiones pendientes (actualizado)

- **Revalidación de sesión:** un usuario desactivado conserva su sesión hasta que caduque (máx. 30 min de inactividad). Falta revalidar contra la base de datos periódicamente; pendiente para cuando exista la pantalla de gestión de usuarios (Fase 2/6).
- **Cambio de contraseña, desactivación y gestión de roles desde la interfaz:** no existen todavía; solo alta por CLI (`--create-admin`) y seed de desarrollo.
- **PIN rápido para tablets** (sección 9.2, "usuario, PIN o credenciales"): no implementado; decidir si se quiere y con qué política antes de la Fase 2.
- **HTTPS en la LAN:** la cookie usa `SecurePolicy=SameAsRequest`. En producción debe servirse por HTTPS (Fase 6).
- **Puerto del servidor de producción y selección de puesto en el login:** sin decidir.
- **Estilo de código:** CA1711 desactivado (falso positivo con `Permission`); migraciones tratadas como código generado.

## Fase 2 (2026-11-16)

Valores por defecto asumidos (el propietario puede cambiarlos; ninguno cierra puertas):

### D-015 · Precios con IVA incluido
`SalePrice` y `UnitPrice` son PVP con IVA. Base e impuesto se derivan por línea (`Money.SplitGross`): la base se redondea a 2 decimales (mitad hacia arriba) y el impuesto es la diferencia, de modo que base + impuesto = importe exactamente (hay prueba con miles de importes). Los totales de la comanda suman líneas activas y se recalculan siempre en servidor. Un CHECK en PostgreSQL exige `grand_total = subtotal + tax_total`.

### D-016 · Tipos impositivos configurables con vigencia
`tax_rates` con porcentaje y fechas de vigencia; nada fijo en código. La línea guarda el porcentaje vigente al vender. "Hoy" se decide en Europe/Madrid (`BusinessTime`). **Los tipos del seed son de demostración (solo desarrollo) y no están validados fiscalmente.** En producción hay que crearlos en «Tipos impositivos»; sin ellos no se puede vender (el producto se rechaza con un aviso claro).

### D-017 · Estrategia horaria
Instantes en UTC (`timestamptz`, `DateTimeOffset` con offset 0). La zona Europe/Madrid solo se usa para "qué día es" en reglas de negocio y para mostrar horas.

### D-018 · Una comanda viva por mesa, garantizada por la base de datos
Índice único parcial (`status IN ('Open','PendingPayment')`). Abrir a la vez desde varias terminales devuelve la misma comanda (hay prueba con 10 en paralelo). Sin cuentas divididas, sin varias comandas por mesa y sin unir/separar mesas (cuestiones 8–10 del apartado 18: pendientes de confirmar).

### D-019 · Destinos de preparación: Cocina y Barra
`PreparationStation` es un enum guardado como texto, no una tabla. Un envío crea un ticket por destino. Si se quiere más de dos destinos o configurarlos, pasa a ser la entidad `KitchenStation` del apartado 6.1 (migración de datos sencilla). Cuestión 7 (bebidas en barra, comida en cocina): se asume que sí.

### D-020 · Líneas: quitar vs. anular
Una línea sin enviar se **quita** (no hay nada confirmado). Una ya enviada solo se **anula** con permiso `VoidLines`, motivo obligatorio y auditoría; se conserva y deja de sumar. La comanda solo se cancela si no tiene nada enviado vivo.

### D-021 · Concurrencia e idempotencia
Comanda con `xmin` como token. Todas las operaciones que la modifican "tocan" la comanda, así que dos terminales no pueden pisarse: la segunda recibe conflicto y el host la repite con datos frescos (`UseCaseRunner`). Añadir línea y enviar a cocina llevan clave de idempotencia (`idempotency_records`, índice único) guardada en la misma transacción: doble clic o reintento no duplican. El envío de lo mismo desde dos puestos produce un solo juego de tickets (prueba).

### D-022 · Un ámbito de servicios por acción en Blazor Server
Un circuito vive horas; reutilizar su `DbContext` acumularía datos obsoletos. `UseCaseRunner` crea un ámbito nuevo por acción con la identidad de la sesión (`CurrentUserAccessor`), y reintenta conflictos. Los errores de negocio se muestran en español; los inesperados se registran y se muestran genéricos.

### D-023 · Application sin EF Core
`IAppData` expone `IQueryable` y métodos asíncronos de ejecución; la infraestructura los implementa con EF Core. Así los casos de uso se prueban con datos en memoria y la capa no depende del ORM.

### D-024 · Nuevos permisos y migración de datos
`ManageCatalog` y `TakeOrders`. Como el seed solo asigna permisos al crear un rol, la migración `Phase2SalesCore` los añade con SQL a los roles ya existentes (en una base nueva no hace nada y el seed usa la matriz actual). Patrón a repetir cuando aparezcan más permisos.

### D-025 · Actualización de pantallas por sondeo (temporal)
Mesas (10 s) y comanda (8 s) se refrescan por sondeo. SignalR llega en la Fase 3.

## Cuestiones pendientes (Fase 2)

- **Sin cobro:** «Pedir la cuenta» deja la comanda pendiente de cobro; cobrar y cerrar llega en la Fase 4. Hasta entonces la mesa no se libera salvo cancelando una comanda sin enviar o reabriendo.
- **Sin pantalla de cocina ni impresión:** el envío ya crea los tickets en base de datos (Fase 3 los muestra e imprime).
- **Stock:** no hay campos de stock en `products` todavía (Fase 5; migración aditiva).
- **Modificadores** (extras/opciones): solo observaciones de texto libre por línea. `OrderLineModifier` queda para cuando se pida.
- **Historial de eventos de comanda** (`OrderEvent`): cubierto de momento por la auditoría; se añadirá si hace falta consultarlo en pantalla.
- **Auditoría de altas del catálogo** en una operación aparte tras guardar (necesita el id generado); las demás operaciones auditan en la misma transacción.
- **Usuario en `opened_by`/`created_by`:** sin clave foránea a `users` hasta decidir qué ocurre con usuarios dados de baja (se desactivan, no se borran).
- **Orden y límite de comensales:** al abrir mesa se asume 1 comensal (editable en el modelo, aún sin control en pantalla).
- **Categorías:** se pueden crear y editar pero no hay botón para borrarlas (se desactivan).

## Fase 3 (2026-11-16)

### D-026 · Cola de impresión persistente y agente separado sin acceso a la base de datos
El ticket de preparación y su trabajo de impresión se guardan en la **misma transacción** del envío. El agente (servicio de Windows) reclama trabajos por una API HTTP autenticada y notifica el resultado; solo conoce `TPVBar.PrintContracts`. **Descartado:** que el agente lea PostgreSQL directamente (obligaría a repartir credenciales de base de datos a cada puesto de impresión) y que el servidor imprima él mismo (el servidor web no tiene por qué ver las impresoras).

### D-027 · Arrendamiento (lease) y entrega al menos una vez
Un trabajo reclamado queda en «Imprimiendo» durante 2 min; si el agente cae, otro intento lo recupera. Es entrega **al menos una vez**: puede haber un duplicado cuando no se sabe si el papel salió. Se mitiga marcando el ticket «REIMPRESION / REINTENTO». Dos agentes reclamando a la vez nunca reciben el mismo trabajo (token de concurrencia `xmin`; hay prueba con 12 trabajos y 2 agentes). Reintentos con espera 5 s → 15 s → 45 s → 2 min → 5 min, máximo 5; después «Fallido», auditado y sin bloquear la cola.

### D-028 · Nunca se afirma «impreso» sin confirmación
Nivel de entrega explícito: Simulado / Enviado a la impresora / Aceptado por la cola de Windows / Confirmado (reservado; hoy ninguna conexión lo puede confirmar). La interfaz lo muestra tal cual.

### D-029 · Eventos de tiempo real como avisos sin datos, sin Outbox
Los eventos solo llevan identificadores y significan «vuelve a leer». Se publican **después** de confirmar la transacción (`PendingOperationEvents` + `UseCaseRunner`). Si se pierde uno (caída entre el commit y la publicación, reconexión), la pantalla se pone al día en la siguiente lectura (refresco de respaldo cada 30 s y relectura al reconectar). Como no transportan estado, repetirlos no puede duplicar pedidos. Por eso **no se implementa Outbox**: se justifica porque no hay eventos cuya pérdida corrompa datos. Si algún día un evento llevara estado, habría que añadirlo.

### D-030 · Blazor usa un bus en proceso; SignalR (`/hubs/operations`) para otros clientes
Un circuito de Blazor Server ya es una conexión SignalR: abrir otra desde cada página sería redundante. Las pantallas Blazor se suscriben a un bus en memoria; el hub, autenticado y con grupos por permiso (sala, cocina, impresión), queda para clientes que no son Blazor (otras pantallas, integraciones). Probado: sin sesión no conecta, cada rol recibe solo sus grupos y el payload solo lleva identificadores.

### D-031 · La anulación llega a cocina
Anular una línea ya enviada marca su línea en el ticket (y cancela el ticket si era la única), imprime un aviso «ANULADO» y las pantallas se actualizan. Una anulación y un avance de cocina simultáneos nunca resucitan la línea anulada (la comanda se «toca» en ambos casos; prueba con PostgreSQL).

### D-032 · Permisos nuevos y rutas de impresión
`UseKitchen` (Cocina, Encargado, Administrador) y `ManagePrinting` (Encargado, Administrador), con migración de datos para roles existentes. Una ruta por destino (cocina/barra → impresora); por defecto barra comparte la impresora de cocina (cuestión 7 pendiente).

### D-033 · La API del agente: clave obligatoria, sin valor por defecto
`X-Agent-Key` comparada en tiempo constante en un middleware **antes** de leer el cuerpo (401 siempre, nunca 400). Sin clave configurada, 503. No usa cookies ni antiforgery. Las páginas de error (`/not-found`) no se aplican a `/api` ni `/hubs`.

## Cuestiones pendientes (Fase 3)

- **Hardware real:** la impresión por Windows (RAW/winspool) y por red están implementadas pero **no probadas con impresoras físicas**. Impresión por USB directo: no implementada. Hay que probar modelos concretos (página de códigos, corte, ancho).
- **Documento de cuenta:** la pre-cuenta se imprime al pedir la cuenta y **no es documento fiscal**; el justificante de pago llega con la Fase 4 y el análisis legal.
- **Seguridad de la API del agente:** clave compartida sin caducidad ni rotación automática; restringida a la LAN solo por cortafuegos (documentado). En producción valorar HTTPS entre agente y servidor.
- **Sin confirmación de papel:** no se detecta falta de papel/tapa abierta (depende del modelo; requiere leer el estado ESC/POS `DLE EOT`).
- **Agente:** cachea impresoras 30 s; sin instalador (Fase 6); reintenta pero no tiene aún cola local propia si el servidor está caído (los trabajos esperan en el servidor, lo que no se imprime mientras no hay servidor).
- **Sonido de cocina:** pitido simple opcional; sin personalización de sonido ni por destino.
- **Identificación de la cocina:** la pantalla de cocina usa la sesión de un usuario con permiso (sin modo «pantalla fija» sin inicio de sesión); a valorar con el propietario.

## Fase 4 (2026-11-17)

Valores por defecto asumidos (el propietario puede cambiarlos; cuestiones 8, 11, 12 y 13 del apartado 18 siguen abiertas):

### D-034 · Una sola caja, cobro total o dividido por importe, sin propinas
Una sesión de caja abierta a la vez (índice único parcial). Una comanda se puede cobrar en varios pagos y con distintos métodos hasta saldar el total. **No** hay división por producto o por persona ni propinas. El control de turnos se reduce a la propia sesión de caja (quién abre y quién cierra).

### D-035 · El dinero registrado es inmutable
`payments`, `payment_refunds` y `cash_movements` rechazan `UPDATE`, `DELETE` y `TRUNCATE` por trigger en PostgreSQL. Las correcciones son operaciones compensatorias: una **devolución** (mismo método, motivo obligatorio, permiso `PerformRefunds`, imputada a la caja abierta en ese momento) o un movimiento inverso. Una devolución no reabre la comanda cobrada. (Nota: `TRUNCATE` de `payments` lo rechaza PostgreSQL antes por la clave foránea de las devoluciones; sigue rechazado.)

### D-036 · Saldo y bloqueo de la comanda con cobros
`orders.paid_total` (neto de devoluciones) se actualiza en la misma transacción que cada pago, con CHECK `0 ≤ paid_total ≤ grand_total`. El primer cobro bloquea la edición; no se puede cobrar más que el saldo; una comanda con cobros no se reabre ni se cancela (primero hay que devolver). Al llegar el saldo a cero la comanda queda «Cobrada» y la mesa libre.

### D-037 · Cobro: concurrencia e idempotencia
Dos terminales cobrando lo mismo a la vez: la comanda lleva `xmin`, la segunda recibe conflicto, se repite y ve el saldo ya saldado. Cada pulsación de «Cobrar» lleva una clave de idempotencia que se **conserva** hasta que el cobro se confirma (un reintento tras un corte de red nunca cobra dos veces). Confirmación explícita en pantalla antes de registrar cada cobro.

### D-038 · Cierre de caja a ciegas y sin carreras
El efectivo esperado lo calcula SIEMPRE el servidor: fondo inicial + cobros en efectivo (el importe aplicado, no lo entregado) − devoluciones en efectivo + entradas − salidas. Solo se revela al cerrar (recuento a ciegas). Cobros, devoluciones y movimientos «tocan» la sesión (`xmin`), de modo que un cierre y un cobro simultáneos no pueden cruzarse: o el cobro entra en el esperado o se rechaza por caja cerrada (hay prueba con PostgreSQL). No se puede cerrar con comandas a medio cobrar. «Usa el cajón» de un método no se puede cambiar si ya tiene cobros.

### D-039 · «Tarjeta» es un registro manual, no una integración bancaria
La pantalla de cobro lo dice expresamente. No se afirma haber cobrado una tarjeta: solo se anota. La integración con datáfonos sería un módulo aparte, sujeto a la documentación y certificación del proveedor (cuestión 13 pendiente).

### D-040 · Justificante de pago: NO es una factura
Al saldar se encola un justificante en la impresora de tickets con desglose de IVA por tipo, pagos, entregado y cambio, y la leyenda «No es una factura ni un documento fiscal». Sin numeración fiscal propia (usa el nº de comanda). Reimpresión con copia marcada «COPIA» y auditada. El resumen de cierre de caja también se imprime como documento interno. **El cumplimiento fiscal (series, facturas simplificadas/rectificativas, VERI*FACTU, RD 1007/2023) sigue sin analizar y TPVBar no es apto fiscalmente.**

### D-041 · Permisos
Nuevo `TakePayments` (Administrador, Encargado, Camarero, Caja). Abrir/cerrar caja y movimientos: `OpenCloseCash` (Administrador, Encargado, Caja). Devoluciones: `PerformRefunds` (Administrador, Encargado, Caja). Un camarero cobra pero no abre ni cierra la caja ni devuelve. Matriz pendiente de validar con el propietario. Los métodos de pago se administran con `ManageCatalog`.

### D-042 · Métodos de pago como datos de referencia
Efectivo, Tarjeta y Otros se crean por código en cualquier entorno (idempotente, sin pisar ediciones posteriores). Configurables: nombre, orden, «usa el cajón», «exige referencia», activo. El código no cambia nunca. Siempre queda al menos un método activo. La referencia del cobro (p. ej. nº de operación) no se copia a la auditoría, solo si existe.

## Cuestiones pendientes (Fase 4)

- **Cajón portamonedas:** no se abre automáticamente (no hay comando ESC p ni integración); el recuento es manual.
- **Cobro por producto o por persona, propinas, varias cajas/turnos:** no implementados (cuestiones 8, 11, 12).
- **Datáfono:** sin integración; la referencia es manual y opcional por método.
- **Documento fiscal:** ver D-040; el justificante no sustituye a una factura simplificada.
- **Devolución de una comanda ya cobrada** no genera documento rectificativo (análisis legal pendiente).
- **Informes:** el cierre resume cobros por método; las ventas por producto, franja, descuentos e impuestos llegan en la Fase 5.
- **Cierre con diferencia:** se registra y se muestra (faltan/sobran), pero no exige autorización de un encargado ni umbral de tolerancia.
- **Sesión abierta de un día para otro:** no se fuerza el cierre; a valorar (jornada contable).

## Fase 5 (2026-11-17)

Valores por defecto asumidos (la aprobación de XLSX y la cuestión 15, «exportar a un programa contable», siguen abiertas):

### D-043 · Política de stock: se descuenta al ENVIAR y se repone al anular
Es el momento en que el producto sale de barra o cocina, y no depende de si luego se cobra. Cada línea enviada genera un movimiento «Venta»; anular una línea enviada genera un movimiento inverso «Devolución por anulación». **Quitar una línea sin enviar no toca el stock** (nunca se descontó). La reposición al anular solo se hace si esa línea llegó a descontarse (existe su movimiento de venta): activar el control de stock después de vender no infla las existencias. Se asume que lo anulado vuelve al almacén; si el producto ya se preparó y no es reutilizable, el encargado lo registra como salida/merma.

### D-044 · El stock actual es siempre la suma de sus movimientos
Las existencias solo cambian mediante movimientos (Venta, Devolución por anulación, Entrada, Salida, Ajuste), nunca editando la ficha. Cada movimiento guarda el saldo resultante, es **inmutable** (trigger) y los ligados a una línea son **únicos por motivo y línea** (índice único): un reintento no puede descontar dos veces. Hay pruebas que comprueban `cantidad == Σ movimientos` tras operaciones concurrentes.

### D-045 · Stock negativo: no, salvo autorización expresa del administrador
Por defecto, si no hay existencias suficientes **todo el envío se rechaza** y no se guarda nada. Un ajuste general (`stock.allow_negative`) lo permite; solo lo cambia quien tiene `ManageConfiguration` (Administrador) y queda auditado. *Consecuencia a valorar:* con el rechazo activado, un descuadre de inventario puede impedir vender un producto que sí hay; para eso existe el ajuste a recuento (permiso `AdjustStock`) y la opción de permitir negativo.

### D-046 · Concurrencia del stock
El producto lleva `xmin` como token. Dos envíos simultáneos del mismo producto: el segundo recibe conflicto, se repite con datos frescos y ve el stock real. Probado con 12 terminales enviando una unidad con 10 de stock (salen exactamente 10) y con negativo permitido (no se pierde ningún descuento).

### D-047 · Alcance del stock
Solo por unidades de producto (sin recetas ni escandallos). Campos: controlado sí/no, mínimo, unidad. Aviso de reposición: indicador «Stock bajo: N» en la barra superior, listado en Almacén, evento `StockAlertRaised` (una sola vez al cruzar el mínimo) y sección en Estadísticas. Un producto inactivo no avisa.

### D-048 · Definiciones de los informes (no se mezclan magnitudes)
- **Ticket** = comanda cobrada cuyo cierre cae en el periodo. **Ventas** = suma de totales de tickets (IVA incluido, tras descuentos); **ventas brutas** = antes de descuentos (hoy no hay descuentos, así que coinciden).
- **Base imponible** e **IVA** desglosan las ventas. **Devoluciones** se cuentan por la fecha de la devolución y **no se desglosan por IVA** (no se sabe a qué líneas afectan). **Neto** = ventas − devoluciones.
- **Cobros por método** (dinero recibido y devuelto, por fecha del cobro) son OTRA magnitud: una cuenta cobrada en dos días reparte sus pagos y su ticket en días distintos. Ventas y cobros no se suman ni se comparan como si fueran lo mismo.
- **Anulaciones** por fecha de anulación (aunque la comanda no se cobre). **Diferencias de caja** por fecha de cierre.

### D-049 · Días y horas locales
Los informes agrupan por día y hora de Europe/Madrid, no UTC (un ticket a las 00:30 locales del día 17 cuenta el 17 aunque en UTC sea el 16). Rango máximo 366 días. Probado en la frontera de medianoche local.

### D-050 · Agregación en memoria
Los informes cargan los tickets del rango y agregan en C#. Suficiente para un bar (miles de tickets al mes); si creciera mucho habría que pasar la agregación a SQL.

### D-051 · Exportación CSV, sin XLSX
CSV para hoja de cálculo en español: separador «;», coma decimal, fechas `dd/MM/yyyy`, UTF-8 con BOM. Las celdas de texto se protegen frente a la **inyección de fórmulas** (si empiezan por `= + - @` se antepone `'`) y se escapan `;` y comillas. Descarga con sesión y permiso `ViewReports`, y **cada exportación queda auditada**. **XLSX no se implementa** (el prompt maestro lo condiciona a aprobación): pendiente de tu decisión.

## Cuestiones pendientes (Fase 5)

- **XLSX** y exportación a un programa contable (cuestión 15): sin aprobar.
- **Inventario físico:** hay ajuste a recuento, pero no un asistente de recuento completo de todos los productos a la vez.
- **Compras y proveedores, lotes/caducidades, mermas con categorías, coste de producto y márgenes:** no existen. Los informes son de venta, no de rentabilidad.
- **Descuentos:** el modelo ya los prevé (`discount_total`), pero no hay forma de aplicarlos todavía, así que «brutas» y «ventas» coinciden.
- **Informes pesados:** agregación en memoria (D-050).
- **Comparativas entre periodos y gráficos avanzados:** solo tablas con barras sencillas.

## Fase 6 (2026-11-17)

Preparación de producción. Valores por defecto asumidos; las cuestiones 1–6, 13, 14 y 16 del documento maestro siguen abiertas.

### D-052 · Tres roles de base de datos (mínimos privilegios)
`tpvbar_owner` (solo migraciones), `tpvbar_app` (solo datos; **sin** UPDATE/DELETE/TRUNCATE sobre cobros, devoluciones, movimientos de caja y de stock ni auditoría) y `tpvbar_backup` (`pg_read_all_data`). Las migraciones usan `ConnectionStrings:TpvBarMigrations` si existe; la aplicación nunca la lee. `scripts\db-grants.sql` se ejecuta tras cada migración. Probado: el rol de la aplicación no puede retirar triggers, alterar el esquema ni modificar los registros inmutables. *Consecuencia:* una tabla inmutable nueva en el futuro debe añadirse a mano a la lista de `REVOKE`. En desarrollo se sigue usando un único rol por sencillez.

### D-053 · Configuración protegida por archivo fuera del repositorio
Producción lee `TPVBAR_CONFIG_FILE` (`C:\ProgramData\TPVBar\config\secrets.json`, ACL solo SYSTEM/Administradores/servicio) con cadenas de conexión, clave del agente y contraseña del `.pfx`. Alternativas descartadas: variables de entorno del sistema (visibles a cualquier proceso del servicio y heredables) y DPAPI/Credential Manager (más seguro pero difícil de recuperar para quien administre el bar). Riesgo asumido: secretos en claro protegidos solo por permisos de archivo.

### D-054 · CSP con `script-src 'unsafe-inline'`
Blazor emite un *import map* en línea; con `script-src 'self'` los módulos de la interfaz no cargan (comprobado en el navegador). Se mantiene el resto de la política (`default-src 'self'`, `object-src 'none'`, `frame-ancestors 'none'`, `form-action 'self'`). Debilidad conocida frente a XSS, compensada por la codificación de Razor; endurecer con nonce cuando el framework lo permita. Las cabeceras se fijan en `OnStarting` para sustituir a las que añade el sistema antifalsificación (`X-Frame-Options: SAMEORIGIN`).

### D-055 · HTTPS obligatorio en producción, claves de sesión persistentes
`Security:RequireHttps=true` fuerza cookie `Secure`; sin certificado no se inicia sesión. Las claves de protección de datos se guardan en `DataProtection:KeysDirectory`, cifradas con DPAPI de la máquina: sin ellas, una cuenta de servicio sin perfil las perdería en cada reinicio y cerraría todas las sesiones. Certificado autofirmado para LAN (`new-lan-certificate.ps1`) o el del propietario.

### D-056 · Copias: volcado verificado + ubicación secundaria + estado en JSON
`pg_dump -Fc` con verificación `pg_restore --list`, huella SHA-256 y duplicado comprobado. El script escribe `backup-status.json`; la aplicación **solo lo lee** (no programa ni ejecuta copias): así la copia sigue funcionando aunque la aplicación esté caída. Alternativa descartada: ejecutar copias desde la aplicación. Restauración con prueba en base temporal y comprobación de coherencia contable (`restore.ps1`). Sin cifrado, sin WAL/PITR (RPO = intervalo entre copias).

### D-057 · Salud y diagnóstico
`/health` (vivo) y `/health/ready` (PostgreSQL + esquema al día; 503 si no), anónimos y sin detalles. Pantalla Diagnóstico con permiso `ManageConfiguration`, solo estados y recuentos (nunca cadenas ni claves). Sin alertas automáticas. Registro estructurado JSON en archivo diario (Serilog solo como sumidero de archivo) si se configura `Logging:File:Directory`.

### D-058 · Los scripts de instalación no se ejecutaron de punta a punta
`install-server.ps1`, `update-server.ps1`, `install-print-agent.ps1` y `new-lan-certificate.ps1` modifican servicios, cortafuegos, ACL y tareas del sistema; solo se verificó su sintaxis y se probaron por separado sus piezas (roles, migraciones con propietario, permisos, copia, restauración, arranque del paquete publicado). Deben ensayarse en un equipo de prueba.

## Cuestiones pendientes (Fase 6)

- **Revisión fiscal y legal:** bloqueante para cobrar a clientes. No hay factura simplificada/completa/rectificativa, ni registros de facturación encadenados, ni VERI*FACTU, ni declaración responsable (`docs/legal/revision-fiscal-y-legal.md`).
- **Pruebas con dispositivos e impresoras reales:** ninguna realizada (`docs/operations/pruebas-en-entorno-real.md`).
- Servidor concreto, número de tablets, impresoras y su conexión, terminal bancario, segundo servidor (cuestiones 1–6, 13, 16).
- Cifrado de copias, alertas automáticas, revisión de seguridad externa, rotación de claves.

## Fase 7 (2026-11-17): facturación

### D-059 · Alcance: facturas y registros de facturación, SIN envío a la AEAT
El propietario eligió VERI*FACTU y los tres tipos de documento (simplificada, completa, rectificativa), y no sabe aún su régimen fiscal (sociedad o persona física). Se implementa la parte que se puede construir y probar sin credenciales: numeración, facturas inmutables, rectificativas, anulaciones, registros de facturación encadenados con huella y QR. **El envío a la AEAT NO está implementado**: exige certificado electrónico, los esquemas XSD/WSDL oficiales y pruebas contra el entorno de preproducción de la AEAT, que no se han podido hacer. Todos los registros quedan en `billing_submissions` con estado «Pendiente» (la tabla está lista para un futuro enviador). No se ha fingido un envío ni se ha añadido un simulador.

### D-060 · Formato de la huella: no contrastado con los ejemplos oficiales
La cadena de entrada del SHA-256 (campos `IDEmisorFactura`, `NumSerieFactura`, `FechaExpedicionFactura`, `TipoFactura`, `CuotaTotal`, `ImporteTotal`, `Huella`, `FechaHoraHusoGenRegistro`; fechas `dd-MM-yyyy`; importes sin ceros finales; hexadecimal en mayúsculas; primer registro con `Huella=` vacío) se tomó de una descripción secundaria de las especificaciones de la AEAT (no se pudo leer el documento oficial). Hay que validarla con los ejemplos oficiales de la AEAT antes de enviar nada. El QR usa la URL de **preproducción** (`invoice.qr_base_url`) por la misma razón, y las facturas **no** llevan la leyenda «VERI*FACTU» mientras no se envíen.

### D-061 · Cadena única serializada por la cabeza de la cadena
Una sola cadena (un emisor, una instalación). `billing_chain_head` guarda el último número y huella y usa `xmin` como concurrencia: dos emisiones simultáneas no pueden crear eslabones con la misma huella anterior (la segunda se repite). Índices únicos sobre huella, huella anterior y (factura, tipo) impiden bifurcar la cadena. Una violación de unicidad al emitir (mismo número tomado a la vez) se trata como conflicto de concurrencia y se repite el caso de uso. `VerifyChainAsync` recalcula todas las huellas y detecta alteraciones, borrados y reordenaciones; no puede detectar que alguien con acceso total a la base de datos haya rehecho **toda** la cadena (eso exigiría anclar la última huella fuera de la base de datos, p. ej. con el envío a la AEAT).

### D-062 · Facturas y registros inmutables
`invoices`, `invoice_lines` y `billing_records` llevan trigger anti UPDATE/DELETE/TRUNCATE (misma función que los libros de dinero) y el rol de la aplicación no tiene esos permisos (`db-grants.sql`, comprobado con el rol restringido). Los errores se corrigen con rectificativas o con un registro de anulación. La cabeza de la cadena se puede actualizar pero no borrar. Cada factura guarda una copia de emisor, cliente y líneas.

### D-063 · Reglas de emisión
Una factura por comanda vigente (original no anulada y no rectificada del todo). Solo se factura una comanda **totalmente cobrada**. Para cambiar de simplificada a completa se rectifica la simplificada (R5) y se emite la completa; no se implementa el tipo F3 (sustitutiva). Rectificativas solo **por diferencias** (importes en negativo; R1 de completa, R5 de simplificada), con control de cantidades ya rectificadas y ajuste de céntimos en el último resto; la rectificación por sustitución no se implementa. La rectificativa **no devuelve dinero**: la devolución de caja sigue siendo una operación aparte. La anulación solo se permite en originales sin rectificativas. Límite de la simplificada configurable (400 € por defecto; **confirmar con el asesor** el límite aplicable). Series por tipo y año (`S2026-000001`, `F…`, `R…`); numeración correlativa garantizada por índice único.

### D-064 · Identificación fiscal
Se valida formato y dígito de control de NIF/NIE/CIF españoles; no se consulta su existencia en la AEAT ni se admiten identificadores extranjeros (VAT UE, pasaporte). Los datos del emisor se guardan en `app_settings` y, sin ellos, no se puede emitir.

## Cuestiones pendientes (Fase 7)
1. **Régimen fiscal** (sociedad / persona física) y fecha límite de adaptación; modalidad definitiva (VERI*FACTU u otra).
2. **Envío a la AEAT**: certificado, XML/SOAP según los XSD oficiales, reintentos y gestión de rechazos; pruebas en preproducción.
3. Validar la **huella y el QR** con los ejemplos oficiales; leyenda «VERI*FACTU».
4. **Registro de eventos** del sistema en el formato del reglamento (hoy solo la auditoría propia) y **declaración responsable** del productor.
5. Facturas F3, rectificación por sustitución, identificadores extranjeros, descuentos.
6. Confirmar límite de factura simplificada, tipos de IVA de hostelería y plazos de conservación con el asesor.
7. RGPD: las facturas completas guardan datos de clientes (nombre, NIF, domicilio): registro de actividades, información y plazo de conservación.

## Fase 8 (2026-11-17): envío a la AEAT

### D-065 · Esquemas oficiales embebidos y validación previa al envío
Los XSD oficiales (SuministroLR, SuministroInformacion, RespuestaSuministro) y el esquema xmldsig se descargaron de la sede de la AEAT (prewww2) y se incluyen en `src/TPVBar.Infrastructure/Billing/Schemas` junto con el WSDL (solo referencia; de él salen los endpoints). Cada cuerpo se valida contra ellos antes de enviarse. La validación detectó dos errores reales durante el desarrollo (espacio de nombres de `Cabecera`; valores `Correcta/AceptadaConErrores/Anulada` del estado de registro duplicado). Comprobar periódicamente si la AEAT publica versiones nuevas.

### D-066 · Envío desactivado por defecto, modo solo desde la configuración del servidor
`Billing:Aeat:Mode` = `Disabled` por defecto; `PreProduction` y `Production` solo se activan en el archivo protegido del servidor, nunca desde la interfaz, porque `Production` tiene efecto fiscal real. Sin certificado legible, el estado queda «Configuración» con el motivo y no se envía nada. **Nunca se ha ejecutado contra los servidores de la AEAT.**

### D-067 · Cola ordenada que se detiene ante un rechazo
Los registros se envían siempre en orden de cadena, por lotes consecutivos, y un registro no se envía hasta que los anteriores están aceptados (o aceptados con errores). La espera `TiempoEsperaEnvio` de la AEAT se aplica a todos los envíos. Un rechazo (`Incorrecto`) detiene la cola hasta que una persona lo revise y pulse «Reintentar» (no se generan registros de subsanación automáticamente). Fallos de comunicación: reintento con retroceso exponencial (1–30 min). Un duplicado ya aceptado en la AEAT (p. ej. se perdió una respuesta) cuenta como aceptado.

### D-068 · Datos del productor del sistema informático
Cada registro declara el sistema informático (productor, NIF, nombre «TPVBar», identificador de 2 caracteres, versión, número de instalación generado una vez). Esos datos deben coincidir con la **declaración responsable**, que sigue pendiente; por eso no se rellenan por defecto y bloquean el envío mientras falten.

## Cuestiones pendientes (Fase 8)
1. Certificado electrónico y alta del NIF en preproducción para la primera prueba real.
2. Contrastar la huella con los ejemplos oficiales (D-060): el rechazo de preproducción por huella es la señal.
3. Subsanación de registros rechazados, consulta de registros y firma electrónica opcional.
4. Declaración responsable y registro de eventos del reglamento.
5. Decidir con el asesor el paso a `Production`.

## Fase 9 (2026-11-17): subsanación de registros

### D-069 · Subsanación = registro nuevo al final de la cadena; el original se conserva como «Sustituido»
Un registro rechazado (o aceptado con errores) no se modifica nunca. «Reenviar corregido / Subsanar» genera un registro NUEVO para la misma factura (nueva fecha-hora y huella, encadenado tras el último registro) con `CorrectsSequence` y `RejectedBefore`, y marca el original como `Superseded` (se conserva, ya no se envía, y deja de bloquear la cola). En el XML se emiten los indicadores del esquema oficial: alta rechazada → `Subsanacion = S`, `RechazoPrevio = S`; alta aceptada con errores → `Subsanacion = S`, `RechazoPrevio = N`; anulación rechazada → `RechazoPrevio = S` (la anulación no tiene `Subsanacion`; una anulación aceptada con errores no se puede subsanar desde TPVBar). La unicidad «un alta y una anulación por factura» pasa a valer solo para registros que no son de subsanación.
**Hipótesis sin confirmar:** la combinación exacta de indicadores sale de los valores del XSD (cuya documentación solo define `RechazoPrevio`) y de mi conocimiento de la especificación; **no se ha contrastado con la AEAT**. Tampoco se sabe cómo valida la AEAT el `RegistroAnterior` cuando un registro anterior fue rechazado y no consta en su sistema: los registros siguientes se encadenan al registro rechazado (orden de generación). Ambas cosas se comprueban en el primer envío de pruebas; si la AEAT las rechaza, hay que ajustar con el documento oficial.
**Cuándo usar cada botón:** «Reintentar» reenvía el mismo registro (basta si la causa era de configuración —productor, certificado— o estaba en la AEAT). «Reenviar corregido» solo sirve cuando la causa estaba en los datos que entran en la huella (p. ej. un error del formato de `BillingHash`, D-060) o en el contenido del registro; un registro con la huella mal calculada seguirá rechazado por mucho que se reintente.

## Cuestiones pendientes (Fase 9)
1. Confirmar con la AEAT (preproducción) la combinación `Subsanacion`/`RechazoPrevio` y el comportamiento del encadenamiento tras un rechazo.
2. Si la huella cambia (D-060), definir qué se hace con los registros ya generados con la huella antigua (hoy: subsanar uno a uno).
3. Subsanación de anulaciones aceptadas con errores y consulta de registros en la AEAT.
