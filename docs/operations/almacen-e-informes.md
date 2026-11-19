# Almacén e informes

## Almacén (por unidades)

- Para controlar un producto: *Productos → Editar → Controlar el stock*, con **mínimo** y **unidad**. Activar el control no cambia las existencias (empiezan en 0).
- Las existencias solo cambian con movimientos, en *Almacén* (permiso `AdjustStock`: Encargado y Administrador):
  - **Entrada**: compra o reposición.
  - **Salida**: merma, rotura o consumo propio (motivo obligatorio).
  - **Ajustar**: se escribe el recuento físico y el sistema calcula la diferencia (motivo obligatorio).
  - **Historial** de cada producto: fecha, motivo, movimiento, saldo, detalle y usuario.
- **Política automática**: el stock se **descuenta al enviar** el pedido a cocina o barra y se **repone si esa línea enviada se anula**.
  Quitar una línea que aún no se envió no mueve stock.
- **Sin stock suficiente** el envío se rechaza entero (no se guarda nada) y el mensaje dice qué producto y cuántas unidades quedan.
  El **administrador** puede permitir stock negativo en *Almacén → Stock negativo*; queda auditado.
- **Avisos de reposición**: indicador «Stock bajo: N» en la barra superior, lista en Almacén y sección en Estadísticas. El aviso se da al llegar al mínimo.

### Garantías

| Regla | Dónde |
|---|---|
| El stock es la suma de sus movimientos. | Dominio + pruebas con PostgreSQL. |
| Un reintento o doble clic no descuenta dos veces. | Clave de idempotencia del envío + índice único por línea. |
| Dos envíos simultáneos no se pisan. | `xmin` del producto + reintento. |
| Los movimientos no se editan ni se borran. | Trigger en PostgreSQL. |
| Cantidades > 0 con 3 decimales; motivo en salidas y ajustes. | Dominio. |
| No se repone lo que nunca se descontó. | Solo se repone si existe el movimiento de venta de esa línea. |

## Estadísticas (permiso `ViewReports`: Encargado y Administrador)

Elige un periodo (hoy, ayer, semana, mes, mes anterior o fechas) y agrupa por día, semana (de lunes a domingo) o mes.
**Los días y las horas son los locales del establecimiento (Europe/Madrid).** Máximo 366 días.

| Sección | Qué cuenta |
|---|---|
| Resumen | Tickets, ventas (IVA incl.), ticket medio, devoluciones y neto. Desglose: brutas, descuentos, base e IVA. |
| Ventas por día/semana/mes y por franja horaria | Tickets y ventas, con barras para comparar (las cifras exactas están en la tabla). |
| Cobros por método | Dinero **recibido y devuelto** por método de pago. |
| Productos y categorías | Unidades e importe de las líneas cobradas (las anuladas no cuentan). |
| Anulaciones y devoluciones | Líneas anuladas tras enviarse (con motivo y usuario) y dinero devuelto. |
| Diferencias de caja | Esperado, contado y diferencia de cada cierre del periodo. |
| Stock bajo | Productos activos en o bajo su mínimo. |

### Definiciones (importante)

- **Ticket**: comanda cobrada cuyo cierre cae en el periodo. **Ventas**: suma de los tickets con IVA incluido.
- **Base e IVA** desglosan las ventas. Las **devoluciones no se desglosan por IVA** y se cuentan por la fecha en que se devuelven.
- **Ventas y cobros no son lo mismo.** Una cuenta cobrada en dos días reparte sus pagos en dos días y cuenta como un solo ticket el día en que se salda.
  No se suman ni se comparan como si fueran la misma magnitud.

## Exportar a CSV

Botones en *Estadísticas*: ventas por periodo, ventas por producto y cobros por método. Abre bien en Excel en español
(separador «;», coma decimal, UTF-8 con BOM). Las celdas de texto que empiezan por `= + - @` se protegen frente a la inyección de fórmulas.
Cada exportación queda **auditada** (quién, qué informe y fechas). **XLSX no está disponible** (pendiente de aprobación).

URL directa (con sesión): `/export/{ventas-por-periodo|ventas-por-producto|cobros-por-metodo}.csv?desde=AAAA-MM-DD&hasta=AAAA-MM-DD[&agrupar=dia|semana|mes]`.

## Límites actuales

- Sin descuentos todavía: «ventas brutas» y «ventas» coinciden.
- Solo ventas, no rentabilidad (no hay costes ni márgenes).
- Sin compras/proveedores, lotes ni caducidades, y sin recuento completo de inventario.
- Los informes agregan en memoria: suficiente para un bar, no para volúmenes enormes.
