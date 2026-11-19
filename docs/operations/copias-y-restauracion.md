# Copias de seguridad y restauración

## Objetivos (a validar con el propietario)

| | Objetivo inicial | Cómo se consigue |
|---|---|---|
| **RPO** (datos que se pueden perder) | 24 h | Copia diaria a las 04:00. Mejorable programando más copias (cada una es un volcado completo). |
| **RTO** (tiempo para volver a funcionar) | menos de 2 h | Restaurar un volcado de un bar es cuestión de minutos; lo lento es reinstalar el servidor si se pierde. **No medido en un servidor real.** |

## Qué hace `scripts\backup.ps1`

1. `pg_dump -Fc` (comprimido) con el rol **`tpvbar_backup`**, de solo lectura.
2. **Verifica** el volcado (`pg_restore --list`) y guarda su huella **SHA-256**.
3. **Duplica** en `-SecondaryDir` (otro disco, unidad de red o USB externo) y comprueba que la copia es idéntica.
   *Una copia solo en el disco del servidor no protege frente a un fallo del disco, un robo o un cifrado malicioso.*
4. **Retención:** borra copias con más de 30 días (`-RetentionDays`) conservando siempre las 3 últimas (`-KeepAtLeast`).
5. Escribe `backup-status.json` (éxito, hora, tamaño, verificada, duplicada). **Diagnóstico** lo muestra y avisa si la copia falló, tiene más de 30 h o no se duplicó.

Copia **antes de cada migración:** `update-server.ps1` la hace con `-Reason premigration` y **no actualiza si falla**.

Programación: la crea `install-server.ps1 -RegisterBackupTask` (tarea «TPVBar copia diaria», 04:00, cuenta SYSTEM). La clave del rol de copias va en
`C:\ProgramData\TPVBar\config\pgpass.conf` (acceso solo para Administradores y SYSTEM), no en la línea de comandos.

## Prueba periódica de restauración (hacerla al menos una vez al mes)

```powershell
# Como administrador de PostgreSQL (la clave por PGPASSWORD o pgpass.conf):
powershell -File C:\TPVBar\scripts\restore.ps1 -BackupFile D:\copias-tpvbar\tpvbar_AAAAMMDD_HHMMSS_daily.dump
```

Restaura en una base **temporal** (`tpvbar_restoretest`), comprueba la huella, aplica los permisos, cuenta migraciones, usuarios, comandas, cobros y auditoría,
**verifica que lo pagado de cada comanda cuadra con cobros menos devoluciones** y borra la base temporal. Debe terminar con *PRUEBA DE RESTAURACIÓN CORRECTA*.
Apunte la fecha y el resultado. **Una copia que no se ha probado no es una copia.**

## Recuperación ante un desastre, paso a paso

**A. La base de datos está dañada, pero el servidor funciona**
1. `Stop-Service TPVBar.Web`.
2. Localice la copia más reciente correcta (la del diagnóstico o la de la ubicación secundaria).
3. `powershell -File C:\TPVBar\scripts\restore.ps1 -BackupFile <copia> -RestoreToProduction`
   (renombra la base actual a `tpvbar_replaced_<fecha>` —no la borra—, crea `tpvbar` y restaura).
4. `Start-Service TPVBar.Web` y compruebe `https://<servidor>/health/ready` (200) y la pantalla Diagnóstico.
5. Revise la caja: puede haber operaciones posteriores a la copia que hay que **repetir a mano** (RPO). Compare con los tickets impresos.
6. Cuando confirme la recuperación, borre a mano la base `tpvbar_replaced_*`.

**B. Se ha perdido el servidor entero**
1. Instale Windows, PostgreSQL 18 y el runtime ASP.NET Core 10; copie el paquete y ejecute `install-server.ps1` (crea roles, base vacía y servicio).
   *Use el mismo certificado* (`.pfx`) o instale el nuevo `.cer` en los dispositivos.
2. Pare el servicio y restaure la copia con `restore.ps1 -RestoreToProduction`.
3. Las claves de la base y del agente **cambian** si reinstala: el agente de impresión debe reinstalarse con la nueva `PrintAgent:ApiKey`.
4. Arranque y compruebe como en A.4–A.5.

**C. Una actualización salió mal**
- Si las migraciones **no** se aplicaron: vuelva a copiar `C:\TPVBar\previous\app` sobre `C:\TPVBar\app` y arranque el servicio.
- Si **sí** se aplicaron: restaure la copia `premigration` (caso A) y la carpeta `previous\app`.

## Límites

- Las copias no están cifradas: guárdelas en un sitio de acceso restringido (los volcados contienen toda la actividad y los hashes de contraseñas).
- No hay copia continua (WAL/PITR): el RPO real es el intervalo entre copias.
- Segundo servidor de respaldo (cuestión 16): no implementado.
