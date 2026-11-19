# Caja y cobros

## Flujo del día

1. **Abrir caja** (*Caja*, permiso `OpenCloseCash`: Caja, Encargado, Administrador): se cuenta el cajón y se anota el **fondo inicial**.
   Sin caja abierta nadie puede cobrar (la barra superior muestra «Caja cerrada»).
2. **Cobrar** (*Comanda → Cobrar*, permiso `TakePayments`: también camareros):
   - Se elige el método y el importe (por defecto, todo el saldo; también «Mitad»).
   - En efectivo se indica lo **entregado** y se muestra el **cambio**.
   - Con tarjeta u otros se puede anotar una referencia (obligatoria si el método lo exige).
   - **Confirmación** antes de registrar. Se puede cobrar en varios pagos y con distintos métodos hasta saldar la cuenta.
   - Al saldar, la comanda queda «Cobrada», la mesa libre y se imprime el **justificante de pago**.
3. **Movimientos manuales** (entradas/salidas de efectivo, con concepto) en *Caja*.
4. **Devoluciones** (permiso `PerformRefunds`): desde la lista de cobros de la sesión. Se devuelve por el mismo método, con **motivo obligatorio**.
   Es una operación compensatoria: el cobro original no se modifica y la devolución se imputa a la caja actual.
5. **Cerrar caja**: se cuenta el cajón y se escribe lo contado. El recuento es **a ciegas**: el efectivo esperado se calcula y se muestra al cerrar,
   junto con la diferencia («Cuadra», «Faltan X €», «Sobran X €»). Se imprime un resumen interno.

## Qué cuenta como efectivo esperado

`fondo inicial + cobros en efectivo − devoluciones en efectivo + entradas − salidas`

- Cuenta el **importe aplicado** a la cuenta, no lo entregado (el cambio sale del cajón y vuelve a restar).
- Los métodos que no usan el cajón (tarjeta, otros) no cuentan para el efectivo, pero sí aparecen en el resumen por método.

## Reglas que el sistema garantiza

| Regla | Dónde |
|---|---|
| Un cobro no supera el saldo pendiente. | Dominio + CHECK en PostgreSQL. |
| Importes > 0 con 2 decimales; el efectivo entregado no es menor que el importe. | Dominio. |
| Dos terminales cobrando lo mismo: solo una cuenta. | Concurrencia optimista (`xmin`). |
| Doble clic o reintento no cobra dos veces. | Clave de idempotencia. |
| Pagos, devoluciones y movimientos no se editan ni se borran. | Triggers en PostgreSQL. |
| Una comanda con cobros no se edita, reabre ni cancela. | Dominio. |
| No se cierra la caja con comandas a medio cobrar. | Servicio de caja. |
| Cierre y cobros simultáneos no se cruzan. | La sesión se «toca» en cada escritura contable. |
| Solo una caja abierta a la vez. | Índice único parcial. |
| Todo queda auditado con usuario y hora. | Auditoría inmutable. |

## Lo que NO es

- **«Tarjeta» no es una integración con el datáfono.** TPVBar no cobra la tarjeta ni comprueba que se haya cobrado: solo registra que el cliente pagó así.
  Se cobra primero en el datáfono y después se anota (con la referencia, si se desea).
- **El justificante de pago no es una factura ni un documento fiscal.** Lleva la leyenda impresa. Sin numeración fiscal ni facturas
  simplificadas/rectificativas hasta completar el análisis legal (RD 1007/2023, VERI*FACTU).
- **El cajón no se abre solo.** No hay comando de apertura del cajón.
- No hay propinas, cobro por producto o por persona, ni varias cajas.

## Métodos de pago

*Configuración → Métodos de pago* (permiso `ManageCatalog`): nombre, orden, «usa el cajón», «exige referencia» y activo. El código es fijo.
Siempre debe quedar un método activo. Un método con cobros ya registrados no puede cambiar su condición de «usa el cajón».
