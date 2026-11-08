# SSCOMMERCE — PHASE 9: PRODUCTION READINESS, DEPLOYMENT & ENGINEERING HANDOFF

**Status:** NOT STARTED. This document is a task specification, not evidence of completed deployment.

**Project:** SSCommerce — single-vendor physical-products store, TRY, modular monolith.
**Stack:** ASP.NET Core 10 / EF Core 10 / PostgreSQL 17; Next.js 16 / React 19 / TypeScript; Node 24; Playwright.
**Existing remote:** `salih12s/SSCommerce`, feature branch `feat/phase-3-4-complete` at `965ebad8829b65e1e165629ce37c95f01812d32a` when this specification was prepared. Re-read local Git before editing; do not assume it is unchanged.
**Scope:** Finish the final planned project phase, demonstrate a production-equivalent deploy, document a safe real-hosting path, establish a release/operational baseline, and prepare a code-grounded learning handoff for the owner.
**Out of scope:** Real payment, refund/capture, invented shipping/tax or collected revenue, automatic stock-reservation expiry, major redesign, RabbitMQ/Redis/microservices, and starting another project.

## 0. Non-negotiable boundaries

1. Read `CLAUDE.md`, `docs/PROGRESS.md` (latest checkpoints only), `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md` (D-061, D-065, D-068–D-076), and the Phase 7/8 frozen contracts only where needed. Inspect actual local branch, status, tracked/untracked files, remote state, and the running services. Treat GitHub `main` as old until proven otherwise.
2. Never overwrite or discard uncommitted work. Do not reset, clean, stash, force-push, rebase, merge, or switch branches without permission. Do not stage the untracked root reference PNG. No commits or pushes without explicit owner approval.
3. Development PostgreSQL port 5433, volume `sscommerce-pgdata`, and its existing users/data are protected. Isolated test PostgreSQL is on 5434 when available. All destructive/failure-injection tests must use an explicitly separate disposable environment. Never `docker compose down -v` or `EnsureDeleted` against development or production resources.
4. Never open, echo, include in traces, commit, upload, or print the root `.env`, database credentials, Data Protection key files, tokens or personally identifiable data. Respect the existing file deny rules. Use placeholder examples only; do not bypass restrictions by reading container env or process memory. If owner-only configuration is needed, provide exact safe steps and record `PENDING_OWNER_ACTION`.
5. Do not access, modify or deploy to a real remote hosting account, domain, live database, production GitHub secrets, DNS, billing settings, or branch protection without explicit permission and the necessary connection. No production schema writes, bootstrap, migration, database reset, or public release by implication.
6. Preserve the existing approved storefront, 11-route admin area, modular monolith, Idempotency-Key, order/stock transactions, `AdminOnly`/`WarehouseOrAdmin` role boundaries and CSRF/origin semantics. D-071 remains: Warehouse MUST NOT receive customer delivery PII without a new owner decision.
7. Do not introduce a payment-success signal, `paid` field, revenue KPI, or grand total. Existing orders are **unpaid operational records**; shipping/grand total may remain null. Do not create payment providers or assert a commercial checkout capability.
8. Current unpaid-order limits (default max 3 open / 10 placed per rolling 24 hours) are locally accepted but `PRODUCTION_ORDERING_POLICY_PENDING`. Do NOT silently approve them for public production. Because placing unpaid orders consumes stock with no automatic release, expose no unaudited public live ordering until the owner accepts the business risk and intended operating policy. A staging/demo environment should be access restricted or have order writes disabled by an explicitly tested, documented configuration if public exposure is unavoidable. Never pretend a UI-only disabled button secures an API.
9. Preserve separate truth labels: `LOCAL_PASS`, `STAGING_PASS`, `PRODUCTION_VERIFIED`, `BLOCKED_EXTERNAL`, `NOT_VERIFIED`, `PENDING_OWNER_APPROVAL`. No success by inference from configuration or historical tests.
10. Work sequentially through dependencies and report concisely in Turkish. If a mandatory external authorization blocks actual production, finish all safely executable tasks and stop with a precise owner-action checklist, not an idle loop.

