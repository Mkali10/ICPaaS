BEGIN;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS schema_migrations(
  version text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tenants(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  slug text NOT NULL UNIQUE CHECK(slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'),
  name text NOT NULL CHECK(length(name) BETWEEN 2 AND 160),
  status text NOT NULL DEFAULT 'active' CHECK(status IN('active','suspended','archived')),
  branding jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid REFERENCES tenants(id),
  email text NOT NULL,
  display_name text NOT NULL,
  password_hash text NOT NULL,
  password_salt text NOT NULL,
  roles text[] NOT NULL DEFAULT ARRAY['agent']::text[],
  status text NOT NULL DEFAULT 'active' CHECK(status IN('active','locked','disabled')),
  token_version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,email)
);
CREATE UNIQUE INDEX users_platform_email_unique ON users(lower(email)) WHERE tenant_id IS NULL;

CREATE TABLE telephony_nodes(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid REFERENCES tenants(id),
  node_key text NOT NULL UNIQUE,
  display_name text NOT NULL,
  engine_type text NOT NULL CHECK(engine_type IN('freeswitch','asterisk','generic_sip','external_provider','simulator')),
  ownership_mode text NOT NULL CHECK(ownership_mode IN('icpaas_managed','shared','external_unmanaged','provider_managed')),
  sip_endpoint text,
  control_endpoint text,
  secret_ref text,
  enabled boolean NOT NULL DEFAULT true,
  max_channels integer CHECK(max_channels IS NULL OR max_channels > 0),
  max_cps numeric(10,3) CHECK(max_cps IS NULL OR max_cps > 0),
  capabilities jsonb NOT NULL DEFAULT '{}'::jsonb,
  last_seen_at timestamptz,
  status text NOT NULL DEFAULT 'unverified' CHECK(status IN('unverified','ready','degraded','offline','draining')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE trunks(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  node_id uuid NOT NULL REFERENCES telephony_nodes(id),
  trunk_key text NOT NULL,
  display_name text NOT NULL,
  authentication_mode text NOT NULL CHECK(authentication_mode IN('ip','register','api','none')),
  remote_endpoint text NOT NULL,
  username text,
  secret_ref text,
  transport text NOT NULL DEFAULT 'tls' CHECK(transport IN('udp','tcp','tls','wss')),
  codecs text[] NOT NULL DEFAULT ARRAY['PCMA','PCMU']::text[],
  default_cli text,
  max_channels integer CHECK(max_channels IS NULL OR max_channels > 0),
  max_cps numeric(10,3) CHECK(max_cps IS NULL OR max_cps > 0),
  priority integer NOT NULL DEFAULT 100,
  enabled boolean NOT NULL DEFAULT true,
  status text NOT NULL DEFAULT 'unverified' CHECK(status IN('unverified','ready','degraded','offline')),
  configuration_revision bigint NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,trunk_key)
);

CREATE TABLE dids(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  trunk_id uuid NOT NULL REFERENCES trunks(id),
  number_e164 text NOT NULL CHECK(number_e164 ~ '^\+[1-9][0-9]{6,14}$'),
  use_for_inbound boolean NOT NULL DEFAULT true,
  use_for_outbound_cli boolean NOT NULL DEFAULT false,
  ownership_verified_at timestamptz,
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,number_e164)
);

CREATE TABLE processes(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  name text NOT NULL,
  process_type text NOT NULL CHECK(process_type IN('inbound','outbound','blended')),
  max_cps numeric(10,3) CHECK(max_cps IS NULL OR max_cps > 0),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,name)
);

CREATE TABLE campaigns(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  process_id uuid NOT NULL REFERENCES processes(id),
  name text NOT NULL,
  dialer_mode text NOT NULL CHECK(dialer_mode IN('manual','preview','progressive','predictive','agentless')),
  state text NOT NULL DEFAULT 'draft' CHECK(state IN('draft','scheduled','running','paused','completed','archived')),
  max_cps numeric(10,3) CHECK(max_cps IS NULL OR max_cps > 0),
  max_channels integer CHECK(max_channels IS NULL OR max_channels > 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,name)
);

