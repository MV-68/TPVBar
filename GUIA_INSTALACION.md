# Guía de instalación de TPVBar

> **Importante:** esta versión está pendiente de validación con hardware real y de revisión fiscal. No la utilices todavía para cobros o facturación reales a clientes.

## 1. Preparar el servidor

Utiliza un equipo con Windows 10/11 Pro o Windows Server, conectado a la red local del bar.

Antes de instalar:

- Asigna una IP fija al servidor o crea una reserva DHCP en el router.
- Define un nombre local, por ejemplo `tpvbar`.
- Prepara una segunda ubicación para las copias de seguridad, por ejemplo `E:\copias-tpvbar`, un disco USB o una carpeta de red.
- Instala PostgreSQL 18 y configúralo para escuchar solo en `localhost`.
- Instala ASP.NET Core Runtime 10 Hosting Bundle.

## 2. Generar el paquete de instalación

En el equipo de desarrollo, abre PowerShell en la carpeta raíz del proyecto y ejecuta:

```powershell
powershell -File scripts\publish.ps1
```

El paquete se generará en `artifacts\package`.

## 3. Copiar el paquete al servidor

Copia todo el contenido de `artifacts\package` al servidor, por ejemplo en:

```text
C:\Instalar\TPVBar
```

## 4. Crear o instalar el certificado HTTPS

La aplicación requiere HTTPS para permitir el inicio de sesión.

Si dispones de un certificado válido para el nombre del servidor, utiliza su archivo `.pfx`.

Para una instalación solo en red local, crea un certificado autofirmado desde la carpeta del paquete:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\new-lan-certificate.ps1 -ServerName tpvbar -IpAddress 192.168.1.10
```

Sustituye `192.168.1.10` por la IP fija del servidor. Guarda el archivo `.pfx` en una ubicación segura, por ejemplo:

```text
C:\ProgramData\TPVBar\certs\tpvbar.pfx
```

Si utilizas el certificado autofirmado, instala su archivo `.cer` como **Entidad de certificación raíz de confianza** en cada PC, tablet y equipo que ejecute el agente de impresión.

## 5. Ejecutar la instalación

Abre PowerShell **como administrador** en la carpeta del paquete y ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-server.ps1 `
  -PfxPath C:\ProgramData\TPVBar\certs\tpvbar.pfx `
  -AdminUserName encargado `
  -BackupSecondaryDir E:\copias-tpvbar `
  -RegisterBackupTask
```

El instalador solicitará:

- La contraseña del certificado `.pfx`.
- La contraseña del usuario `postgres`.
- La contraseña del administrador inicial de TPVBar.

También creará la base de datos, configurará los permisos y secretos, instalará el servicio `TPVBar.Web`, abrirá el acceso HTTPS desde la red local y programará las copias diarias.

## 6. Comprobar que funciona

Desde el servidor, abre:

```text
https://tpvbar
```

También puedes comprobar el estado técnico mediante:

```text
https://tpvbar/health
https://tpvbar/health/ready
```

`/health/ready` debe responder correctamente cuando el proceso, PostgreSQL y las migraciones estén listos.

## 7. Instalar el agente de impresión

Instala primero las impresoras en Windows o anota la dirección `IP:9100` de las impresoras de red.

En el equipo conectado a las impresoras, ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-print-agent.ps1 -ServerUrl https://tpvbar
```

El script pedirá la clave `PrintAgent:ApiKey`, disponible en la configuración protegida del servidor. Después, configura las impresoras en TPVBar y comprueba en la pantalla **Diagnóstico** que el agente aparece como «En línea».

## 8. Validaciones antes de usarlo

Antes de operar con clientes, completa las pruebas documentadas en:

- `docs\operations\pruebas-en-entorno-real.md`
- `docs\legal\revision-fiscal-y-legal.md`

Prueba expresamente las impresoras reales, los dispositivos móviles, la red, las copias de seguridad y la restauración de una copia.

## 9. Actualizar la aplicación

Genera y copia un paquete nuevo. Después, fuera de horario de servicio, ejecuta desde la carpeta del nuevo paquete:

```powershell
powershell -File scripts\update-server.ps1 -BackupSecondaryDir E:\copias-tpvbar
```

El proceso crea una copia previa, conserva la versión anterior y comprueba el estado de la aplicación tras actualizar.