## 1. Orchestration & token budget

- Use a **Sonnet 5.5 High** lead for routine engineering. Opus 5.5 High is optional for a single short architecture/security escalation if genuinely needed and available; do not let an Opus-heavy control loop consume the session.
- Existing specialists: `backend-dev`, `frontend-dev`, `database-dev`, `security-auditor`, `health-observer`, `qa-reviewer`. Assign genuine, narrowly scoped work to relevant agents at the right stage; do not summon all agents concurrently merely to satisfy a count.
- The lead owns release architecture, production configuration decisions, integration and the final acceptance verdict. `backend-dev` owns ASP.NET-specific readiness/security changes; `frontend-dev` owns Next custom-server deployment and frontend fixes; `database-dev` owns migration/backup/least-privilege DB work; independent `security-auditor` and `health-observer` review after implementation; `qa-reviewer` runs independent final gate.
- No two agents edit the same file concurrently. An agent must not stop another agent's process or container. Delegation brief: exact paths, objective, constraints, evidence and exit criteria. Agent handoff: AGENT / TASK / RESULT / FILES / COMMANDS+EXIT / FINDINGS / RISKS, <=25 lines.
- Avoid repo-wide rescans, giant logs, duplicate full test runs and repeated idle wakeups. A clean targeted gate should not be rerun until code changes that could affect it; run the **full final release gate after all fixes**.
- Update `docs/PROGRESS.md` at stable checkpoints so context-window/session-limit interruptions are recoverable. No new speculative “brain” system.

## 2. F9.0 — Baseline, architecture decision & deploy target

1. Confirm the current working tree and all Phase 0–8 artifacts. Review the last local Phase 8 evidence: reported backend Release 2,195 pass / 0 fail / 0 skip and Playwright 210 pass, but treat these as historical until rerun.
2. Audit host assumptions: ASP.NET API is private behind Next/proxy; client calls same-origin `/api/backend/*`; `apps/web/server.mjs` is a custom HTTP ingress that sanitizes forwarding headers; `apps/web/next.config.ts` rewrites API requests. Do not lose this behavior by using a generic `next start` or `output: 'standalone'` image without a documented and fully tested equivalent.
3. Choose the simplest **portable, isolated production-like deployment** reproducible on a local machine or Linux host. Prefer one reproducible stack (web + API + PostgreSQL + HTTPS edge), with separate environment and volumes from development; do not add Kubernetes/microservices solely for appearances. A containerized approach is fine only if Docker is available and does not collide with the development stack.
4. Document in `docs/DEPLOYMENT.md` the chosen topology, runtime/OS requirements, ports, origin/hostnames, trust boundary, data path, secret source, and deployment commands. If a hosting provider/domain is absent, give a provider-neutral runbook and demonstrate an isolated local production-like stack. Do not invent URLs, public IPs, provider accounts, DNS records, costs or readiness.
5. Define evidence-based acceptance thresholds for performance/availability before benchmarks. Do not invent SLA, p95 or throughput numbers. Separate baseline measurements from business targets awaiting owner approval.

## 3. F9.1 — Reproducible release artifacts

