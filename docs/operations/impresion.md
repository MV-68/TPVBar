# Impresión y pantalla de cocina

## Cómo funciona

```
Camarero envía ─▶ [PostgreSQL: ticket de cocina + trabajo en cola]  (una sola transacción)
                         │
   Agente de impresión ──┘  reclama por HTTP autenticado (X-Agent-Key), imprime y notifica el resultado
```

- Cada envío crea **un ticket de preparación por destino** (cocina / barra) y **un trabajo de impresión** en la cola persistente.
  Ambos se guardan en la misma transacción: no puede haber un pedido enviado sin su ticket en cola ni al revés.
- El **agente de impresión** (`TPVBar.PrintAgent`) es un proceso independiente (servicio de Windows). **No accede a la base de datos**:
  habla con el servidor por `/api/print/*` y solo conoce el contrato público `TPVBar.PrintContracts`.
- La cola es **persistente**: si el servidor, el agente o la impresora caen, los trabajos esperan en PostgreSQL.
- La pantalla de **Cocina** (`/cocina`) lee los mismos tickets; el aviso en tiempo real solo dice «algo cambió» y la pantalla vuelve a leer
  de la base de datos (con refresco de respaldo cada 30 s).

## Estados y garantías

| Estado | Significado |
|---|---|
| En cola | Esperando agente (o esperando la hora del siguiente reintento). |
| Imprimiendo | Un agente lo tiene reclamado (arrendamiento de 2 min). |
| Enviado | Entregado al dispositivo. **Mira el nivel de entrega** (abajo). |
| Fallido | Agotados 5 intentos. Requiere reintento manual. No bloquea a los demás. |
| Cancelado | Cancelado por una persona (auditado). |

**Nivel de entrega** (nunca se afirma «impreso» sin confirmación del hardware):

| Nivel | Qué sabemos realmente |
|---|---|
| Simulado | Se escribió un archivo. **No ha salido papel.** |
| Enviado a la impresora | Los bytes salieron por la red (TCP). No se sabe si hay papel o tapa abierta. |
| Aceptado por la cola de Windows | El spooler lo aceptó. Aún puede fallar al llegar a la impresora. |
| Impreso (confirmado) | Reservado: ninguna conexión actual lo puede confirmar. |

**Reintentos:** esperas de 5 s, 15 s, 45 s, 2 min y 5 min. Tras el 5.º fallo el trabajo queda «Fallido» (auditado) y se puede reintentar desde
*Impresoras* (3 intentos más).

**Agente caído a mitad de un trabajo:** el arrendamiento caduca a los 2 minutos y otro intento lo recupera. Como no se puede saber si el papel
salió, **puede reimprimirse**: ese ticket sale con el aviso «REIMPRESION / REINTENTO». Lo mismo si el agente imprime pero no consigue avisar al servidor.

**Evitar duplicados:** cada trabajo lleva una clave única (p. ej. el envío concreto). Repetir un envío (doble clic, reintento) no encola otro.
La **reimpresión manual** crea un trabajo nuevo y queda auditada.

**Sin impresora configurada:** el envío funciona y el pedido aparece en cocina, pero la pantalla avisa «NO se ha imprimido».

## Tipos de impresora

| Conexión | Destino | Estado |
|---|---|---|
| Windows | Nombre **exacto** de la impresora instalada (compartida o USB con controlador). Envío RAW a la cola. | Implementado. **Sin probar con hardware real.** |
| Red | `equipo:puerto`, p. ej. `192.168.1.50:9100`. Socket TCP, tiempo de espera 5 s. | Implementado y probado contra un servidor TCP local. **Sin probar con hardware real.** |
| USB directo | — | **No implementado.** Instale la impresora en Windows y use «Windows». |
| Simulada | Carpeta. Escribe un `.txt` por ticket. | Solo desarrollo y pruebas. La pantalla la marca «SIMULADA». |

Formato: ESC/POS, página de códigos CP858 (acentos y €), 80 mm = 48 columnas, 58 mm = 32. Corte parcial al final.
Los modelos exactos de impresora siguen pendientes de decidir (cuestiones 3–6 del documento maestro): hay que **probar con las impresoras reales**
antes de dar la impresión por validada (algunos modelos necesitan otra página de códigos).

Barra y cocina comparten por defecto la impresora de cocina. Se cambia en *Impresoras → Qué imprime cada destino*.

## Puesta en marcha en desarrollo

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1      # crea también la clave de la API del agente (User Secrets)
dotnet run --project src/TPVBar.Web --launch-profile http          # en una terminal
$env:DOTNET_ENVIRONMENT = "Development"; dotnet run --project src/TPVBar.PrintAgent   # en otra
```

Con los datos de demostración hay dos impresoras **simuladas**; los tickets aparecen en `%TEMP%\tpvbar-print\cocina` y `\tickets`
(o en `Seed:SimulatedPrintFolder`).

## Despliegue del agente (resumen; el instalador llega en la Fase 6)

1. Publicar: `dotnet publish src/TPVBar.PrintAgent -c Release -o C:\TPVBar\PrintAgent`.
2. Definir **por variables de entorno del servicio** (nunca en el repositorio): `Agent__ServerUrl`, `Agent__ApiKey`, `Agent__Name`.
   En el servidor web: `PrintAgent__ApiKey` con **la misma clave**. Sin clave en el servidor, la API del agente responde 503 (desactivada).
3. Instalar como servicio: `sc.exe create TPVBarPrintAgent binPath= "C:\TPVBar\PrintAgent\TPVBar.PrintAgent.exe" start= auto`.
4. Cortafuegos: la API `/api/print/*` solo debe ser accesible desde la LAN (idealmente solo desde el equipo del agente).

La clave se compara en tiempo constante y se comprueba **antes** de leer el cuerpo de la petición. Rótela cambiando ambos valores.

## Diagnóstico

- Indicador de impresión en la barra superior: *OK*, *N en cola*, *fallidos*, *agente sin conexión*, *sin impresoras*.
- *Configuración → Impresoras*: estado de cada impresora (último envío correcto / último error), cola con intentos y errores, y acciones
  *Imprimir prueba*, *Reintentar*, *Cancelar* y *Reimprimir*.
- El agente cachea la lista de impresoras 30 s: un cambio de configuración puede tardar hasta 30 s en aplicarse (o reiniciar el agente).
