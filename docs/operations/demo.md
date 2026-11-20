# Entorno de demostración

Entorno aparte para enseñar TPVBar con datos **ficticios**, sin tocar la base de desarrollo (`tpvbar`).

> Solo para demostraciones. No es una instalación de producción y los tipos impositivos son de ejemplo
> (no validados fiscalmente). «Tarjeta» sigue siendo un registro manual.

## Uso

Requisitos: los de [desarrollo local](desarrollo-local.md) (SDK .NET 10 y PostgreSQL 18).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\demo.ps1          # prepara y arranca en http://localhost:5142
powershell -ExecutionPolicy Bypass -File scripts\demo.ps1 -Reset   # borra y recrea la base demo
powershell -ExecutionPolicy Bypass -File scripts\demo.ps1 -NoRun   # solo carga los datos
```

El script arranca el PostgreSQL de desarrollo (`dev-db.ps1`), crea la base `tpvbar_demo`, aplica migraciones, el
catálogo demo y los usuarios de prueba, y ejecuta `TPVBar.Web --seed-demo`.

## Qué contiene

- Catálogo demo: 3 zonas, 14 mesas, 6 categorías, 18 productos, existencias y impresoras simuladas.
- Usuarios `dev.admin`, `dev.encargado`, `dev.camarero`, `dev.cocina`, `dev.caja`, `dev.comandero`; la contraseña es la clave
  `Seed:DevUserPassword` (`dotnet user-secrets list --project src\TPVBar.Web`).
- Historial: 3 turnos de caja cerrados (~65 comandas pagadas en efectivo y tarjeta), una devolución y un descuadre
  de 0,50 € en el último turno.
- Un turno abierto con una mesa ya cobrada, 3 mesas ocupadas (una sin enviar a cocina) y 1 pendiente de cobro.
- Dos productos por debajo del stock mínimo para ver los avisos.

## Límites

- `--seed-demo` se niega a ejecutarse en producción y no hace nada si la base ya tiene turnos de caja.
- Las ventas pasan por los casos de uso reales, por lo que llevan la fecha y hora de la carga (no hay histórico
  repartido en varios días).
- La primera ejecución registra un error de EF Core («tabla `__ef_migrations_history` no existe»): es la comprobación
  previa a crear el esquema y es inofensivo.
