BEGIN;
CREATE TABLE IF NOT EXISTS contact_queues(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 name text NOT NULL,
 strategy text NOT NULL DEFAULT 'longest_idle' CHECK(strategy IN('longest_idle','round_robin','fewest_calls','ring_all')),
 max_wait_seconds integer NOT NULL DEFAULT 60 CHECK(max_wait_seconds>0),
 wrap_up_seconds integer NOT NULL DEFAULT 20 CHECK(wrap_up_seconds>=0),
 enabled boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(tenant_id,name)
);
CREATE TABLE IF NOT EXISTS dispositions(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 name text NOT NULL,
 code text NOT NULL,
 category text NOT NULL CHECK(category IN('connected','not_connected','callback','invalid','completed','other')),
 parent_id uuid REFERENCES dispositions(id),
 callable boolean NOT NULL DEFAULT false,
 requires_remark boolean NOT NULL DEFAULT true,
 callback_required boolean NOT NULL DEFAULT false,
 enabled boolean NOT NULL DEFAULT true,
 sort_order integer NOT NULL DEFAULT 100,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(tenant_id,code)
);
ALTER TABLE processes
 ADD COLUMN IF NOT EXISTS did_id uuid REFERENCES dids(id),
 ADD COLUMN IF NOT EXISTS queue_id uuid REFERENCES contact_queues(id),
 ADD COLUMN IF NOT EXISTS number_masking boolean NOT NULL DEFAULT false,
 ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 3 CHECK(max_attempts>0),
 ADD COLUMN IF NOT EXISTS retry_delay_minutes integer NOT NULL DEFAULT 60 CHECK(retry_delay_minutes>=0),
 ADD COLUMN IF NOT EXISTS recording_enabled boolean NOT NULL DEFAULT true,
 ADD COLUMN IF NOT EXISTS working_hours jsonb NOT NULL DEFAULT '{}'::jsonb;
CREATE TABLE IF NOT EXISTS process_agents(
 process_id uuid NOT NULL REFERENCES processes(id) ON DELETE CASCADE,
 user_id uuid NOT NULL REFERENCES users(id),
 priority integer NOT NULL DEFAULT 100,
 enabled boolean NOT NULL DEFAULT true,
 PRIMARY KEY(process_id,user_id)
);
CREATE TABLE IF NOT EXISTS process_dispositions(
 process_id uuid NOT NULL REFERENCES processes(id) ON DELETE CASCADE,
 disposition_id uuid NOT NULL REFERENCES dispositions(id),
 PRIMARY KEY(process_id,disposition_id)
);
CREATE TABLE IF NOT EXISTS contact_lists(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 name text NOT NULL,
 source_filename text,
 total_rows integer NOT NULL DEFAULT 0,
 valid_rows integer NOT NULL DEFAULT 0,
 duplicate_rows integer NOT NULL DEFAULT 0,
 status text NOT NULL DEFAULT 'ready' CHECK(status IN('uploading','ready','archived','failed')),
 created_by uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS contacts(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 list_id uuid NOT NULL REFERENCES contact_lists(id) ON DELETE CASCADE,
 phone_number text NOT NULL,
 first_name text,
 last_name text,
 external_id text,
 attributes jsonb NOT NULL DEFAULT '{}'::jsonb,
 attempt_count integer NOT NULL DEFAULT 0,
 last_disposition_id uuid REFERENCES dispositions(id),
 last_called_at timestamptz,
 next_callback_at timestamptz,
 state text NOT NULL DEFAULT 'fresh' CHECK(state IN('fresh','queued','dialing','connected','callback','completed','invalid','exhausted')),
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(list_id,phone_number)
);
CREATE INDEX IF NOT EXISTS contacts_campaign_pick_idx ON contacts(tenant_id,state,next_callback_at,attempt_count);
ALTER TABLE campaigns
 ADD COLUMN IF NOT EXISTS list_id uuid REFERENCES contact_lists(id),
 ADD COLUMN IF NOT EXISTS number_masking boolean NOT NULL DEFAULT false,
 ADD COLUMN IF NOT EXISTS scheduled_at timestamptz,
 ADD COLUMN IF NOT EXISTS started_at timestamptz,
 ADD COLUMN IF NOT EXISTS stopped_at timestamptz;
CREATE TABLE IF NOT EXISTS campaign_contacts(
 campaign_id uuid NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
 contact_id uuid NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
 state text NOT NULL DEFAULT 'queued' CHECK(state IN('queued','reserved','dialing','connected','disposed','callback','failed','skipped')),
 attempts integer NOT NULL DEFAULT 0,
 assigned_agent_id uuid REFERENCES users(id),
 last_call_id uuid REFERENCES calls(id),
 last_error text,
 queued_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(campaign_id,contact_id)
);
CREATE INDEX IF NOT EXISTS campaign_contacts_pick_idx ON campaign_contacts(campaign_id,state,queued_at);
CREATE TABLE IF NOT EXISTS call_outcomes(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 call_id uuid REFERENCES calls(id),
 campaign_id uuid REFERENCES campaigns(id),
 contact_id uuid REFERENCES contacts(id),
 agent_user_id uuid REFERENCES users(id),
 disposition_id uuid NOT NULL REFERENCES dispositions(id),
 sub_disposition_id uuid REFERENCES dispositions(id),
 remark text,
 callback_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS rechurn_jobs(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 source_campaign_id uuid NOT NULL REFERENCES campaigns(id),
 target_campaign_id uuid NOT NULL REFERENCES campaigns(id),
 disposition_ids uuid[] NOT NULL,
 state text NOT NULL DEFAULT 'draft' CHECK(state IN('draft','running','completed','cancelled','failed')),
 eligible_count integer NOT NULL DEFAULT 0,
 queued_count integer NOT NULL DEFAULT 0,
 created_by uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 completed_at timestamptz
);
CREATE TABLE IF NOT EXISTS agent_presence(
 tenant_id uuid NOT NULL REFERENCES tenants(id),
 user_id uuid NOT NULL REFERENCES users(id),
 process_id uuid REFERENCES processes(id),
 campaign_id uuid REFERENCES campaigns(id),
 state text NOT NULL DEFAULT 'offline' CHECK(state IN('offline','available','reserved','ringing','on_call','wrap_up','break')),
 last_state_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(tenant_id,user_id)
);
INSERT INTO schema_migrations(version) VALUES('005_contact_center.sql') ON CONFLICT DO NOTHING;
COMMIT;