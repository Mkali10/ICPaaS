BEGIN;
ALTER TABLE processes
 ADD COLUMN IF NOT EXISTS calling_timezone text NOT NULL DEFAULT 'UTC',
 ADD COLUMN IF NOT EXISTS calling_days smallint[] NOT NULL DEFAULT ARRAY[1,2,3,4,5,6]::smallint[],
 ADD COLUMN IF NOT EXISTS calling_start time NOT NULL DEFAULT '09:00',
 ADD COLUMN IF NOT EXISTS calling_end time NOT NULL DEFAULT '20:00',
 ADD COLUMN IF NOT EXISTS require_consent boolean NOT NULL DEFAULT false;
ALTER TABLE contacts
 ADD COLUMN IF NOT EXISTS consent_status text NOT NULL DEFAULT 'unknown' CHECK(consent_status IN('unknown','granted','revoked')),
 ADD COLUMN IF NOT EXISTS consent_at timestamptz,
 ADD COLUMN IF NOT EXISTS consent_source text;
CREATE TABLE IF NOT EXISTS tenant_dnc(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 phone_normalized text NOT NULL CHECK(phone_normalized ~ '^[1-9][0-9]{6,14}$'),
 reason text,
 source text NOT NULL DEFAULT 'manual',
 expires_at timestamptz,
 created_by uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(tenant_id,phone_normalized)
);
CREATE INDEX IF NOT EXISTS tenant_dnc_lookup_idx ON tenant_dnc(tenant_id,phone_normalized) WHERE expires_at IS NULL;
CREATE TABLE IF NOT EXISTS dialing_compliance_events(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 campaign_id uuid REFERENCES campaigns(id),
 contact_id uuid REFERENCES contacts(id),
 rule text NOT NULL,
 decision text NOT NULL CHECK(decision IN('blocked','allowed')),
 detail jsonb NOT NULL DEFAULT '{}'::jsonb,
 occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS dialing_compliance_events_tenant_idx ON dialing_compliance_events(tenant_id,occurred_at DESC);
INSERT INTO schema_migrations(version) VALUES('007_dialing_compliance.sql') ON CONFLICT DO NOTHING;
COMMIT;