1. Provide maintainable, version-consistent release packaging (multi-stage build artifacts / Dockerfiles where suitable) for the .NET API and Next web. Avoid running as root when practical, use `.dockerignore`, exclude `.env`, test results and secrets, avoid dev dependencies at runtime, and pin runtime base versions responsibly.
2. **Next custom-server constraint:** retain `node server.mjs` and its `server/forwarding.mjs` and `server/privacy.mjs` imports. Verify that every runtime asset, `.next` output and `public/` file exists in the produced artifact. Do not use Next `standalone` output unless a complete equivalent for the custom server is proved and specifically approved.
3. Make `API_BASE_URL`, `PORT`, `WEB_HOST`, `WEB_TRUSTED_PROXIES`, `Security__AllowedOrigins__0`, `Security__TrustedProxies__*`, `ConnectionStrings__Postgres`, `DataProtection__KeysPath`, and `Ordering:*` deployment inputs explicit; defaults must not accidentally point staging/prod to the user's `127.0.0.1:5433` development DB. Be careful when env vars are build-time vs runtime for Next rewrites; validate production artifact behavior with the actual chosen base URL.
4. Keep PostgreSQL in a private network/volume, not publicly exposed. No baked-in production database passwords or fixed Admin credentials. Separate production schema migration identity from restricted runtime app identity where possible; the runtime role must not have capabilities to bypass append-only DB protections. Document any limitations.
5. Implement graceful termination/startup and health/ready handling; probe startup tolerance must be justified by actual observations (historical cold start near the 3-second readiness threshold). Liveness must not claim DB is healthy; readiness must not show success with missing migrations.
6. Release commands must be repeatable and non-destructive for a fresh isolated stack; make every dangerous command explicit, opt-in, and scoped. Document artifact versions/SHA and a quick rollback path. Test restarting API/web without losing PostgreSQL and Data Protection data.

## 4. F9.2 — HTTPS, auth cookies, trusted proxies & security baseline

1. Provide a trusted TLS-terminating reverse proxy plan/config (e.g. Caddy or Nginx, chosen for simplicity), with HTTPS and HTTP-to-HTTPS redirect where appropriate. For local validation use a clearly marked local certificate and non-public host; a local self-signed or development CA test is **not** proof of real public TLS issuance. Do not configure a live domain without approval.
2. Test via an actual HTTPS browser connection across edge -> Next `server.mjs` -> ASP.NET API -> PostgreSQL (not only an HTTP unit test). Verify `__Host-ssc.auth` and `__Host-ssc.csrf`, `Secure`, `HttpOnly`, `Path=/`, lack of Domain attribute, correct SameSite, login/logout, refresh and CSRF. Verify a browser cannot successfully forge an origin/header or reach private API/admin paths through a bypass.
3. Preserve IP rate limit integrity and `X-Forwarded-For`/`X-Forwarded-Proto` handling: only exact known reverse proxies may supply trusted forwarded values. Test spoofed `XFF`, wrong proxy peer, multiple proxy chain, cross-client partitions and direct HTTP bypass denial. Keep Next and API bound to private/loopback networks. Compare against ASP.NET Core KnownProxies and the app's `WEB_TRUSTED_PROXIES` implementation, do not wildcard trust internet peers.
4. `DataProtection__KeysPath` MUST be a durable location outside source control and writable only by the service identity, not transient container storage. Test real login cookie remains valid across an application restart / redeploy, and that a different environment does not share a key ring. Because plain XML keys may not be encrypted at rest, provide filesystem/volume encryption and access guidance; do not print the keys.
5. Adopt a tested, non-breaking security header policy for HTML and API, including suitable CSP, `frame-ancestors`, `nosniff`, Referrer-Policy and other relevant headers; account for Next scripts/styles/images. Do not blindly use `unsafe-inline` or break hydration. Test admin pages and storefront after tightening CSP, including potential script/style violations.
6. Production config must refuse or explicitly block unsafe missing required security values when appropriate; don't turn developer convenience settings into production overrides. No verbose stack traces, exception messages, connection strings, addresses/phones or personal order details in logs/API public responses.
7. Validate authorization (401/403/404), Admin vs Warehouse, audit of delivery view, session revocation, `last_admin`, CSRF, order/idempotency/stock, and rate limit handling, including 429 Retry-After. Security reviewer must report severity and tests.

## 5. F9.3 — Migration, backup, restore & disaster recovery

