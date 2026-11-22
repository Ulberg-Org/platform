# Database operations: what we do now, and how we would do the rest

Postgres runs as Azure Database for PostgreSQL Flexible Server (D85). Today it's deliberately simple: `dev` is
rebuilt on every start with seed data (D45), so it needs no backups, no replicas and no high availability. This
document is what we'd do once data matters (prod, Phase 6b), so the decisions are written down before they're
needed. Each part says what's done now, and what we'd do later.

## Backups and restore

**Now:** the server's automatic backups (daily snapshots and the write-ahead log), kept 7 days, locally redundant. They
don't survive `destroy`, which is fine for dev.

**Later (prod):**
- **Point-in-time restore**, from Azure's own backups: retention 35 days (the maximum) and **geo-redundant backup**,
  so a whole-region outage still has a copy in the paired region. That can only be chosen when the server is
  created, so prod starts with it.
- **Long-term backups outside the server's life:** Azure Backup's vault for Postgres Flexible (weekly or monthly,
  kept a year or more). It survives deleting the server, which Azure's own backups don't. The vault sits in
  `shared` or a prod-persistent stack, never in the environment stack that gets destroyed.
- **Logical dumps (`pg_dump`)** of each app's database, as a scheduled job, into the persistent storage account
  (immutable, a retention lock), for moving a database to another server or version, or restoring one table.
- **Restore drills:** a scheduled workflow restores the latest backup into a throwaway server, runs a check
  (counts, the app's migrations apply), and deletes it. An untested backup isn't a backup.
- **Targets:** write down RPO (how much data we may lose: minutes with PITR) and RTO (how fast we're back: well
  under an hour for a restore in the same region), and check them in the drills.

## High availability

**Now:** none (`HighAvailability = Disabled`), and the Burstable tier can't have it.

**Later (prod):** **zone-redundant HA**: a standby in another availability zone, kept in sync, with automatic failover
(typically 60–120 s) and the same address. It needs the General Purpose tier (e.g. `Standard_D2ds_v5`) and doubles
the compute cost. The app copes with a failover the usual way: Npgsql retries and the connection pool reconnects.

## Read replicas

**Now:** none.

**Later, only when measured load asks for it:** asynchronous read replicas (up to five), in the same region for read
scaling or another region for reads close to users. Because they lag behind (seconds), only reads that tolerate stale
data go there: reports, search, the read-only role (developers and Claude, D75). Each replica gets the same private
access and Entra roles, and a **virtual endpoint**, so apps use a stable name for "the writer" and "a reader"
regardless of promotions. `platform.yaml` would declare it per database (e.g. `replicas: 1`), the renderer gives the
app a second connection string, and the app chooses per query.

## Multi-region

**Now:** one region (Sweden Central, D83).

**Later, if an outage of a whole region must not stop us:**
- **Cross-region read replica** in the paired region, which can be **promoted** to the writer (manually or by
  runbook) in a disaster. The virtual endpoint follows the promotion.
- **Geo-redundant backups** (above) as the cheaper fallback, if hours of downtime are acceptable.
- Everything else must be in the second region too (AKS, gateway, DNS failover), so multi-region is a platform
  decision, not a database one. We'd decide it per app's availability target.

## Connection pooling

**Now:** each backend instance's own Npgsql pool.

**Later:** the server's built-in **PgBouncer** (Flexible Server has one, on port 6432) once there are many app
instances or short-lived jobs, so connections to Postgres stay few. Transaction pooling mode; the app avoids session
state (prepared statements per session, advisory locks) or uses session mode.

## Maintenance, upgrades and settings

- **Maintenance window:** a custom window at a quiet time (Azure's patching restarts the server).
- **Minor versions:** applied by Azure in the window. **Major versions** (18 → 19, see PLAN's TODO): Azure's in-place
  major upgrade, rehearsed first on a restored copy, and locally at the same time (D56).
- **Server settings** that the platform owns (D92), in the environment stack: `pg_stat_statements` and Query Store
  (D64), `log_min_duration_statement`, `idle_in_transaction_session_timeout`, connection limits. Per-app tuning is
  the app's, in its migrations.
- **Storage:** autogrow on, with an alert well before the limit.

## Security (Phase 3, now: D93)

- **Already:** no public access, Entra only (no passwords), TLS, private DNS, least-privilege roles (D75, D85).
- **Audit:** `pgaudit` logs role and privilege changes, DDL and failed sign-ins, and the server logs connections
  (`log_connections`, `log_disconnections`). Phase 5 brings these logs into Grafana.
- **Threat detection:** Microsoft Defender for open-source relational databases on the server: unusual access,
  brute force, suspicious queries. About 15 USD a month per server, billed by the hour, so a few kroner for dev's
  4-hour runs.
- **Later, only if compliance requires it:** customer-managed keys (CMK) in Key Vault. Azure already encrypts at rest
  with its own keys.

## Monitoring and alerts (Phase 5: D93)

The server's metrics (CPU, memory, storage, connections, replication lag), its logs (slow queries, the pgaudit log,
connections) and Defender's findings go into the same Grafana as everything else (D71). Alerts, to the alert list
(D40) and readable by Claude (D58):
- storage above 80 %, connections near the limit, CPU or memory pressure;
- queries running longer than a minute, deadlocks, failed sign-ins in bursts;
- replication lag (once there are replicas), failed backups or restore drills;
- every Defender alert.