CREATE TABLE routes(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  route_type text NOT NULL CHECK(route_type IN('inbound','outbound')),
  name text NOT NULL,
  did_id uuid REFERENCES dids(id),
  process_id uuid REFERENCES processes(id),
  campaign_id uuid REFERENCES campaigns(id),
  primary_trunk_id uuid NOT NULL REFERENCES trunks(id),
  failover_trunk_id uuid REFERENCES trunks(id),
  preferred_engine text,
  destination_pattern text,
  priority integer NOT NULL DEFAULT 100,
  enabled boolean NOT NULL DEFAULT true,
  configuration_revision bigint NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK((route_type='inbound' AND did_id IS NOT NULL AND process_id IS NOT NULL) OR (route_type='outbound' AND destination_pattern IS NOT NULL))
);

CREATE TABLE calls(
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  process_id uuid REFERENCES processes(id),
  campaign_id uuid REFERENCES campaigns(id),
  route_id uuid REFERENCES routes(id),
  trunk_id uuid REFERENCES trunks(id),
  engine_type text NOT NULL,
  engine_node_id uuid REFERENCES telephony_nodes(id),
  engine_call_id text,
  direction text NOT NULL CHECK(direction IN('inbound','outbound')),
  from_number text,
  to_number text NOT NULL,
  state text NOT NULL,
  selected_at timestamptz NOT NULL DEFAULT now(),
  answered_at timestamptz,
  ended_at timestamptz,
  hangup_cause text,
  selection_reason text,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX calls_engine_identity ON calls(engine_node_id,engine_call_id) WHERE engine_call_id IS NOT NULL;

CREATE TABLE plugins(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  plugin_key text NOT NULL,
  category text NOT NULL,
  display_name text NOT NULL,
  manifest jsonb NOT NULL,
  encrypted_configuration bytea,
  configuration_key_id text,
  status text NOT NULL DEFAULT 'disabled' CHECK(status IN('disabled','configured','ready','degraded','revoked')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,plugin_key)
);

CREATE TABLE quality_scorecards(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  name text NOT NULL,
  version integer NOT NULL,
  definition jsonb NOT NULL,
  status text NOT NULL DEFAULT 'draft' CHECK(status IN('draft','published','retired')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id,name,version)
);

CREATE TABLE quality_evaluations(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  call_id uuid NOT NULL REFERENCES calls(id),
  scorecard_id uuid NOT NULL REFERENCES quality_scorecards(id),
  reviewer_user_id uuid NOT NULL REFERENCES users(id),
  state text NOT NULL DEFAULT 'draft' CHECK(state IN('draft','submitted','disputed','final')),
  score numeric(8,3),
  result jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE audit_events(
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id uuid REFERENCES tenants(id),
  actor_user_id uuid REFERENCES users(id),
  event_type text NOT NULL,
  resource_type text NOT NULL,
  resource_id text,
  correlation_id uuid NOT NULL,
  before_state jsonb,
  after_state jsonb,
  source_ip inet,
  occurred_at timestamptz NOT NULL DEFAULT now(),
  integrity_hash text NOT NULL
);
CREATE OR REPLACE FUNCTION reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit_events is append-only'; END $$;
CREATE TRIGGER audit_events_immutable BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();

CREATE TABLE outbox_events(
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid REFERENCES tenants(id),
  event_type text NOT NULL,
  aggregate_id text,
  payload jsonb NOT NULL,
  idempotency_key text NOT NULL UNIQUE,
  occurred_at timestamptz NOT NULL DEFAULT now(),
  available_at timestamptz NOT NULL DEFAULT now(),
  claimed_until timestamptz,
  claimed_by text,
  attempts integer NOT NULL DEFAULT 0,
  completed_at timestamptz,
  last_error text
);

CREATE INDEX outbox_ready_idx ON outbox_events(available_at) WHERE completed_at IS NULL;
CREATE INDEX calls_tenant_created_idx ON calls(tenant_id,created_at DESC);
CREATE INDEX routes_tenant_type_idx ON routes(tenant_id,route_type,enabled);
CREATE INDEX trunks_tenant_status_idx ON trunks(tenant_id,status,enabled);

INSERT INTO schema_migrations(version) VALUES('001_core.sql') ON CONFLICT DO NOTHING;
COMMIT;
