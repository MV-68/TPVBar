# Entorno de desarrollo local

Guía reproducible para trabajar en TPVBar en un equipo Windows. No es el procedimiento de producción.

## Requisitos

- SDK .NET 10 o superior (`dotnet --info`).
- PostgreSQL 18 instalado (binarios). El script **no usa ni modifica** ningún otro servidor PostgreSQL del equipo.
- Herramientas locales del repositorio: `dotnet tool restore` (instala `dotnet-ef` 10.0.15 desde `dotnet-tools.json`).

## 1. Base de datos de desarrollo

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1
```

Qué hace (es idempotente):

- Crea un clúster propio en `.pgdata` (puerto **5434**, solo `localhost`, autenticación `scram-sha-256`) y lo arranca desacoplado.
  Si ya estaba en marcha en otro puerto (antes era 5433), lo reinicia en el nuevo. Si otro programa ocupa el puerto, se detiene
  con un mensaje: en este equipo el servicio `miFactura-PostgreSQL` (otra aplicación) usa el 5433 y arranca con Windows.
  Para usar otro puerto: `scripts\dev-db.ps1 -Port <puerto>` (actualiza los User Secrets).
- Crea el rol `tpvbar_app` y las bases `tpvbar` (desarrollo) y `tpvbar_test` (pruebas; se **borra y recrea** en cada ejecución de pruebas).
- Genera contraseñas aleatorias. Se guardan en `.pgdata\dev-credentials.json` (ignorado por Git) y como **User Secrets** de `TPVBar.Web`:
  `ConnectionStrings:TpvBar`, `ConnectionStrings:TpvBarTest` y `Seed:DevUserPassword`.
- El log del servidor está en `.pglog\server.log` (fuera de los datos: dentro provoca *sharing violation* en Windows).

Parar el servidor:

```powershell
& "C:\Program Files\PostgreSQL\18\bin\pg_ctl.exe" stop -D .pgdata -m fast
```

## 2. Ejecutar

```powershell
dotnet run --project src/TPVBar.Web --launch-profile http
```

En **Desarrollo** la aplicación aplica las migraciones, asegura los roles iniciales y crea seis usuarios de prueba
(`dev.admin`, `dev.encargado`, `dev.camarero`, `dev.cocina`, `dev.caja`, `dev.comandero`). Su contraseña es `Seed:DevUserPassword`:

```powershell
dotnet user-secrets list --project src/TPVBar.Web
```

Si falta ese secreto no se crea ningún usuario de prueba (no existe contraseña por defecto).

## 3. Migraciones

```powershell
dotnet tool run dotnet-ef migrations add NombreDelCambio --project src/TPVBar.Infrastructure --startup-project src/TPVBar.Web --output-dir Persistence/Migrations
dotnet run --project src/TPVBar.Web -- --migrate
dotnet tool run dotnet-ef migrations script --idempotent --project src/TPVBar.Infrastructure --startup-project src/TPVBar.Web --output docs/database/<nombre>.sql
```

Las migraciones se revisan a mano (por ejemplo, el trigger de inmutabilidad de `audit_events` está en `InitialCreate`).

## 4. Pruebas

```powershell
dotnet test
```

Las pruebas de integración usan PostgreSQL real (`tpvbar_test`). Si no hay cadena de conexión **fallan con un mensaje claro**
(no se omiten en silencio). Alternativa: variable de entorno `TPVBAR_TEST_CONNECTION`.

## 5. Crear un administrador (también válido fuera de desarrollo)

```powershell
dotnet run --project src/TPVBar.Web -- --create-admin <usuario>
```

Pide la contraseña por consola sin mostrarla, o la toma de la variable de entorno `TPVBAR_ADMIN_PASSWORD`.
Se aplica la política de contraseñas (mínimo 10 caracteres, sin el nombre de usuario).

## 6. Crear otros usuarios (también válido fuera de desarrollo)

```powershell
dotnet run --project src/TPVBar.Web -- --create-user ana --role Comandero --name "Ana"
```

Roles: `Administrador`, `Encargado`, `Camarero`, `Cocina`, `Caja` y `Comandero` (solo toma comandas, pensado para móviles:
no cobra, no anula líneas enviadas ni reabre mesas). `--name` es el nombre que se ve en pantalla (por defecto, el usuario).
La contraseña se pide por consola sin mostrarla, o se toma de la variable de entorno `TPVBAR_USER_PASSWORD`.

En el **servidor de producción**, desde PowerShell como Administrador (necesita leer la configuración protegida):

```powershell
$env:TPVBAR_CONFIG_FILE = 'C:\ProgramData\TPVBar\config\secrets.json'; $env:ASPNETCORE_ENVIRONMENT = 'Production'
dotnet C:\TPVBar\app\TPVBar.Web.dll --create-user ana --role Comandero --name "Ana"
```
