BEGIN;
ALTER TABLE tenant_settings
 ADD COLUMN IF NOT EXISTS plan_key text NOT NULL DEFAULT 'flex',
 ADD COLUMN IF NOT EXISTS agent_limit integer NOT NULL DEFAULT 10 CHECK(agent_limit>0),
 ADD COLUMN IF NOT EXISTS storage_limit_gb integer NOT NULL DEFAULT 25 CHECK(storage_limit_gb>0),
 ADD COLUMN IF NOT EXISTS recording_retention_days integer NOT NULL DEFAULT 90 CHECK(recording_retention_days>0);

CREATE TABLE IF NOT EXISTS billing_accounts(
 tenant_id uuid PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
 currency char(3) NOT NULL DEFAULT 'INR',
 billing_mode text NOT NULL DEFAULT 'prepaid' CHECK(billing_mode IN('prepaid','postpaid','unmetered')),
 credit_balance numeric(18,4) NOT NULL DEFAULT 0,
 credit_limit numeric(18,4) NOT NULL DEFAULT 0,
 low_balance_threshold numeric(18,4) NOT NULL DEFAULT 0,
 status text NOT NULL DEFAULT 'active' CHECK(status IN('active','hold','suspended')),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS credit_ledger(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 amount numeric(18,4) NOT NULL CHECK(amount<>0),
 entry_type text NOT NULL CHECK(entry_type IN('credit','debit','adjustment','refund')),
 reference text,
 note text,
 actor_user_id uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS credit_ledger_tenant_idx ON credit_ledger(tenant_id,created_at DESC);
INSERT INTO billing_accounts(tenant_id) SELECT id FROM tenants ON CONFLICT DO NOTHING;
INSERT INTO schema_migrations(version) VALUES('004_reseller_control.sql') ON CONFLICT DO NOTHING;
COMMIT;
