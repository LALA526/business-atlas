# Business Atlas operations and disaster recovery

## Signal to diagnosis

Every API error envelope carries `requestId` and W3C `traceId`. `/metrics` is available only on the internal API container (the proxy deliberately does not expose it) and includes HTTP latency/status, durable queue/outbox/job state, lease expiry and backup verification/freshness gauges. Obtain the matching request trace from API structured JSON logs, then correlate `jobId`, `taskId`, `runId` or `commitId` with Worker logs and the append-only domain audit. Metrics never carry project/content labels; domain audit and operational telemetry remain separate.

```sh
docker compose -p atlas-product logs --since 30m api worker
docker compose -p atlas-product exec -T postgres psql -U atlas -d atlas
docker compose -p atlas-product exec -T api wget -qO- http://localhost:8080/metrics
```

`monitor.py` evaluates readiness, DB connectivity/latency/connections, queue age, failed jobs, expired leases, outbox retries, backup freshness, WAL archive lag and backup-volume free space. Notifications contain only opaque codes/locators and a runbook anchor. Configure a real HTTPS webhook and HMAC secret file in `alerts.json`; `--dry-run` exercises evaluation without claiming delivery. Test the actual receiver with `--test-alert`; that event is explicitly named `ATLAS_SYNTHETIC_ALERT_TEST` and uses `automated-test-actor`.

## Alert responses

- **ATLAS_BACKUP_MISSING / ATLAS_BACKUP_STALE**: inspect `backup.log`, backup lock and free space. Run verification on the latest archive. Create a new logical backup; if writers were left stopped, restore readiness. Do not delete the last verified recovery point.
- **ATLAS_WAL_NOT_ARCHIVED / ATLAS_WAL_STALE**: inspect PostgreSQL `pg_stat_archiver`, `archive_command`, WAL mount permissions and `wal-sync.log`. Preserve local WAL before cleanup. A logical dump alone does not provide PITR.
- **ATLAS_LEASE_EXPIRED / ATLAS_QUEUE_STALE**: locate the job ID and fence in Worker logs. A crashed holder is reclaimed only after `lease_until`; fencing CAS prevents stale commits. Do not manually reset a running fence. Restart Worker, then verify queued work resumes from checkpoint without duplicate domain effects.
- **ATLAS_JOB_FAILED / ATLAS_OUTBOX_RETRY**: inspect the durable error code and attempts. Retry transient dependencies through the authorized job retry operation; deterministic validation/permission failures require correction, not a retry loop.
- **ATLAS_READINESS_FAILED / ATLAS_DB_UNAVAILABLE**: stop release activity and new writes. Check container health, DB connections and migrations. `/health/live` alone is not readiness. Roll back compatible application images; inconsistent/lost data follows isolated restore below.
- **ATLAS_DISK_LOW**: preserve verified backups, referenced blobs and WAL. Expand storage first. Blob GC is forbidden while any history or backup can reference an object.

## Disaster recovery drill and cutover

1. Assign an incident ID, stop new deployments and record the observed failure and latest healthy LSN/time. Keep the original damaged environment untouched for diagnosis.
2. Select a verified logical/base archive. Copy it and required encrypted WAL on separate media; keep the encryption key on independent secure storage.
3. Follow `deploy/backup/README.md` to restore into the isolated environment. For PITR select an explicit target timestamp and retain the generated report. Never rehearse on production volumes.
4. Reconcile every old baseline identity/digest, revision membership, source/blob hash, permissions/history, task/job/outbox state and deployment/audit records. Run the corresponding application migration/readiness and real authorization smoke checks.
5. Record elapsed RTO and actual RPO/data-loss window. A human DataOwner/ReleaseOwner must approve production cutover; automated drill actors cannot supply that approval.
6. For cutover, stop API/Worker, preserve a final backup where possible, switch only after another verified restore/reconciliation, then restart and test readiness/login/current+historical reads. Keep pre-cutover evidence and a rollback path.

## Capacity, upgrade and host restart

Review `/metrics` and Docker resource reports for p95/p99, connection pool, locks, queue age, disk/memory and container restarts. Keep total DB connections at or below 40 and alert at 35. Before an upgrade, confirm free disk, ARM64 images, secrets, OIDC and backup key access; take and verify a pre-deployment logical backup, run the one-shot migration, then API/Worker readiness and real smoke. Prefer expand/backfill/contract migrations. If schema compatibility fails, stop writes and use isolated recovery rather than forcing an old image onto an incompatible database.

For Mac sleep/power/VM restart, Docker services use `restart: unless-stopped`; keep the Docker VM in host startup and disable sleep during scheduled backup windows. After restart, confirm DB integrity/readiness, outbox progress and lease recovery. Never infer recovery from container presence alone. Logs rotate daily for 30 days; audit and approved knowledge follow enterprise retention and are not destroyed by log rotation.
