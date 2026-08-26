# ICPaaS Release Operations

These commands apply only to the ICPaaS repository, normally `/opt/icpaas/app`. They do not apply to Llamar.

## Install

Run `chmod +x scripts/icpaas` and then run the installer with `ICPAAAS_PROFILE=standalone ./scripts/icpaas install`.

Supported profiles are `standalone`, `application`, and `distributed`. Standalone uses bundled PostgreSQL 17. Application and distributed profiles require external PostgreSQL connection variables in `.env`. Add `ICPAAAS_MEDIA_BUNDLED=true` when bundled CoTURN is required.

The installer validates Docker Compose, generates secrets, protects `.env` with mode 600, bundles pinned SIP.js locally, builds the API, runs idempotent migrations, starts services, and verifies liveness/readiness.

## Doctor, backup, update

- `./scripts/icpaas doctor`
- `./scripts/icpaas backup`
- `./scripts/icpaas update`

Updates create a database backup first, reject a dirty Git tree, use fast-forward-only pull, migrate, restart, and verify health.

## Restore

Restore replaces the target database and therefore requires explicit authorization:

`ICPAAAS_CONFIRM_RESTORE=RESTORE ./scripts/icpaas restore backups/icpaas-YYYYMMDDTHHMMSSZ.tar.gz`

Copy backups away from the application server, encrypt them at the storage layer, and regularly test restoration on an isolated host.

## Offline installation

Provide the pinned browser runtime with `SIPJS_SOURCE=/media/sip-0.21.2.min.js ./scripts/icpaas install`. Container images must also be preloaded when registry access is unavailable.

Never run Git or Compose commands from `/root`. Enter `/opt/icpaas/app` first.
