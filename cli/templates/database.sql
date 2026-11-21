-- An app's database (§9.2, D64, D75), filled in by the platform CLI (its template function).
CREATE ROLE @db@_migrator LOGIN PASSWORD 'local-only-migrator';
CREATE ROLE @db@_app LOGIN PASSWORD 'local-only-app';
CREATE ROLE @db@_readonly LOGIN PASSWORD 'local-only-readonly';
CREATE SCHEMA @db@ AUTHORIZATION @db@_migrator;
GRANT USAGE ON SCHEMA @db@ TO @db@_app, @db@_readonly;
ALTER DEFAULT PRIVILEGES FOR ROLE @db@_migrator IN SCHEMA @db@ GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO @db@_app;
ALTER DEFAULT PRIVILEGES FOR ROLE @db@_migrator IN SCHEMA @db@ GRANT SELECT ON TABLES TO @db@_readonly;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
-- Statistics (D64): per-query statistics, a monitor role for the collector (no data, only statistics), and
-- every query's statistics for the read-only role, which Claude's Postgres MCP uses (EXPLAIN, top queries).
CREATE EXTENSION pg_stat_statements;
CREATE ROLE @db@_monitor LOGIN PASSWORD 'local-only-monitor' IN ROLE pg_monitor;
GRANT pg_read_all_stats TO @db@_readonly;
-- The read-only role (developers, and Claude's Postgres MCP, D75) may EXPLAIN ANALYZE: its privileges allow only
-- SELECT, its transactions are read-only by default, and nothing runs longer than 30 seconds.
ALTER ROLE @db@_readonly SET default_transaction_read_only = on;
ALTER ROLE @db@_readonly SET statement_timeout = '30s';
