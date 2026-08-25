BEGIN;
ALTER TABLE plugins
 ADD COLUMN IF NOT EXISTS endpoint_url text,
 ADD COLUMN IF NOT EXISTS secret_ref text,
 ADD COLUMN IF NOT EXISTS settings jsonb NOT NULL DEFAULT '{}'::jsonb,
 ADD COLUMN IF NOT EXISTS subscribed_events text[] NOT NULL DEFAULT ARRAY[]::text[],
 ADD COLUMN IF NOT EXISTS last_tested_at timestamptz,
 ADD COLUMN IF NOT EXISTS last_error text;
CREATE TABLE IF NOT EXISTS plugin_deliveries(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 plugin_id uuid NOT NULL REFERENCES plugins(id) ON DELETE CASCADE,
 event_type text NOT NULL,
 payload jsonb NOT NULL DEFAULT '{}'::jsonb,
 state text NOT NULL DEFAULT 'queued' CHECK(state IN('queued','processing','delivered','failed','dead_letter')),
 attempts integer NOT NULL DEFAULT 0,
 response_code integer,
 response_excerpt text,
 last_error text,
 available_at timestamptz NOT NULL DEFAULT now(),
 delivered_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS plugin_deliveries_ready_idx ON plugin_deliveries(state,available_at) WHERE state IN('queued','failed');
CREATE INDEX IF NOT EXISTS plugin_deliveries_tenant_idx ON plugin_deliveries(tenant_id,created_at DESC);
CREATE TABLE IF NOT EXISTS quality_evaluation_notes(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 evaluation_id uuid NOT NULL REFERENCES quality_evaluations(id) ON DELETE CASCADE,
 author_user_id uuid NOT NULL REFERENCES users(id),
 note_type text NOT NULL CHECK(note_type IN('review','dispute','resolution','compliance')),
 body text NOT NULL CHECK(length(body) BETWEEN 1 AND 5000),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS quality_notes_evaluation_idx ON quality_evaluation_notes(evaluation_id,created_at);
INSERT INTO schema_migrations(version) VALUES('003_integrations_quality.sql') ON CONFLICT DO NOTHING;
COMMIT;