1. Inspect the real migration chain through `AddAdminOperations` and confirm schema/snapshot match, `dotnet ef migrations has-pending-model-changes` (or equivalent), fresh install and upgrade on an isolated clone. No destructive migration on dev/prod. Validate idempotent scripts and first-run order.
2. Implement repeatable database backup procedure (consistent `pg_dump -Fc`, controlled directory, secretless logs, retention policy left to owner if not specified) and demonstrate `pg_restore` into a disposable isolated database. Verify row counts/hash sampling and invariants across restore (catalog, users, stock balances/movements, carts, orders and admin audit); do not copy private user data into logs or the repo.
3. Write exact recovery/rollback runbook: restore a backup into a separately named database, verify it, then only with owner approval choose cutover; do not overwrite production/development blindly. Document that application rollback may be incompatible after a forward-only migration and cannot undo real business data.
4. Verify persistent keys/session semantics on restart; capture disk/resource failure indicators, backup validation and data retention as operational tasks.
5. Provide separate migration credentials/permissions guidance (runtime DB role should not own tables/disable critical triggers), least privilege, secure local backups and restore rehearsal evidence. Any destructive chaos drill must use disposable data only.

## 6. F9.4 — CI / release discipline / GitHub

1. Inspect `.github/workflows/ci.yml` and maintain Linux/Windows compatibility as needed. CI should cover backend Release build + real Postgres tests, web lint/typecheck/build, server/proxy tests and real API/Postgres Playwright tests. Keep a consistent single web build per job when feasible; avoid leaking fixtures into public API.
2. Fix only actual workflow issues. Verify workflow syntax locally with an available validator (e.g. `actionlint` / existing validator), plus reproducible local equivalent. Do not claim hosted runners passed if the GitHub Actions billing/account lock persists. `BLOCKED_EXTERNAL` is an independent status.
3. If GitHub account authorizations and owner approval are available, propose a **Draft Pull Request** from `feat/phase-3-4-complete` to `main`; never create/merge it without explicit approval. Review branch protection, required checks and deployment secrets only when authorized. Do not enable untrusted PR secrets exposure.
4. Explain release tagging, changelog, version selection, reproducible images, supply chain risk, secret scanning and rollback decisions. Do not automatically push tags, merge `main`, alter protection or deploy to public endpoints.
5. Keep tests/prod secrets out of workflow logs/artifacts; use fake credentials only for isolated CI Postgres. Do not mask GitHub billing problems by disabling checks.

## 7. F9.5 — Observability & operational health

1. Use the existing .NET logging/health primitives. Add lightweight structured telemetry **only where it provides concrete value**; OpenTelemetry traces/metrics may be added with a small, bounded instrumentation surface if feasible and justified. Do not introduce Kafka, RabbitMQ, Redis, an external APM service, Grafana stack, or cloud account just to pad the CV.
2. Capture at minimum: request count, error count, response duration distribution, status group, health/readiness transitions, DB connectivity, order placement/409/503/idempotency replay rates and proxy failures, with low-cardinality labels. Correlate requests across ingress and API using trace/correlation IDs without recording sensitive payloads. Do not expose `/metrics` publicly without access control.
3. Demonstrate telemetry with actual controlled requests and failure events, check that no cookies, CSRF secrets, raw SQL parameters, addresses, phones, emails, identifiers or tokens are leaked into logs/trace attributes. Explain retention and sampling defaults; separate verified from proposed dashboards.
4. Define an operator runbook for unhealthy DB, unavailable API, failed migration, excessive 429/503, slow checkout, suspicious ordering abuse, backup failure and session-loss-after-restart. Include alerts as configuration examples, not promises of an active hosted monitoring service.

## 8. F9.6 — Performance, resilience & full regression

