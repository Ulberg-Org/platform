-- An app's database roles (§9.2, D75), the same locally and in Azure (D85). The roles hold the privileges and
-- can't sign in; who signs in as them differs: local.sql (passwords), azure.sql (Entra identities and groups).
-- Filled in by the platform CLI and by the environment stack: @db@ is the database's name.
--
-- Whoever creates the roles may act as them (Postgres 16+): Azure's admin isn't a superuser.
SET createrole_self_grant = 'set, inherit';
CREATE ROLE @db@_migrator NOLOGIN;
CREATE ROLE @db@_app NOLOGIN;
CREATE ROLE @db@_readonly NOLOGIN;
CREATE SCHEMA @db@ AUTHORIZATION @db@_migrator;
GRANT USAGE ON SCHEMA @db@ TO @db@_app, @db@_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE @db@_migrator IN SCHEMA @db@ GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO @db@_app;
ALTER DEFAULT PRIVILEGES FOR ROLE @db@_migrator IN SCHEMA @db@ GRANT SELECT ON TABLES TO @db@_readonly;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
-- The read-only role (developers and every Claude, D75) may EXPLAIN ANALYZE: its privileges allow only SELECT,
-- its transactions are read-only by default, and nothing runs longer than 30 seconds.
ALTER ROLE @db@_readonly SET default_transaction_read_only = on;
ALTER ROLE @db@_readonly SET statement_timeout = '30s';
