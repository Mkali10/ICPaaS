BEGIN;

ALTER TABLE tenant_settings
  ADD COLUMN IF NOT EXISTS service_entitlements text[] NOT NULL DEFAULT '{}';

INSERT INTO schema_migrations(version)
VALUES ('008_service_entitlements')
ON CONFLICT DO NOTHING;

COMMIT;
