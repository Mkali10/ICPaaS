# ICPaaS

ICPaaS is a multi-tenant, white-label contact-center control plane for browser-based WebRTC calling. It manages customer workspaces, SIP infrastructure, DIDs, routes, agents, queues, processes, campaigns, lead data, call outcomes, recordings and supervisor operations from one application.

The project supports managed FreeSWITCH ESL and Asterisk ARI control paths. Generic SIP gateways can be connected for basic originate/control operations, but advanced browser-agent delivery, supervision and managed recordings currently require FreeSWITCH or Asterisk.

> **Current maturity:** active development. The human-agent dialer foundation is functional and CI-tested, but the product has not yet passed a real-carrier production acceptance test. Review [Current status](#current-status) and [Known limitations](#known-limitations) before deployment.

## What the product is

ICPaaS is designed for three operating levels:

- **Platform operator / reseller:** provisions customer workspaces, service limits, telephony nodes, billing accounts and branding.
- **Tenant / client:** configures trunks, DIDs, routes, queues, processes, campaigns, data lists, users and outcomes.
- **Agent and supervisor:** handles WebRTC calls, works assigned leads, records dispositions and monitors live operations.

The intended execution chain is:

```text
Tenant
  -> SIP node and trunk
  -> DID and inbound/outbound route
  -> Process (queue, agents, outcomes, CPS, recording policy)
  -> Campaign (mode, data list, channels, lifecycle)
  -> Lead reservation and dialer worker
  -> FreeSWITCH / Asterisk call
  -> Browser WebRTC agent
  -> Disposition, callback or rechurn
  -> Recording, reporting and supervisor operations
```

## Implemented capabilities

### Identity and multi-tenancy

- Platform administrator, tenant owner/admin, supervisor, agent, auditor and billing roles.
- Tenant-safe JWT authentication and workspace-aware login.
- Customer provisioning, user creation, password reset, session revocation and account status.
- Per-tenant plan, channel, CPS, agent-seat, storage and recording-retention settings.

### SIP and telephony infrastructure

- Managed telephony nodes and runtime database resolution.
- FreeSWITCH ESL, Asterisk ARI and generic SIP control adapters.
- SIP trunk, DID, primary/failover route and destination-pattern configuration.
- Node/trunk/DID/route enable-disable lifecycle.
- Trunk verification, provisioning jobs and managed test calls.
- Answer, hangup, hold, resume, DTMF, transfer and bridge controls.

### Contact-center configuration

- Queues with longest-idle, round-robin, fewest-calls and ring-all configuration values.
- Processes for inbound, outbound and blended workloads.
- Agent and disposition assignment.
- Dispositions and sub-dispositions with callback/callable rules.
- Number masking, retry limits, recording policy, CPS and working-hours storage.
- Agent movement between processes.

### Campaigns and data

- CSV contact-list import and validation.
- Campaign create/edit and draft, running, paused, completed and archived lifecycle.
- Manual, preview, progressive and predictive mode configuration.
- CPS and channel leases, lead reservation and retry scheduling.
- Campaign start, pause, resume, stop and archive controls.
- Fresh, queued, dialing, connected, callback, disposed, failed and skipped lead states.
- Campaign counters and lead drill-down.
- Callback-aware lead selection.
- Disposition-selected rechurn jobs with duplicate-safe target seeding.
- Tenant DNC suppression, consent metadata and process-specific calling windows.
- Worker and manual-dial compliance checks with auditable block decisions.

### Agent workspace

- Agent-specific login and SIP/WebRTC endpoint assignment.
- SIP.js browser phone registration.
- Incoming answer/reject and active-call mute, hold, DTMF and hangup controls.
- Manual dial restricted by campaign mode.
- Assigned campaign/lead context, masked number and prior lead information.
- Atomic manual/preview lead claim.
- Required disposition, remark and optional callback submission.
- Paired customer/agent call lifecycle and automatic wrap-up transition.

### Inbound calling

- FreeSWITCH XML dialplan lookup by DID.
- Inbound DID -> route -> process -> queue registration.
- Atomic available-agent reservation.
- WebRTC agent ringing and customer/agent bridging.
- Queue timeout and capacity enforcement.
- Requeue when an inbound agent rejects or misses the delivery.

### Supervisor operations

- Tenant-safe Live Monitor screen.
- Live call, duration, process, campaign and agent state.
- Agent presence and calls-today counters.
- Supervisor force-hangup.
- FreeSWITCH listen, whisper and barge through eavesdrop.
- Asterisk listen, whisper and barge through ARI snoop/mixing bridges.

### Recordings

- Process-controlled automatic recording start for FreeSWITCH and Asterisk.
- Recording metadata, SHA-256 integrity and retention expiry.
- Node-key authenticated ingest with a hard 50 MB file limit.
- Tenant-isolated persistent local storage.
- Authorized playback without a public media URL.
- Automatic retention cleanup.

### Integrations, quality and operations

- Generic webhook/plugin configuration and delivery retry/dead-letter foundation.
- Quality scorecards and evaluations.
- Audit-event and operations-health foundations.
- Customer billing ledger and credit adjustment screens.
- Date-filtered call, campaign, agent and disposition reports.
- Authorized calls/outcomes CSV exports.
- PostgreSQL 17 migrations, Docker Compose profiles and installer tooling.

## Calling modes

| Mode | Behaviour |
|---|---|
| Manual | Agent selects or enters a destination and starts the call. |
| Preview | Agent claims and reviews the next eligible lead before calling. |
| Progressive | One eligible lead is reserved for an available agent and dialled under CPS/channel limits. |
| Predictive | Worker calculates a bounded dial batch from available agents and campaign capacity. Current pacing is rule-based, not a statistical abandonment-rate model. |
| Inbound | DID resolves to a process queue and rings an available browser agent. |

## Configuration order

Use this order for a new tenant:

1. Platform administrator creates the customer workspace and owner.
2. Tenant owner creates agent/supervisor users.
3. Assign a SIP/WebRTC extension to each browser-phone user.
4. Create a FreeSWITCH, Asterisk or generic SIP connection.
5. Add/verify trunks and DIDs.
6. Create inbound and/or outbound routes with failover where required.
7. Create queues, dispositions and sub-dispositions.
8. Create a process and assign DID, queue, agents, outcomes, CPS and recording policy.
9. Import a CSV contact list for outbound work.
10. Create a campaign with its mode, list, channel and CPS limits.
11. Set agents to available and start the campaign.
12. Verify a complete call, disposition, callback/rechurn and recording cycle.

## Runtime architecture

| Component | Purpose |
|---|---|
| ASP.NET Core API | Authentication, administration, workers, APIs and web console. |
| PostgreSQL 17 | Tenant configuration, calls, leads, leases, outcomes, recordings and audit data. |
| Redis | Bundled runtime capability for future high-scale coordination; current safety-critical leases use PostgreSQL. |
| FreeSWITCH ESL | SIP/media control, endpoint delivery, bridge, recording and supervision. |
| Asterisk ARI | Channel control, endpoint delivery, bridge, recording and supervision. |
| SIP.js | Browser WebRTC phone. |
| CoTURN | NAT traversal for WebRTC media. |

The API and telephony engine may run on separate servers. Node secrets are referenced as `env:VARIABLE`; secret values must not be stored in database configuration.

## Deployment profiles

- `standalone`: API plus bundled PostgreSQL; Redis and media services can be enabled through profiles.
- `application`: API connected to external infrastructure.
- `distributed`: application and infrastructure services deployed independently.
- `media-bundled`: bundled CoTURN ports and relay range.

The installer entry point is:

```bash
./scripts/icpaas install
```

Do not deploy by copying example secrets. Generate independent strong values for JWT, bootstrap, node, database, Redis and TURN secrets. See [Release Operations](docs/RELEASE_OPERATIONS.md) for the operational procedure.

## Essential environment configuration

Copy `.env.example` to `.env` and configure at minimum:

```text
ICPaaS__Security__JwtSecret
ICPaaS__Security__BootstrapKey
ICPaaS__Security__NodeKey
POSTGRES_PASSWORD
REDIS_PASSWORD
TURN_REALM
TURN_SHARED_SECRET
ICPaaS__PublicEndpoints__WebSocketUrl
ICPaaS__Media__TurnRealm
ICPaaS__Media__TurnSharedSecret
```

Managed node/trunk secrets use environment references such as:

```text
env:PRIMARY_FREESWITCH_ESL_PASSWORD
env:PRIMARY_ASTERISK_ARI_PASSWORD
env:CARRIER_SIP_PASSWORD
```

## Recording delivery

Recordings are created on the telephony node. The completed WAV must be uploaded to:

```text
PUT /internal/recordings/{recordingId}
X-ICPaaS-Node-Key: <node key>
Content-Type: audio/wav
```

The request is rejected above 50 MB. In distributed deployments, install the uploader on every FreeSWITCH/Asterisk recording node:

```bash
sudo ./scripts/icpaas install-recording-uploader https://control.example.com
systemctl status icpaas-recording-uploader.timer
```

The installer uses the API node key from `.env` and runs a systemd-sandboxed uploader every minute. It does not change PBX recording-directory ownership. Successful files are archived under `.uploaded`; files that exhaust ten attempts are moved to `.failed` with the last API response for diagnosis. Browser playback is authorized through the API and does not expose a permanent public object URL.

## Development and CI

GitHub CI verifies:

- .NET restore and Release build.
- Docker Compose configuration.
- PostgreSQL 17 migrations.
- Migration idempotency and expected migration count.
- Release-tooling syntax checks.

Useful local checks:

```bash
node --check src/IcpaaS.Api/wwwroot/supervisor.js
node --check src/IcpaaS.Api/wwwroot/recordings.js
sh -n scripts/icpaas
docker compose -f compose.yml config --quiet
```

## Release acceptance

Run authenticated API and configuration checks against the deployed server:

```bash
export ICPAAAS_ACCEPTANCE_API_URL=https://control.example.com
export ICPAAAS_ACCEPTANCE_EMAIL=owner@example.com
export ICPAAAS_ACCEPTANCE_PASSWORD='tenant-owner-password'
export ICPAAAS_ACCEPTANCE_WORKSPACE=workspace-slug
./scripts/acceptance.sh
```

To include a real managed SIP originate, explicitly provide a destination and optional CLI. This can create a chargeable carrier call:

```bash
export ICPAAAS_ACCEPTANCE_DESTINATION=+919876543210
export ICPAAAS_ACCEPTANCE_CALLER_ID=+911234567890
./scripts/acceptance.sh
```

Backups include PostgreSQL, environment configuration, Data Protection keys and recordings. Validate an archive without restoring it:

```bash
./scripts/icpaas backup
./scripts/icpaas verify-backup backups/icpaas-YYYYMMDDTHHMMSSZ.tar.gz
```

## Current status

| Area | Status |
|---|---|
| Multi-tenant identity and roles | Implemented |
| Trunks, DIDs and routes | Implemented; real-carrier acceptance pending |
| FreeSWITCH managed calling | Implemented; live acceptance pending |
| Asterisk managed calling | Implemented; live acceptance pending |
| WebRTC agent phone | Implemented; device/network hardening pending |
| Manual/preview/progressive execution | Implemented |
| Predictive execution | Basic bounded pacing; advanced pacing pending |
| Inbound queue delivery | Implemented for managed FreeSWITCH path; broader acceptance pending |
| Dispositions, callbacks and rechurn | Implemented foundation; workload acceptance pending |
| Live monitor and supervision | Implemented; live acceptance pending |
| Secure recordings | Implemented with node uploader, retry and quarantine lifecycle |
| DNC, consent and calling-hour enforcement | Implemented foundation; regulatory policy review required |
| Calling, campaign, agent and disposition reports | Implemented with CSV exports |
| Billing | Ledger/UI foundation; runtime charging enforcement pending |
| Native CRM/messaging plugins | Not implemented |
| IVR/visual flow builder | Not implemented |
| AI voice agents | Not implemented |
| HA/DR and production observability | Partial architecture/foundation |

## Known limitations

- No production SIP carrier or WebRTC browser matrix has been acceptance-tested in this repository environment.
- Predictive pacing does not yet implement statistical answer-rate/abandonment-rate modelling.
- `ring_all` is stored as a queue strategy, but the current inbound worker reserves one agent per delivery cycle.
- FreeSWITCH/Asterisk recording directories must be mounted or configured at `/var/lib/icpaas/recordings` for the bundled uploader contract.
- Recording output is WAV; GSM/Opus archival transcoding and external S3-compatible object storage are pending.
- IVR, visual call-flow builder, approved prompt/audio library and AI voice-agent execution are pending.
- DNC, consent and calling-window enforcement is implemented; jurisdiction-specific TRAI/DOT policy review, workflow evidence and number-change approvals remain pending.
- Native WhatsApp, SMS, Zoho, Odoo, Salesforce and payment/appointment plugins are pending; current integrations are generic webhook foundations.
- Billing balances are not yet debited by live usage and do not currently block calls automatically.
- Advanced reports, scheduled exports, wallboards and recording-quality analytics are pending.
- WebRTC microphone/speaker/ringtone device selection and detailed ICE diagnostics need completion.
- Full HA, database replication, multi-node leader election, automated failover drills and restore acceptance are pending.

## Remaining release gates

The product must not be called production-ready until these gates pass:

1. Real inbound and outbound calls through the intended carrier.
2. Chrome/Edge WebRTC registration, two-way audio and NAT/TURN tests.
3. Manual, preview, progressive and predictive campaign acceptance datasets.
4. Pause/resume/stop, retry, callback and disposition-selected rechurn verification.
5. Inbound queue timeout, reject/requeue and concurrent-agent tests.
6. FreeSWITCH and Asterisk hold, transfer, DTMF, supervision and recording tests.
7. Recording node upload, 50 MB rejection, retention and restore tests.
8. Tenant-isolation, role-authorisation and rate-limit security tests.
9. PostgreSQL backup/restore and complete server rebuild drill.
10. Capacity, CPS, channel, failure and recovery load tests.

## Documentation

- [Release operations](docs/RELEASE_OPERATIONS.md)
- [Versatile architecture](docs/VERSATILE_ARCHITECTURE.md)

## Licence and compliance

No open-source licence is currently declared in this repository. Treat the source as proprietary unless the repository owner adds an explicit licence.

ICPaaS provides technical controls, not legal certification. Each operator is responsible for carrier contracts, consent, DNC, recording disclosure, data retention, privacy, TRAI/DOT and other jurisdiction-specific requirements.
