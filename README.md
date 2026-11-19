# TPVBar

Punto de venta para un único bar, ejecutado en red local (servidor Windows + navegadores en PC y tablets).
Interfaz y textos en español (España); código en inglés.

> **Estado: Fase 8 (envío a la AEAT) completada; versión candidata, PENDIENTE DE VALIDACIÓN FISCAL** (hardware real y revisión fiscal sin hacer; ver abajo). Hay inicio de sesión, permisos, auditoría, plano de mesas, carta, comandas, pantalla de cocina en
> tiempo real, impresión mediante un agente (probada con impresora **simulada**, no con hardware real) y caja con cobros, devoluciones
> y cierre. «Tarjeta» es un registro manual (sin datáfono) y el justificante **no es una factura**. Incluye almacén básico por unidades, estadísticas de ventas y exportación CSV
> (sin XLSX), y scripts de instalación, copias y diagnóstico. Emite facturas simplificadas, completas y rectificativas con registros de facturación encadenados y QR, y puede enviarlas a la AEAT (VERI*FACTU, **desactivado por defecto y no probado contra la AEAT**). **No se ha probado con tablets ni impresoras reales, y no hay revisión fiscal ni prueba real del envío a la AEAT: no debe usarse para cobrar y facturar a clientes todavía.**

Especificación completa: [`Prompt_Maestro_TPVBar.md`](Prompt_Maestro_TPVBar.md).

## Requisitos de desarrollo

| Componente | Versión |
|---|---|
| SDK .NET | 10.0.x o superior (fijado por `global.json`; compila a `net10.0`) |
| PostgreSQL | 18 (aún no se usa en Fase 0) |
| IDE | Visual Studio 2026 (opcional) |

Comprobar el entorno: `dotnet --info`.

## Compilar, probar y ejecutar

```powershell
dotnet tool restore
powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1   # PostgreSQL de desarrollo (puerto 5433) + secretos
dotnet restore
dotnet build
dotnet test
dotnet run --project src/TPVBar.Web --launch-profile http
```

`dotnet test` usa Microsoft.Testing.Platform (xUnit v3), activado en `global.json`. Las pruebas de integración necesitan
el PostgreSQL de desarrollo en marcha. La web escucha en `http://localhost:5141`; `GET /health` devuelve el estado del proceso.

Para imprimir hay que arrancar además el agente (en otra terminal):

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/TPVBar.PrintAgent
```

Demostración con datos ficticios: `powershell -ExecutionPolicy Bypass -File scripts\demo.ps1` ([guía](docs/operations/demo.md)).

Guías: [desarrollo local](docs/operations/desarrollo-local.md) (migraciones, usuarios de prueba, administrador) e
[impresión y cocina](docs/operations/impresion.md) (estados, garantías, despliegue del agente) y
[caja y cobros](docs/operations/caja-y-cobros.md) (flujo, efectivo esperado, reglas y límites) y
[almacén e informes](docs/operations/almacen-e-informes.md) (política de stock, definiciones y exportación).

Producción: [instalación en el servidor](docs/operations/produccion.md), [copias y restauración](docs/operations/copias-y-restauracion.md),
[pruebas en el entorno real](docs/operations/pruebas-en-entorno-real.md) (pendientes), [primer envío a la AEAT en preproducción](docs/operations/pruebas-preproduccion-aeat.md), [facturación](docs/operations/facturacion.md) y [revisión fiscal y legal](docs/legal/revision-fiscal-y-legal.md) (pendiente).

## Estructura

```
src/
  TPVBar.Domain          entidades y reglas (sin dependencias)
  TPVBar.Application     casos de uso y abstracciones (-> Domain)
  TPVBar.Infrastructure  EF Core, Npgsql, impresión, seguridad (-> Application, Domain)
  TPVBar.Web             Blazor Web App, hubs SignalR (compone las capas)
  TPVBar.PrintContracts  contrato público servidor ↔ agente (sin dependencias)
  TPVBar.PrintAgent      agente de impresión (servicio de Windows; solo conoce PrintContracts)
tests/                   Domain.Tests, Application.Tests, Integration.Tests
docs/                    architecture, database, ui, operations, legal
scripts/                 despliegue y utilidades
```

## Documentación

- [`DECISIONS.md`](DECISIONS.md): decisiones técnicas, alternativas descartadas y cuestiones pendientes.
- [`CHANGELOG.md`](CHANGELOG.md): historial de cambios.

## Secretos

Nunca se guardan en el repositorio. Usar User Secrets en desarrollo y variables de entorno en el servidor.
