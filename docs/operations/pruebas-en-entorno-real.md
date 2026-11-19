# Pruebas en el entorno real (pendientes)

> **Nada de esta lista se ha podido ejecutar en el desarrollo.** Hasta completarla y firmarla, TPVBar es una
> *versión candidata a producción pendiente de validación*, no un sistema validado.

Rellene fecha, quién lo probó y resultado. Un fallo se anota; no se da por bueno «porque casi funcionaba».

## 1. Servidor
| Prueba | Resultado |
|---|---|
| `install-server.ps1` termina sin errores en el servidor real | |
| `https://<nombre>/health/ready` responde 200 desde un PC y desde una tablet | |
| El servicio arranca solo tras reiniciar Windows y reinicia si se mata el proceso | |
| Las sesiones **sobreviven** a reiniciar el servicio (claves de protección de datos persistentes) | |
| Desde otro equipo de la LAN **no** se puede conectar al puerto de PostgreSQL | |
| Desde fuera de la subred local **no** se puede abrir TPVBar | |
| La IP del servidor es fija o tiene reserva DHCP; el nombre DNS local resuelve en todos los dispositivos | |

## 2. Dispositivos
| Prueba | Resultado |
|---|---|
| El certificado (.cer) instalado: sin advertencias en cada navegador/tablet | |
| Mesas, comanda, cobro y cocina usables **con el dedo** en cada tablet real (botones, scroll, teclado en pantalla) | |
| Rotación horizontal/vertical, reposo y reconexión de la tablet sin perder el pedido | |
| 5 terminales a la vez durante un servicio simulado (hora punta) sin lentitud apreciable | |
| Pérdida de WiFi de una tablet 30 s: aviso visible y recuperación sin duplicar pedidos | |

## 3. Impresoras (modelos exactos aún por decidir: cuestiones 3–6)
| Prueba | Resultado |
|---|---|
| Ticket de prueba desde *Impresoras* en la impresora de cocina y en la de tickets | |
| Acentos, ñ y € correctos (si no, otra página de códigos: hoy CP858) | |
| Ancho real (80 mm = 48 columnas; 58 mm = 32) sin cortes de línea | |
| Corte de papel y cajón portamonedas (si hay) | |
| Ticket de cocina de un pedido de bebidas (barra) y de comida (cocina) a su impresora | |
| **Apagar la impresora y enviar un pedido:** queda en cola, avisa y sale al encenderla (sin duplicar) | |
| Quitar papel / tapa abierta: comprobar **qué** se detecta (probablemente nada: el sistema no lo promete) | |
| Reiniciar el agente a mitad de un trabajo: sale como máximo una reimpresión marcada | |
| Impresora Windows compartida vs. red (TCP 9100): cuál se usará y que funciona tras reiniciar el equipo | |

## 4. Caja y datos
| Prueba | Resultado |
|---|---|
| Un día completo de servicio simulado con datos de prueba: apertura, cobros en efectivo/tarjeta/dividido, devolución y cierre a ciegas | |
| El cierre cuadra con el dinero físico y con los tickets | |
| `backup.ps1` programado: aparece en Diagnóstico y se duplica en la ubicación externa | |
| `restore.ps1` (prueba) correcto con una copia del servidor real | |
| Simulacro de desastre (apartado B de copias-y-restauracion.md) con cronómetro: ¿cuánto tarda realmente? | |

## 5. Antes de abrir al público
- [ ] Revisión fiscal y legal completada con un asesor (`docs/legal/revision-fiscal-y-legal.md`). **Bloqueante.**
- [ ] Datos de demostración **no** presentes (el seed solo corre en Desarrollo) y tipos impositivos revisados por el asesor.
- [ ] Contraseñas del administrador cambiadas/guardadas; usuarios y roles reales creados.
- [ ] Plan de contingencia en papel si el sistema cae durante el servicio.