1. Use disposable, isolated test databases and controlled localhost/staging targets. Do NOT load-test a real publicly hosted production site or the owner's development DB without separate authorization. Generate no real accounts/orders on unapproved external systems.
2. Define workloads and record real machine/host specs, tool/version, concurrency, request count, duration, success/error counts, p50/p95/p99 and throughput. Include storefront listing/search, auth, admin list/report, checkout preview, order placement, idempotent replay, stock contention, order cancellation, and readiness under moderate parallelism. Be aware that state-changing tests need fresh isolated data and cannot be naively repeated.
3. Verify stock never negative, one winner for final unit, exactly-once replay/cancel, no duplicate orders, price snapshots, safe 409/429/503 responses and test isolation. Inject network/database interruption only in disposable environment; show failure and recovery without data corruption. Identify bottlenecks based on evidence rather than inventing targets.
4. Run final exact commands against the final code and an isolated real PostgreSQL 17:
   - `dotnet build backend/SSCommerce.slnx -c Release`
   - `dotnet test backend/SSCommerce.slnx -c Release` (all required integration tests really execute; 0 silently skipped)
   - `npm run lint`, `npm run typecheck`, `npm run test:server`, `npm run build`, `npm run test:e2e`, `npm run test:proxy` from `apps/web`
   - Any new staging/HTTPS/security/load tests needed for a defensible release.
5. Don't assume 2,195 backend and 210 Playwright cases still pass; those are **historical Phase 8 counts**, not Phase 9 evidence. Record fresh actual counts, exit codes, test setup and limitations. Run Playwright at 390/768/1440, keyboard, accessibility, storefront/customer/admin/warehouse and real order workflows. Verify changes to admin are not hiding endpoint errors.
6. Independent `security-auditor`, `health-observer` and `qa-reviewer` gates happen on final code. Critical/High/Medium issues block acceptance until fixed or explicitly owner-approved with rigorous justification (never silently accept). For Low findings, record impact and disposition.

## 9. F9.7 — Realistic launch preparation and owner checkpoints

Prepare a release checklist that clearly separates these conditions:

### Local production-equivalent gate
- Build production artifacts; run a fully isolated production-mode stack using real PostgreSQL, HTTPS edge (local test cert clearly labelled), exact custom Next server, private API, secure cookie, stable Data Protection keys, end-to-end tests, backup/restore rehearsal and operational telemetry.
- Verify a restart preserves customer session, data and audit records; failures/recovery work as claimed.
- This can pass without a paid hosting account. Call it `LOCAL_PRODUCTION_EQUIVALENT_PASS`, never `LIVE_PRODUCTION_VERIFIED`.

### External staging gate (conditional)
- Requires owner-selected host/domain, DNS, network and TLS setup, funding, secrets and explicit access permissions. If missing, complete the local equivalent and provide actionable setup steps. Do not call externally hosted tests PASS or invent a URL.
- Real public CA TLS, internet clients, production-like reverse proxy, cookies on real TLS and remote host health must be verified **on that host** before marking `STAGING_PASS`.

### Live/public release gate (separate explicit authorization)
- Requires owner approval of public-release/business risks, especially D-061 unpaid stock consumption, D-071 Warehouse PII, D-072 production order limits, absence of real payment, and absent automated expiry. No user can be told that orders are paid or that they will be fulfilled automatically.
- Require documented owner decisions and sign-off for public access, database and DNS writes, production migration, secret deployment, GitHub PR/main merge, and release. Do not interpret this GOAL as any of those permissions.
- If release conditions are absent, **stop safely** at a reproducible, locally verified deliverable and report exactly what remains blocked. Never change business rules or invent customer communications to force the gate to pass.

## 10. Documentation and CV/learning handoff

