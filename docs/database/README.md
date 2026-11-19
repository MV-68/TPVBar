# Base de datos

Las **migraciones de EF Core** (`src/TPVBar.Infrastructure/Persistence/Migrations`) son la única fuente de verdad del esquema.
Los `.sql` de esta carpeta se generan a partir de ellas solo para revisión; no se mantienen a mano.

- Esquema `tpv`, nombres en `snake_case`, historial de migraciones en `tpv.__ef_migrations_history`.
- [`Schema-Fase5.sql`](Schema-Fase5.sql): script idempotente completo (acumulado hasta la Fase 5).

## Modelo de la Fase 1

| Tabla | Contenido | Notas |
|---|---|---|
| `users` | Trabajadores | Solo hash de contraseña. Índice único sobre el nombre normalizado. Concurrencia optimista con `xmin`. Contador de intentos y fin de bloqueo. |
| `roles` | Roles | Nombre único. |
| `role_permissions` | Permisos de cada rol | Permiso guardado por nombre (texto). Editable: el seed solo los asigna al crear el rol. |
| `user_roles` | Roles de cada usuario | Clave compuesta. |
| `audit_events` | Auditoría | **Inmutable**: un trigger rechaza `UPDATE`, `DELETE` y `TRUNCATE`. `details` en `jsonb`, sin secretos. |

## Decisiones respecto al DDL del prompt maestro

- Se usa `uuid` v7 (`Guid.CreateVersion7()`) para `users.id`, generado en la aplicación.
- `AppUser` propio con el hasher de ASP.NET Core Identity, en lugar de las tablas completas de Identity (ver `DECISIONS.md`, D-009).
- Las tablas de caja, pagos, stock, impresión, etc. del DDL llegan en las fases siguientes mediante migraciones.

## Limitación conocida

El trigger protege frente a la aplicación y frente a SQL ordinario del rol `tpvbar_app`, pero el propietario de la tabla puede
eliminar el trigger. En producción, las migraciones deben ejecutarse con un rol distinto del que usa la aplicación
(mínimos privilegios, sección 11.1 del prompt maestro). Está previsto en la Fase 6.

## Modelo de la Fase 2

| Tabla | Contenido | Notas |
|---|---|---|
| `zones`, `dining_tables` | Sala | Nombre único (zona; mesa dentro de su zona). El estado de la mesa **no se guarda**: se deriva de la comanda viva. |
| `categories`, `products` | Carta | Precio con IVA incluido `numeric(12,2)`. SKU único si existe. Destino de preparación como texto. |
| `tax_rates` | Impuestos | Porcentaje `numeric(6,3)` con vigencia (`valid_from`/`valid_to`). Sin valores fijos en código. |
| `orders` | Comandas | **Una viva por mesa** (índice único parcial). `xmin` como concurrencia. CHECK `grand_total = subtotal + tax_total`. |
| `order_lines` | Líneas | Fotografía de nombre, precio, impuesto y destino. Anuladas se conservan con motivo/usuario/fecha. |
| `kitchen_tickets`, `kitchen_ticket_lines` | Tickets de preparación | Uno por destino y envío. Con etiqueta de mesa y nombres congelados. No son documento fiscal. |
| `idempotency_records` | Operaciones ya hechas | Clave única; se guarda en la misma transacción que la operación. |

Migración `Phase2SalesCore`: además del esquema, añade los permisos `ManageCatalog` y `TakeOrders` a los roles que ya existían.

## Modelo de la Fase 3

| Tabla | Contenido | Notas |
|---|---|---|
| `printers` | Impresoras | Nombre único. Tipo (cocina/tickets), conexión (Windows/Red/USB/Simulada), destino (nombre, `equipo:puerto` o carpeta), papel 58/80 mm. **Sin contraseñas ni secretos.** Último éxito/error. |
| `printer_routes` | Destino → impresora | Una ruta por destino de preparación (cocina, barra). |
| `print_jobs` | Cola persistente | `payload` (jsonb, sin secretos), clave de deduplicación **única**, estado, nivel de entrega, intentos, próximo intento, arrendamiento (`locked_by/until`), `xmin` como concurrencia. |
| `print_job_attempts` | Intentos | Quién, cuándo, resultado y error de cada intento. |
| `print_agent_heartbeats` | Latido del agente | Último contacto, para el indicador «agente sin conexión». |
| `kitchen_tickets` | (ampliada) | `updated_at`/`updated_by`: quién y cuándo cambió el estado de preparación. |

