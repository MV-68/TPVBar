# Instalación en el servidor de producción (Windows, red local)

> Los scripts de esta guía **se han escrito y comprobado sintácticamente, y sus piezas (roles, migraciones con propietario, permisos, copias, restauración,
> arranque en modo Producción con el paquete publicado) se probaron en un PostgreSQL de desarrollo. `install-server.ps1`, `update-server.ps1`,
> `install-print-agent.ps1` y `new-lan-certificate.ps1` NO se han ejecutado de principio a fin en un Windows de producción**, porque modifican servicios,
> cortafuegos y permisos del sistema. Hágalo primero en un equipo de ensayo.

## 1. Requisitos del servidor
- Windows 10/11 Pro o Windows Server, 4 GB de RAM como mínimo y disco con espacio para copias (decisión 1 del documento maestro: **sin confirmar**).
- Una **segunda ubicación** para las copias (otro disco, USB externo o carpeta de red).
- Red local con IP fija (o reserva DHCP en el router) y un nombre local (p. ej. `tpvbar` por DNS del router o archivo *hosts*).
- Software: **ASP.NET Core Runtime 10 (Hosting Bundle)** y **PostgreSQL 18** (instalado con contraseña de `postgres`, escuchando solo en `localhost`).

## 2. Preparar el paquete (en el equipo de desarrollo)
```powershell
powershell -File scripts\publish.ps1        # genera artifacts\package (web, agent, scripts)
```
Copie `artifacts\package` al servidor (p. ej. `C:\Instalar\TPVBar`).

## 3. Certificado HTTPS
La cookie de sesión en producción **solo viaja por HTTPS** (`Security:RequireHttps=true`): sin certificado no se puede iniciar sesión.
- **Con autoridad certificadora propia o certificado válido:** use su `.pfx`.
- **Sin ella (LAN):** `scripts\new-lan-certificate.ps1 -ServerName tpvbar -IpAddress 192.168.1.10` crea un `.pfx` autofirmado (servidor) y un `.cer`.
  Instale el `.cer` como **entidad de certificación raíz de confianza** en cada PC, tablet y en el equipo del agente de impresión.
  Apunte la **fecha de caducidad** (2 años): al caducar, los dispositivos dejarán de conectar. Acceda siempre por el nombre/IP incluidos.

## 4. Instalar
```powershell
# PowerShell como Administrador, en la carpeta del paquete:
powershell -ExecutionPolicy Bypass -File scripts\install-server.ps1 `
    -PfxPath C:\ProgramData\TPVBar\certs\tpvbar.pfx -AdminUserName encargado `
    -BackupSecondaryDir E:\copias-tpvbar -RegisterBackupTask
```
Pide por consola las claves (`.pfx`, superusuario `postgres`, administrador inicial). Hace, y se detiene al primer fallo:
roles y base de datos → configuración protegida → migraciones → permisos → administrador → servicio → cortafuegos → copia diaria.

| Pieza | Dónde queda |
|---|---|
| Aplicación | `C:\TPVBar\app` (servicio `TPVBar.Web`, cuenta virtual `NT SERVICE\TPVBar.Web`, reinicio automático) |
| Configuración y secretos | `C:\ProgramData\TPVBar\config\secrets.json` (solo SYSTEM, Administradores y el servicio). **No está en el repositorio.** |
| Registros (JSON, un evento por línea, 30 días) | `C:\ProgramData\TPVBar\logs\tpvbar-AAAAMMDD.log` |
| Claves de sesión (cifradas con DPAPI de la máquina) | `C:\ProgramData\TPVBar\keys` |
| Copias | `C:\TPVBar\backups` + ubicación secundaria |

**Claves guardadas en `secrets.json`** (en claro, protegido por permisos del sistema de archivos): cadena de la aplicación, cadena de migraciones, clave del agente y
contraseña del `.pfx`. Si necesita recuperarlas, solo un administrador del servidor puede leerlas. Las contraseñas de los roles no se muestran en pantalla.

## 5. Base de datos: tres roles de mínimos privilegios
| Rol | Uso | Puede |
|---|---|---|
| `tpvbar_owner` | **Solo migraciones** (`--migrate`, `update-server.ps1`) | Cambiar el esquema |
| `tpvbar_app` | La aplicación | Leer/escribir datos. **No** cambia el esquema ni retira triggers; sobre cobros, devoluciones, movimientos de caja y de stock y auditoría **solo lee e inserta** |
| `tpvbar_backup` | Copias | Solo leer |