Update, with actual evidence:
- `docs/PROGRESS.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `README.md`, `CLAUDE.md` (only as warranted, preserving prior history).
- `docs/DEPLOYMENT.md`: topology, environment config schema, commands, TLS edge, health, rollback, release artifacts, secret and DB least-privilege boundaries.
- `docs/OPERATIONS.md`: startup/shutdown, health/alerts, backup/restore, failure recovery, diagnostics and recurring maintenance; no secrets.
- `docs/SECURITY_REVIEW_PHASE_9.md`: threat surface, independent findings, evidence and accepted risks, HTTPS/cookie/proxy test evidence.
- `docs/PHASE_9_VERIFICATION.md`: final tests, exit codes, E2E, performance, migration verification, limitations and environmental provenance.
- `docs/SSCOMMERCE_LEARNING_MAP.md`: an **owner-focused, source-grounded** guided walkthrough index for later lessons. Show real source paths, not fictional ones: Storefront -> Next ingress/proxy -> ASP.NET endpoint -> Application -> Domain -> EF/Postgres; catalog, Identity/cookie+CSRF, inventory, cart, order placement and idempotency, concurrency/locks, Admin/RBAC, migration, CI, logging, deployment, tests. For each module give 3–5 essential concepts and 2 sample technical interview questions. No full code dump. This is a roadmap for a separate learning series, not a substitute for teaching the code now.
- A clear `docs/RELEASE_NOTES.md` or release-readiness section identifying what the application **does** (unpaid order workflows and operational management) and **does not** (payment, shipping pricing, real collected revenue, automatic reservation expiry).

Avoid stale roadmap statements like “waiting for Phase 3 commit.” Keep the distinction between committed feature-branch evidence, a PR, a `main` merge, a local demo and external production.

## 11. Acceptance gates and final report

The lead must check, with real evidence:

G1. Existing Phase 0–8 behaviors/regression tests still work; no fabricated test PASS.
G2. Reproducible production-mode web/API/PostgreSQL artifacts with isolated volumes and no dev DB collisions.
G3. Real HTTPS path in a local production-like stack and correct secure-cookie/CSRF/forwarding behavior; clearly labelled if not publicly trusted TLS.
G4. Data Protection key persistence across restarts and secret safety.
G5. DB migration fresh/upgrade, backup-and-restore drill, least privilege and a non-destructive recovery runbook.
G6. CI workflow validated locally; hosted CI accurately labelled `PASS` or `BLOCKED_EXTERNAL`, never inferred.
G7. Observability and health/recovery tested; no sensitive-data logging.
G8. Bounded load/performance benchmark and critical order/inventory consistency tests in isolated environment.
G9. Independent Security, Health and QA reviews on final code; zero unresolved Critical/High/Medium absent explicit decision.
G10. Complete operations, deployment, security, evidence and **learning map** documentation, with source paths and CV-relevant architecture decisions.

**Completion label policy:**
- If G1–G10 pass in a fully local isolated production-equivalent environment but remote hosting/real public TLS/CI remain blocked, report `PHASE 9: LOCAL_PRODUCTION_EQUIVALENT_PASS / EXTERNAL_DEPLOYMENT_PENDING` and list concrete blocked decisions. This is a successful technical phase, **not** a claim that the site is publicly live.
- If any local gate fails, report `PHASE 9: INCOMPLETE` with the precise failed gate and next action.
- Only if a real authorized hosted deployment has been observed and tested may the report say `PHASE 9: STAGING_VERIFIED`. `LIVE_PRODUCTION_VERIFIED` requires explicit public release consent and all corresponding checks.
- Always report `PENDING_GIT_APPROVAL` until the owner separately approves final commits, push, PR/merge or tag creation.

**Final report to the owner (Turkish):** current branch and exact local HEAD; changed files; new release artifacts; executed commands with pass/fail/skip and exit codes; HTTPS/cookie/Data Protection evidence; DB backup/restore evidence; telemetry and real measured benchmarks; Security/Health/QA findings; external CI and hosting status; unresolved owner decisions and business risks; linkable source paths for the next SSCommerce teaching series. Stop. Do not automatically begin a new project or the learning lessons.

## 12. Source references for implementation (not substitutes for actual testing)

- Next.js custom server guidance: https://nextjs.org/docs/app/guides/custom-server
- ASP.NET Core reverse proxies and forwarded headers: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0
- ASP.NET Core Data Protection configuration: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0
- GitHub Actions workflows: https://docs.github.com/en/actions
- Existing frozen contracts: `docs/CONTRACT_PHASE_7.md`, `docs/CONTRACT_PHASE_8.md`; existing decisions are authoritative until deliberately revised.
