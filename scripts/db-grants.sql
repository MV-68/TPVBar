-- TPVBar · permisos del rol de la aplicación. IDEMPOTENTE: ejecutar tras CADA migración, conectado a la base
-- 'tpvbar' como superusuario o como tpvbar_owner:
--
--   psql -h localhost -U tpvbar_owner -d tpvbar -v ON_ERROR_STOP=1 -f scripts\db-grants.sql
--
-- Principio: la aplicación solo trabaja con datos. No puede cambiar el esquema ni retirar los triggers que
-- protegen los registros inmutables, y ni siquiera tiene permiso de UPDATE/DELETE/TRUNCATE sobre ellos
-- (defensa en profundidad: los triggers ya los rechazan).

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA tpv TO tpvbar_app;

-- Datos: lectura y escritura general...
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA tpv TO tpvbar_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tpv TO tpvbar_app;

-- ...salvo los registros inmutables: solo se leen y se insertan.
REVOKE UPDATE, DELETE, TRUNCATE ON
    tpv.audit_events,
    tpv.payments,
    tpv.payment_refunds,
    tpv.cash_movements,
    tpv.stock_movements,
    tpv.invoices,
    tpv.invoice_lines,
    tpv.billing_records
FROM tpvbar_app;

-- La cabeza de la cadena de facturación se actualiza en cada emisión, pero no se borra ni se vacía.
REVOKE DELETE, TRUNCATE ON tpv.billing_chain_head FROM tpvbar_app;

-- El historial de migraciones es solo lectura para la aplicación (el diagnóstico lo consulta).
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON tpv.__ef_migrations_history FROM tpvbar_app;

-- Tablas y secuencias futuras creadas por el propietario: datos generales sí; los registros inmutables nuevos
-- (si una fase futura los añade) deben revisarse a mano y añadirse a la lista de arriba.
ALTER DEFAULT PRIVILEGES FOR ROLE tpvbar_owner IN SCHEMA tpv
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tpvbar_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tpvbar_owner IN SCHEMA tpv
    GRANT USAGE, SELECT ON SEQUENCES TO tpvbar_app;
