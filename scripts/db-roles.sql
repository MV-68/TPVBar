-- TPVBar · roles de base de datos de PRODUCCIÓN (mínimos privilegios). Ejecutar UNA vez como superusuario:
--
--   psql -h localhost -U postgres -v ON_ERROR_STOP=1 `
--        -v owner_pw=CLAVE1 -v app_pw=CLAVE2 -v backup_pw=CLAVE3 -f scripts\db-roles.sql
--   (opcional: -v dbname=otra_base; por defecto «tpvbar». Use claves largas y aleatorias, sin comillas ni espacios.)
--
-- Las contraseñas llegan por variables de psql (no se guardan en este archivo ni en el repositorio).
--
--   tpvbar_owner   dueño de la base y de las tablas. SOLO para migraciones (TPVBar.Web --migrate). Nunca lo usa la aplicación.
--   tpvbar_app     lo usa la aplicación. Solo datos: sin DDL, sin poder retirar triggers, y sin UPDATE/DELETE sobre
--                  los registros inmutables (cobros, devoluciones, movimientos de caja y de stock, auditoría).
--   tpvbar_backup  solo lectura (pg_read_all_data), para pg_dump.
--
-- Después de crear los roles: aplicar migraciones con el rol propietario y ejecutar scripts\db-grants.sql.

\set ON_ERROR_STOP on
\if :{?dbname}
\else
  \set dbname tpvbar
\endif

SELECT format('CREATE ROLE tpvbar_owner LOGIN PASSWORD %L', :'owner_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tpvbar_owner') \gexec
SELECT format('CREATE ROLE tpvbar_app LOGIN PASSWORD %L NOCREATEDB NOCREATEROLE NOSUPERUSER', :'app_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tpvbar_app') \gexec
SELECT format('CREATE ROLE tpvbar_backup LOGIN PASSWORD %L NOCREATEDB NOCREATEROLE NOSUPERUSER', :'backup_pw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tpvbar_backup') \gexec

SELECT format('CREATE DATABASE %I OWNER tpvbar_owner ENCODING ''UTF8'' TEMPLATE template0 LOCALE_PROVIDER icu ICU_LOCALE ''es-ES'' LOCALE ''C''', :'dbname')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'dbname') \gexec

GRANT pg_read_all_data TO tpvbar_backup;

\connect :dbname

-- Nadie más que el propietario y los roles de TPVBar entra en la base.
REVOKE ALL ON DATABASE :"dbname" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"dbname" TO tpvbar_app, tpvbar_backup, tpvbar_owner;

-- El esquema lo crea y posee el propietario (las migraciones usan CREATE SCHEMA IF NOT EXISTS).
CREATE SCHEMA IF NOT EXISTS tpv AUTHORIZATION tpvbar_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA tpv TO tpvbar_app, tpvbar_backup;

-- Lo que el propietario cree después (las migraciones) llega con permisos de datos para la aplicación; así el primer
-- arranque funciona. Los registros inmutables se restringen con scripts\db-grants.sql (se ejecuta tras cada migración).
ALTER DEFAULT PRIVILEGES FOR ROLE tpvbar_owner IN SCHEMA tpv GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tpvbar_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tpvbar_owner IN SCHEMA tpv GRANT USAGE, SELECT ON SEQUENCES TO tpvbar_app;
