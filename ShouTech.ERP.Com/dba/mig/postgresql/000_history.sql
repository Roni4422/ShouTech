-- Migration-runner bootstrap history. Keep this file immutable after release.
CREATE TABLE IF NOT EXISTS schema_migrations (
    migration_id text PRIMARY KEY,
    checksum char(64) NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
