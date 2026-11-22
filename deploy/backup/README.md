# Backup, verification and recovery

Requirements: Docker Compose, the running `atlas-product` deployment, Python 3.11+, and OpenSSL. Generate a 32+ byte random key with `openssl rand -out KEY 32`, chmod it `0600`, and store it separately from the backup media. Losing this key makes encrypted backups unusable. `ATLAS_BACKUP_ROOT` must be mounted storage in another physical failure domain; a second directory on the same disk is not disaster recovery.

## Scheduled backup and WAL

`backup.py create` takes an exclusive backup lock, stops API, Worker and Identity writers, captures the atlas database and content-addressed blob tree across the same write-free boundary, records LSN/migrations/application build and per-file hashes, restarts exactly the writers that were running, AES-256 encrypts the archive, and decrypts/verifies the result. A logical backup is for daily handoff and isolated recovery. A `base` backup uses `pg_basebackup` with streamed WAL for PITR. `wal-sync` encrypts every newly archived WAL segment before copying it to backup storage. `retain` keeps seven daily, four weekly and three monthly recovery points.

```sh
deploy/backup/backup.py create --release-dir RELEASE --backup-root "$ATLAS_BACKUP_ROOT" --key-file "$ATLAS_BACKUP_KEY_FILE" --kind logical
deploy/backup/backup.py create --release-dir RELEASE --backup-root "$ATLAS_BACKUP_ROOT" --key-file "$ATLAS_BACKUP_KEY_FILE" --kind base
deploy/backup/backup.py verify ARCHIVE --backup-root "$ATLAS_BACKUP_ROOT" --key-file "$ATLAS_BACKUP_KEY_FILE"
deploy/backup/backup.py wal-sync --backup-root "$ATLAS_BACKUP_ROOT" --key-file "$ATLAS_BACKUP_KEY_FILE" --wal-root "$ATLAS_WAL_ROOT"
```

Backups are not successful when a command merely exists: an archive is emitted only after its outer SHA-256, inner file hashes and content-addressed blobs validate. Failed/partial directories are excluded from discovery. Writer restart occurs in `finally`; after a failed backup, confirm readiness and treat the alert as a failed recovery point, not a valid backup.

## Isolated restore and PITR

Restore into a dedicated empty work directory. The command creates an internal-only Docker network, new volumes and no host port; it never selects the production Compose project. It verifies every database blob reference against both extracted bytes and restored volume bytes, and records baseline IDs/numbers/parents/digests plus revision, source, job and audit counts.

```sh
scripts/restore/restore.py ARCHIVE --key-file "$ATLAS_BACKUP_KEY_FILE" --work-dir "$ATLAS_RESTORE_WORK_ROOT/drill-YYYYMMDD" --keep
scripts/restore/restore.py BASE_ARCHIVE --key-file "$ATLAS_BACKUP_KEY_FILE" --work-dir "$ATLAS_RESTORE_WORK_ROOT/pitr-YYYYMMDD" --target-time 2026-11-22T03:00:00+00:00 --wal-dir "$ATLAS_BACKUP_ROOT/wal" --keep
```

After `--keep`, run the application version recorded in the backup against the isolated DB, apply/read the recorded migration state, and perform authenticated smoke checks for current and historical baselines, field/source permissions, sources, tasks and deployment records. Do not point production at the drill until a human DataOwner/ReleaseOwner authorizes cutover and records the recovery point, expected data-loss window and before/after reconciliation. Knowledge rollback remains a newly approved baseline, not a database pointer edit. Without `--keep`, all isolated containers, volumes and network are removed after verification; the JSON report remains.

An actual drill must record archive ID, target time/LSN, duration, data-loss window, operator and human cutover decision where applicable. The initial targets are RPO 24h/RTO 2h for logical recovery and RPO 15min with continuous WAL; measure rather than claiming these targets from script presence.
