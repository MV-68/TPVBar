# Revisión fiscal y legal (estado a 17/11/2026)

> **TPVBar NO está declarado apto fiscalmente.** Este documento es un análisis técnico de partida, **no asesoramiento jurídico**.
> Antes de usarlo para cobrar a clientes hay que validarlo con un asesor fiscal y comprobar las fuentes oficiales vigentes.

## Qué emite hoy TPVBar

| Documento | Estado | ¿Es documento fiscal? |
|---|---|---|
| Ticket de preparación (cocina/barra) | Implementado | **No** |
| Pre-cuenta | Implementado | **No** |
| Justificante de pago (leyenda «No es una factura ni un documento fiscal») | Implementado | **No** |
| Factura simplificada, completa y rectificativa (Fase 7) | Implementado (numeración, inmutabilidad, QR); **sin envío a la AEAT ni validación fiscal** | Sí, en cuanto se valide (ver `docs/operations/facturacion.md`) |

Cobrar con TPVBar sin emitir una factura simplificada válida (cuando proceda) puede ser **incumplir la obligación de facturación**.
Mientras no se resuelva, **no se debe usar como único sistema de cobro y facturación**.

## Marco normativo a analizar (fuentes oficiales)

- Real Decreto 1007/2023 (reglamento de sistemas informáticos de facturación): [BOE-A-2023-24840](https://www.boe.es/buscar/act.php?id=BOE-A-2023-24840).
- Orden HAC/1177/2024 (especificaciones técnicas, funcionales y de contenido): [BOE-A-2024-22138](https://www.boe.es/diario_boe/txt.php?id=BOE-A-2024-22138).
- Real Decreto 1619/2012 (obligaciones de facturación).
- AEAT, sistemas informáticos de facturación y VERI*FACTU: [nota sobre la ampliación del plazo](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/nota-informativa-ampliacion-plazo-adaptacion-facturacion.html) y [preguntas frecuentes](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes.html).

**Calendario (según la nota informativa de la AEAT consultada el 17/11/2026, tras el Real Decreto-ley 15/2025):** los obligados que
presentan el Impuesto sobre Sociedades deben tener adaptado su sistema **antes del 1 de enero de 2027**; el resto, **antes del 1 de julio de 2027**.
Las fechas se han aplazado más de una vez: **comprobar la norma vigente en el BOE** antes de decidir.

## Requisitos del reglamento frente al estado de TPVBar

| Requisito | Estado en TPVBar |
|---|---|
| Registro de facturación por cada factura (alta/anulación), con huella (hash) y encadenamiento | Implementado (Fase 7); **formato de huella no contrastado con ejemplos oficiales** |
| Modalidad VERI*FACTU (envío a la AEAT) o no VERI*FACTU (con registro de eventos y firma) | **Elegida VERI*FACTU pero envío NO implementado**; la decisión de modalidad es del negocio y su asesor |
| Código QR y leyenda en la factura | QR implementado (URL de preproducción); **sin leyenda VERI*FACTU** porque no hay envío |
| Registro de eventos del sistema | Parcial: auditoría inmutable de operaciones (no cumple el formato del reglamento) |
| Inalterabilidad, conservación, accesibilidad, trazabilidad | Base sólida (registros de dinero inmutables por trigger, copias, auditoría) pero **sin validar** frente al reglamento |
| Facturas rectificativas, series y numeración correlativa | Implementado (rectificación por diferencias); las devoluciones de caja siguen sin ser rectificativas |
| Declaración responsable del productor del software | **Pendiente**: la debe emitir quien produce/distribuye el sistema (no se puede dar por hecha) |
| Tipos impositivos de hostelería | Configurables con vigencia; **los de demostración no están validados fiscalmente** |
| Exportación e interoperabilidad de registros | CSV de libro de facturas e IVA para el asesor; **sin exportación en el formato oficial ni envío** |

## Decisiones que necesito del propietario

1. **Régimen fiscal** del bar (persona física o sociedad) y su fecha límite de adaptación.
2. ¿Se emitirá **factura simplificada** a todos los clientes o solo a petición? ¿Facturas completas con datos del cliente?
3. ¿Modalidad **VERI*FACTU** o no? (cuestión 14 del documento maestro).
4. ¿Qué terminal bancario se usa? (hoy el cobro con tarjeta es un registro manual).
5. ¿Exportación a un programa contable? (cuestión 15).

## Propuesta de siguiente fase (fuera de esta entrega)

Una fase propia de **facturación** (tras el asesoramiento): modelo de factura y numeración, registros encadenados e inalterables,
QR, rectificativas, módulo VERI*FACTU si se elige, y la declaración responsable. Hasta entonces el sistema debe describirse como
**«gestión interna del bar, no sistema de facturación»**.

## Otras obligaciones a revisar con el asesor

- **Protección de datos (RGPD/LOPDGDD):** hoy se guardan datos de trabajadores (usuario, hash de contraseña, auditoría de sus actos). No hay datos
  de clientes. Si se añaden (facturas completas, reservas) habrá que inscribirlos en el registro de actividades, informar y definir plazos de conservación.
- **Conservación:** las facturas y registros deben conservarse durante los plazos legales (normalmente 4 años, más en algunos casos): definir y comprobar
  que las copias de seguridad los cubren.
- **Cámaras, fichaje y control horario** no están en el alcance.

## Actualización de la Fase 7 (17/11/2026)

Respuestas del propietario: régimen fiscal **desconocido** (a consultar con el asesor), modalidad preferida **VERI*FACTU**, documentos: simplificada,
completa y rectificativa, con exportación para el asesor e informe de IVA por tipo. Resultado y límites en
[`docs/operations/facturacion.md`](../operations/facturacion.md) y en `DECISIONS.md` (D-059..D-064). **Sigue sin estar declarado apto fiscalmente**:
falta el envío a la AEAT, contrastar la huella y el QR con los ejemplos oficiales, el registro de eventos del reglamento y la declaración responsable.
Siguen abiertas las cuestiones 1 (régimen/fecha límite) y 4 (terminal bancario).

## Actualización de la Fase 8 (17/11/2026)

El **envío de registros a la AEAT** (VERI*FACTU) está implementado: XML validado contra los XSD oficiales, cola ordenada, reintentos y estados (ver
`docs/operations/facturacion.md` y D-065..D-068). **Nunca se ha ejecutado contra la AEAT** (falta certificado real) y viene **desactivado**. Siguen
pendientes: la primera prueba en preproducción, contrastar la huella con los ejemplos oficiales, la declaración responsable, el registro de eventos, la
confirmar con la AEAT la subsanación de rechazos (D-069) y la decisión (con el asesor) de pasar a producción. **Sigue sin estar declarado apto fiscalmente.**