Migración `Phase3KitchenAndPrinting`: además del esquema, añade `UseKitchen` y `ManagePrinting` a los roles que ya existían.

## Modelo de la Fase 4

| Tabla | Contenido | Notas |
|---|---|---|
| `payment_methods` | Métodos de pago | Código único e inmutable; «usa el cajón» y «exige referencia» configurables. Se crean Efectivo, Tarjeta y Otros. |
| `cash_sessions` | Sesiones de caja | **Una sola abierta** (índice único parcial). `xmin` como concurrencia y `last_activity_at` para serializar cierre y cobros. Esperado/contado/diferencia al cerrar. |
| `cash_movements` | Entradas/salidas manuales | Importe > 0, concepto obligatorio. **Inmutable** (trigger). |
| `payments` | Cobros | Importe aplicado, efectivo entregado y cambio (CHECK de coherencia), referencia, sesión, usuario. **Inmutable** (trigger). |
| `payment_refunds` | Devoluciones | Operación compensatoria sobre un pago, con motivo, método y sesión. **Inmutable** (trigger). |
| `orders` | (ampliada) | `paid_total` neto de devoluciones, CHECK `0 ≤ paid_total ≤ grand_total`. |

Migración `Phase4CashAndPayments`: además del esquema y los triggers, añade `TakePayments` a los roles que ya existían. Los métodos de pago iniciales los crea el seed de referencia al arrancar.

Los triggers protegen frente a la aplicación y frente a SQL ordinario del rol de la aplicación, pero el propietario de las tablas podría retirarlos (ver «Limitación conocida»).

## Roles de producción (Fase 6)

Resuelve la «Limitación conocida» de arriba: en producción la aplicación **no** es propietaria de las tablas.
`scripts\db-roles.sql` crea `tpvbar_owner` (migraciones), `tpvbar_app` (datos; sin UPDATE/DELETE/TRUNCATE sobre `audit_events`, `payments`, `payment_refunds`,
`cash_movements` y `stock_movements`, y solo lectura del historial de migraciones) y `tpvbar_backup` (solo lectura). `scripts\db-grants.sql` se ejecuta tras cada migración.
Detalle y pruebas en `docs/operations/produccion.md` y `DECISIONS.md` (D-052). El propietario de las tablas **sigue pudiendo** retirar los triggers: su clave
solo está en `secrets.json` del servidor y no la usa la aplicación.

## Modelo de la Fase 5

| Tabla | Contenido | Notas |
|---|---|---|
| `products` | (ampliada) | `stock_controlled`, `stock_quantity`, `stock_minimum` (≥ 0), `stock_unit`. `xmin` como concurrencia. La cantidad solo cambia mediante movimientos. |
| `stock_movements` | Inventario | Delta ≠ 0, saldo resultante, motivo, referencia y usuario. **Inmutable** (trigger). Único por (motivo, referencia) cuando hay referencia: un reintento no descuenta dos veces. |
| `app_settings` | Ajustes generales | Clave/valor. Hoy: `stock.allow_negative` (por defecto desactivado). |

Migración `Phase5StockAndReports`. No hay tablas de informes: se calculan siempre sobre los datos originales (tickets, líneas, cobros, devoluciones y cierres).

## Modelo de la Fase 7

| Tabla | Contenido | Notas |
|---|---|---|
| `invoices` | Facturas | **Inmutable** (trigger). Serie + número únicos; emisor y cliente congelados; CHECK base + IVA = total, signo según tipo, cliente obligatorio en la completa. |
| `invoice_lines` | Líneas de factura | **Inmutable**. Cantidad (negativa en rectificativas), precio con IVA, tipo, base, cuota y total; enlace a la línea rectificada. |
| `billing_records` | Registros de facturación (alta/anulación) | **Inmutable**. Posición en la cadena, huella y huella anterior (únicas), fecha-hora tal como entró en la huella. |
| `billing_chain_head` | Cabeza de la cadena | Una sola fila (`id = 1`); `xmin` serializa las emisiones. Se actualiza, pero no se puede borrar ni vaciar. |
| `billing_submissions` | Estado de envío a la AEAT | Hoy todos «Pendiente»: el envío no está implementado. |

Migración `Phase7Invoicing`: además del esquema, crea los triggers, inserta la cabeza de la cadena y añade `IssueInvoices` / `RectifyInvoices` a los roles existentes.
`scripts\db-grants.sql` quita UPDATE/DELETE/TRUNCATE al rol de la aplicación sobre las tablas inmutables nuevas.