Comprobado con el rol de la aplicación: `UPDATE`/`DELETE`/`TRUNCATE` sobre los registros inmutables, `DROP TRIGGER`, `ALTER TABLE`, `CREATE TABLE` (en `tpv` y `public`),
modificar el historial de migraciones y `DROP SCHEMA` quedan **denegados**. **PostgreSQL no se expone a la red:** no hay regla de cortafuegos para su puerto
y debe escuchar en `localhost` (compruebe `listen_addresses` en `postgresql.conf`).

Tras **cada migración** se ejecuta `scripts\db-grants.sql` (lo hacen los scripts). Si una fase futura añade otra tabla de registros inmutables, hay que añadirla a su lista de `REVOKE`.

## 6. Red: cortafuegos, IP fija y nombre
- El script abre **solo** el puerto HTTPS y solo a la **subred local** (`-RemoteAddress LocalSubnet`, perfiles Privado/Dominio). Verifique que la red del servidor está marcada como *Privada*.
- IP fija o reserva DHCP, y DNS local o archivo *hosts*: **manual** (depende de su router). Documente la IP y el nombre.
- No publique el puerto en el router hacia Internet.

## 7. Impresoras y agente de impresión
1. Instale las impresoras en Windows (o anote `IP:9100` si son de red) y configúrelas en *Impresoras* de TPVBar.
2. En el equipo conectado a las impresoras (puede ser el servidor): `scripts\install-print-agent.ps1 -ServerUrl https://tpvbar` (pide la clave `PrintAgent:ApiKey` de `secrets.json`).
3. En *Diagnóstico* debe aparecer «En línea». **Pruebe con hardware real** (`pruebas-en-entorno-real.md`).

## 8. Diagnóstico y monitorización
- Pantalla **Diagnóstico** (solo administrador): versión, entorno, PostgreSQL (estado, versión, latencia, migraciones), pantallas conectadas, agente de impresión,
  trabajos en cola/fallidos, último error de impresión y estado de la última copia. Muestra **avisos** (agente sin conexión, copia fallida, obsoleta o sin duplicar, migraciones pendientes).
- `GET /health` (el proceso vive) y `GET /health/ready` (PostgreSQL responde y el esquema está al día; **503** si no). Anónimos y sin detalles; útiles para una tarea de vigilancia.
- Cabecera `X-Correlation-Id` en cada respuesta y en cada línea de registro: dé ese identificador al buscar un error.
- Los registros **no incluyen** contraseñas, claves ni cuerpos de petición. Las consultas SQL no se registran.
- **No hay alertas automáticas** (correo/SMS): alguien debe mirar Diagnóstico o vigilar `/health/ready`.

## 9. Actualizar a una versión nueva
```powershell
powershell -File scripts\update-server.ps1 -BackupSecondaryDir E:\copias-tpvbar   # desde el paquete nuevo
```
Copia previa (si falla, no actualiza) → para el servicio → guarda la versión anterior en `C:\TPVBar\previous` → copia la nueva → migra → permisos → arranca → comprueba `/health/ready`.
Hágalo **fuera de horas de servicio**. Marcha atrás: `copias-y-restauracion.md`, caso C.

## 10. Seguridad: qué se hace y qué no
| Medida | Estado |
|---|---|
| HTTPS en la LAN, cookie `Secure` + `HttpOnly` + `SameSite=Strict`, caducidad 30 min de inactividad / 12 h absoluta | Hecho (probado) |
| Cabeceras: `nosniff`, `X-Frame-Options: DENY`, CSP, `Referrer-Policy`, `Permissions-Policy` | Hecho. **CSP admite scripts en línea** (D-054) |
| HSTS | Activo en Producción (el navegador recordará usar HTTPS) |
| Tokens antifalsificación (CSRF), límite de intentos de login por IP, bloqueo de usuario | Hecho (fases anteriores) |
| Secretos fuera del repositorio y del paquete (`publish.ps1` aborta si encuentra `.pfx`, `secrets.json` o credenciales de desarrollo) | Hecho |
| Contraseñas con hash de ASP.NET Core Identity; auditoría inmutable | Hecho |
| Cuenta del servicio sin privilegios (virtual); base de datos con 3 roles | Hecho |
| Cifrado de las copias, antivirus/actualizaciones del servidor, copia en la nube | **No** (decisión del propietario) |
| Prueba de intrusión / revisión de seguridad externa | **No realizada** |
| Cuenta del agente de impresión: `LocalSystem` (necesaria para el spooler) | Riesgo asumido y documentado |

## 11. Qué falta para llamarlo «listo»
`pruebas-en-entorno-real.md` completada y `docs/legal/revision-fiscal-y-legal.md` resuelta. **No lo use con clientes sin ello.**
