BEGIN;
CREATE TABLE IF NOT EXISTS call_recordings(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),tenant_id uuid NOT NULL REFERENCES tenants(id),call_id uuid NOT NULL REFERENCES calls(id),
 storage_key text NOT NULL,state text NOT NULL DEFAULT 'starting' CHECK(state IN('starting','recording','awaiting_upload','ready','failed','expired')),
 content_type text,size_bytes bigint,sha256 text,started_at timestamptz,completed_at timestamptz,expires_at timestamptz NOT NULL,error text,
 created_at timestamptz NOT NULL DEFAULT now(),UNIQUE(call_id)
);
CREATE INDEX IF NOT EXISTS call_recordings_retention_idx ON call_recordings(state,expires_at);
INSERT INTO schema_migrations(version) VALUES('006_recordings.sql') ON CONFLICT DO NOTHING;
COMMIT;
