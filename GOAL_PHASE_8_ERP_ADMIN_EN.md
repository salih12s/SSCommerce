# SSCommerce — PHASE 8 GOAL: ERP / ADMIN OPERATIONS

Status: READY FOR USER-INITIATED EXECUTION — NOT YET IMPLEMENTED
Owner: Lead Orchestrator (Claude Opus 5.5 High)
Specialists: existing six Claude Sonnet 5.5 High subagents
Scope: Phase 8 only; Phase 9 deployment is excluded
Source baseline: `feat/phase-3-4-complete` at `1f18c3570331f1dfdd77b3b9198faa4b76a5af89` (verify against the actual local checkout before touching anything).

## 0. Mission and non-negotiable boundaries

Make SSCommerce operable by a real store owner through a secure, responsive `/admin` interface and server-owned management APIs. Build operational catalog administration, existing warehouse/inventory workflows, order processing, access controls, operational dashboards, and explicit protections for unpaid orders consuming stock. This is **not** a cosmetic mockup: all approved actions must use real ASP.NET Core APIs and PostgreSQL 17, with independent integration and browser evidence.

Preserve Phases 0–7, the approved storefront visual language, all existing migrations, idempotent ordering, immutable snapshots and stock movements, and the current transaction/lock ordering. Do not edit already-applied migrations. No real payment or refunds, shipping fee computation, grand-total fabrication, reservation timers, automated expiration jobs, ERP-vendor synchronization, invoice/fiscal document issuance, arbitrary external image upload or URL ingestion, or public production deployment. Do not implement Phase 9. Never imply `placed` is paid, and never label unpaid order subtotals as collected revenue.

Never commit, push, merge into `main`, remove reference PNG, inspect/export private `.env` secrets, reset local work, drop data, or recreate the persistent DB volume without separate explicit user approval. A successful local goal ends with `LOCAL_COMPLETED / PENDING_GIT_APPROVAL`.

## 1. Establish the real local baseline before design or edits

1. Read `CLAUDE.md`, `.claude/settings.json`, `docs/{PROGRESS,ROADMAP,ARCHITECTURE,DECISIONS}.md`, `docs/CONTRACT_PHASE_7.md`, and the existing Phase 5–6 contract. Verify branch, HEAD, remote, staged/unstaged/untracked paths, existing work and actual available agent definitions. The GitHub baseline is not a substitute for the working tree.
2. Read concrete source and tests for `AdminEndpoints`, `InventoryEndpoints`, `OrderEndpoints`, catalog/domain mutations, Identity, CSRF, rate limits, `OrderPlacementService`, `OrderStatusService`, stock movement history, web custom server and private-page caching. Discover exact class and file names rather than assuming them.
3. Check Docker and local PostgreSQL non-destructively. For any DB migration: backup, test a restore against an isolated DB, then apply only a reviewed, additive migration with user authorization appropriate to existing local conventions; do not change passwords or delete data. Never print connection strings or real credentials.
4. Before implementation, write a concise **Phase 8 contract** in `docs/CONTRACT_PHASE_8.md`: routes, request/response types, privileges, input limits, state transitions, audit rules, catalog referential integrity, concurrency and error codes, and test acceptance matrix. Resolve contradictory code assumptions in the contract first.
5. A Phase 8 implementation may add a new feature branch only with the user's authorization; otherwise preserve the existing feature branch and keep all Phase 8 work uncommitted.

## 2. Specialist orchestration — real invocations, scoped handoffs

The Opus lead is the **sole integrator and acceptance authority**. Invoke all six actual, pre-existing specialists for substantive, separate tasks — never pretend a review happened. Confirm actual model reported by each agent, but mark independent telemetry `NOT_VERIFIED` if unavailable.

- `database-dev` — catalog/admin persistence changes, additive migration and constraints, transaction-safe audit schema, data-preservation/upgrade verification, query indexes and bounded pagination. Own `backend/src/SSCommerce.Infrastructure/Persistence/`.
- `backend-dev` — catalog management API, dashboard/report queries, authorization-safe staff operations, order abuse controls, audited writes, validation and integration tests. Own backend outside `Persistence/`; coordinate contract with database-dev.
- `frontend-dev` — responsive `/admin` routes, secure auth redirects, catalog/stock/orders/operations UX, real proxy integration and Playwright tests. Own `apps/web/`.
- `security-auditor` — independent **read-only** review of privilege separation, role elevation, CSRF, PII, XSS, image paths, tampering, order abuse, no-store and race conditions. Do not ask this agent to write code or run tools that it does not have.
- `health-observer` — independent **non-destructive** readiness, slow query, startup, DB outage, proxy and service recovery checks; report runtime versus static evidence honestly.
- `qa-reviewer` — independent read-only regression, real PostgreSQL concurrency, migrations, API and E2E verification; use the existing testing guide. Never allow QA to edit implementation.

Give each agent: precise ownership, inputs/contract, forbidden files/actions, measurable acceptance tests, and a concise evidence-based handoff (FILES, TESTS, RISKS, HANDOFF). Parallelize disjoint file sets; never let multiple agents write `Program.cs`, `SSCommerceDbContext`, migration snapshot or `CLAUDE.md` concurrently. The lead integrates and reviews all cross-module changes; avoid duplicate broad repository scans and giant log dumps.

## 3. F8.1 — Admin authentication, navigation and safe bootstrap

Build one admin area in the existing Next.js app, not a second application: `/admin`, `/admin/urunler`, `/admin/kategoriler`, `/admin/stok`, `/admin/depolar`, `/admin/siparisler`, and appropriate detail/edit screens. Keep the premium storefront's approved charcoal/off-white/orange design language but distinguish operational screens. Provide 1440, 768 and 390 px layouts with keyboard navigation, focus, loading/empty/error states, and accessibility checks.

Authorization is enforced on **every backend endpoint**, never only by hiding React buttons. Use the existing same-origin `/api/backend/*` proxy, cookie auth, Origin + CSRF protection, and trusted-proxy controls. Every admin page must be `private, no-store` and must not embed private data in static HTML/RSC, browser storage or analytics. Redirect anonymous users to login with a validated internal return URL; show a real 403 for authenticated users without sufficient role. A `Warehouse` user may see only the explicitly authorized staff order workflow; do not grant Admin catalog/inventory/role administration by accident.

Currently there is **no default Admin account** in development (0 users after dev cleanup). Provide an explicit, local, auditable, secure bootstrap procedure for creating/promoting the first Admin without a hard-coded/default password or a public role-escalation HTTP route. Prefer an interactive local command or a similarly controlled out-of-band method; document prerequisites and use. Production bootstrap requires separate authorization, never automatic startup elevation. The development bootstrap must be tested with an isolated DB and must not silently create a persistent dev account without the user's approval.

If staff role management is included, allow only Admin to grant/revoke roles; reject self-demotion, removal of the last Admin, and any unauthorized privilege escalation; invalidate affected sessions securely. Do not silently implement registration as Admin. Prefer the minimum set of role-management endpoints actually needed for Phase 8; record role changes in an audit trail.

## 4. F8.2 — Manage the real product catalog

Create Admin-only CRUD-style operations appropriate to the current Domain model for categories, products, variants/SKUs, and linked product imagery. Use backend validation plus PostgreSQL constraints: unique category/product slugs and SKUs, numeric(18,2) TRY money with API `long` minor units, nonnegative prices, bounded specifications, variant option signatures, UTF-8/control/bidi safety consistent with existing validators, active/inactive states, category parenting with no cycles, image primary uniqueness and stable sort order.

Allow creation and controlled editing, activation/deactivation, category assignment, variant price changes, and approved product image metadata/path selection. Avoid hard-delete of entities referenced by carts, inventory, snapshots or orders; deactivate/archive instead. Never mutate prior order-item price/product snapshots or existing stock movement history. Guard against simultaneous conflicting edits with optimistic concurrency (e.g. explicit version/ETag or appropriate row checks) where a stale admin update could overwrite a newer price or product status. Report a clear 409 conflict rather than silent last-writer-wins.

Keep public storefront queries filtered to active entities; admin queries can show inactive entities. Images: existing `ProductImage.StorageKey` is a local `/images/...` key and credits are tracked. Do **not** invent a file-upload service, permit arbitrary external URLs, SSRF, path traversal or uncredited images. Either select currently approved local assets, or provide a documented no-upload workflow; truthful UI matters more than a fake uploader. New product records must not depend on placeholder fixtures.

## 5. F8.3 — Warehouse and inventory operations

Use existing Admin-only warehouse, inventory-balance, receiving, adjustment and immutable movement APIs wherever possible. Build an accessible warehouse list/edit screen and stock ledger UI: warehouse code/status, SKU search, pagination, current per-warehouse quantity, movement reason/actor/UTC time, and honest error states.

For stock `receive`/`adjust`, generate and retain a valid `Idempotency-Key` across network uncertainty. If the first response is unknown, retry with the **same** key and identical payload or reload the authoritative ledger; do not create a new stock movement accidentally. Render on-hand and available counts correctly and do not claim a stock reservation exists. Refuse insufficient-stock writes; do not expose warehouse breakdown in public catalog responses.

Respect existing `AdminOnly` restriction for warehouse/inventory reads and writes (D-045). `Warehouse` staff must **not** gain stock-adjustment privileges merely because a Warehouse dashboard exists; any scope expansion requires a new explicit policy and user approval.

## 6. F8.4 — Orders, fulfillment workflow and privacy

Build admin order list/detail and authorized status progression with filters, bounded paging, stable ordering and replay-safe retry handling. Reuse `OrderStateMachine` and existing staff order status endpoints. Preserve permitted transitions: `placed -> processing -> shipped -> completed`, cancellation from `placed` or `processing`, Customer only own `placed` cancellation, Admin authorized cancellation (including refunding stock to **original allocation warehouses**), Warehouse only forward transitions. Never bypass these rules with an admin "edit status" SQL update.

Existing staff list DTO intentionally omits customer PII. Any new detailed shipping/address view needs an explicit Admin-only or narrower purpose-built authorization rule, minimum necessary fields and `no-store`. Do not expose delivery address/phone in list views, URLs, logs, RSC, exports, or Warehouse views by default. If a Warehouse view needs address details, treat this as a security decision requiring explicit approval rather than loosening authorization. Do not add payment, paid/refunded flags, shipping fees or fabricated grand totals.

Cancelled orders must stay immutable as history except permitted status metadata; preserve one-time stock return and replay semantics. For Admin cancellation use the existing endpoint or an authorized extension with the same service and invariants; never duplicate stock-return logic in a new service.

## 7. F8.5 — Mitigate unpaid-order stock blocking (D-061, D-067)

This is a central Phase 8 security/operational gate. Currently an unpaid order permanently consumes stock until explicitly cancelled, and customers can cycle order/cancellation to create records. Implement and test **server-side**, not UI-only, guardrails against a single account creating unlimited outstanding unpaid orders. The policy is a proposed baseline, not a hidden business decision:

- Proposed configurable default: maximum **3** customer orders in `placed` or `processing` combined and maximum **10** newly placed orders per rolling 24 hours; determine whether counting staff/other roles changes the rule. The user must approve final production thresholds before public checkout is enabled.
- Ensure that concurrent requests with different idempotency keys cannot bypass either limit. Integrate into the established Phase 7 lock order with explicit deadlock analysis, and enforce atomically before any order/stock/cart mutation. Successful idempotent replays must **not** count again. Rejected attempts must not consume an idempotency key, stock, or cart.
- Provide stable 409/429 problem codes and user-understandable messages, and show correct guidance in checkout UI. Do not silently cancel, auto-expire, or free stock: reservation jobs/payment-time expiry are outside MVP.
- Admin dashboard must show aged `placed`/`processing` orders and support manual review/cancellation using the existing transaction-safe status/cancellation mechanism. Clearly show the date of placement, age, status and unpaid nature; avoid inventing a payment state or due amount.
- Consider direct database-only invalid order status transitions (D-067). Decide whether an **additive** DB trigger/constraint plus transition-specific test is appropriate; if not, document why application-level `OrderStateMachine` remains the only writer contract. Never edit historic `AddOrdering` migration.
- Document the remaining multi-account abuse risk and the fact that no real payment means production ordering still requires an explicit go-live decision. If the business thresholds are not approved, mark `PRODUCTION_ORDERING_POLICY_PENDING` without blocking otherwise valid Phase 8 local implementation.

## 8. F8.6 — Operational dashboard, reports and auditability

Build a genuinely useful Admin dashboard, backed by bounded indexed PostgreSQL queries rather than client-side fake data: order counts by lifecycle status, aged unpaid/open orders, low-stock/out-of-stock SKU count (threshold explicit), product/variant counts, recent stock movements, and recent order events. Show UTC timestamps converted to the user's locale on the client. Clearly distinguish **unpaid order subtotal** from **collected revenue**; payment receipts do not exist, therefore collected revenue must not be inferred from orders. Keep `grandTotalMinor` null where shipping is unknown.

Add narrowly scoped immutable Admin audit records for material catalog/price/status changes, role changes if supported, and sensitive operational changes not already covered by `stock_movements` or `order_status_history`. Record actor, operation, target, timestamp and non-sensitive old/new fields; do not log password, session, full addresses, phone or secrets. Prefer appending audit and the associated business change within one transaction. Add an additive migration only if actually necessary; index filters and cap result sets to prevent costly unbounded scans.

Dashboard read models and reports must never cause writes. Admin API/SSR data must have `Cache-Control: no-store`/private caching controls as appropriate, safe error responses, and bounded query parameters. Provide empty-state views for the real development DB with no Admin or stocked products, not fake metrics.

## 9. F8.7 — Security, health, concurrency, regression and end-to-end proof

Run all existing Phase 0–7 regressions plus new focused checks. The gate is real evidence, not only success labels.

Backend and PostgreSQL: Release build; integration suites against PostgreSQL 17 with zero skipped DB tests; fresh/upgrade migration proof with backup/restore; category cycle and uniqueness; duplicate SKU and image primary; stale-price conflict; price minor-unit correctness; stock immutable log and order snapshots unchanged after catalog edits; order list authorization and PII isolation; Admin 401/403, Warehouse 403 for stock writes, Customer 403 for Admin; forged body fields; malformed JSON; CSRF and Origin; no-store and safe errors; 409/429; role changes immediately revoked where applicable.

Abuse and concurrency: high-contention different-key placement for one account, same-key 24+ idempotent replays, 24 customers racing for last unit, two parallel cancellations only one return, active-order/rolling-quota bypass attempts, concurrent stock adjustments, failure rollback (no partial audit/order/stock mutation), optimistic catalog edit conflict, no deadlocks in lock-order stress runs. Test lock-removal or equivalent meaningful mutation to prove the concurrency suite catches real regressions; restore code after mutation.

Web: `npm run lint`, `npm run typecheck`, `npm run test:server`, full build and Playwright E2E on 1440/768/390, axe and keyboard access, login/403 handling, route privacy, Admin CRUD and stock workflow with real API/database, Admin order handling, Warehouse-restricted view, no PII leak in HTML/RSC/URL, invalid input errors, 5xx/network-retry safety, anonymous/customer role denials. Existing storefront/carts/orders flows must remain green. Preserve the test harness's isolated database and stock seeding; do not create a persistent default Admin in real dev data.

`security-auditor` must independently review privilege/escalation, XSS/untrusted product text, role/session revocation, CSRF, idempotency, private data, audit logs and abuse controls. `health-observer` must independently check liveness/readiness, DB-outage behavior, query performance/boundedness, cold-start and recovery, and proxy health. QA must independently validate final changed code and run full suites. Do not claim real TLS, real physical phone, remote CI, load/stress/soak or external integrations were verified unless actually executed. Keep external GitHub CI billing lock separate from local PASS.

Record exact commands, numeric pass/fail/skip counts, environment, exit codes, notable defects and fixes, nonverified checks and risk acceptances. Do not silently weaken assertions or mask flaky tests with retries; establish cause and meaningful signal.

## 10. Documentation, Git and completion gates

Update `docs/CONTRACT_PHASE_8.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/PROGRESS.md`, README and `CLAUDE.md` to match implementation. Maintain existing project brain rather than recreating it. Technical contract/source comments can be English; provide concise progress and final reports to the user in Turkish.

Do not change Git stage/index, create a commit, push, create a PR, merge, or start Phase 9 without explicit approval. Leave the excluded root reference PNG untracked. Run a secret scan on any suggested staged manifest without reading private secrets; provide a two-commit proposal only after local verification (code/tests/migrations, then docs).

The Opus lead may declare `LOCAL_COMPLETED / PENDING_GIT_APPROVAL` **only when**:

1. Baseline, existing work and current branch were accurately established and protected.
2. Real Admin navigation, permissions and a safe first-Admin setup procedure are implemented and tested, with no secret/default account leak.
3. Admin catalog workflows create/edit/deactivate real entities and preserve historical orders and inventory integrity.
4. Admin warehouse/stock screens use existing transaction-safe idempotent endpoints without opening Warehouse write permissions.
5. Admin order workflows respect the Phase 7 state machine, preserve PII restrictions and return stock to the original allocations exactly once.
6. Server-side unpaid-open-order safeguards are concurrency-safe; production threshold policy and residual risks are explicitly documented (pending owner approval is called out, not concealed).
7. Operational dashboard uses real data, truthful financial labels, bounded queries, and auditable material mutations.
8. Full backend, real PostgreSQL, frontend, Playwright, security and health gates have actual evidence; tests and known limitations are accurately reported.
9. All six specialist agents were actually invoked and returned distinct, substantive, verifiable reports to Opus.
10. Project brain/docs updated; no secrets, no destructive DB changes, no unauthorized Git operations, no Phase 9 work.

If blocked, emit one concise `BLOCKED` report with exact missing prerequisite/authorization and stop; no repeated waiting loops. Avoid gratuitous model calls, duplicate investigations and overlapping file ownership. Limit autonomous execution to 50 meaningful work cycles, but never trade away verification to meet this cap.

## 11. Initial execution plan

Step A: Opus validates actual local checkout, model and contracts. Step B: invoke security-auditor for baseline privileges and health-observer for DB/service baselines, while database-dev inspects schema implications without writing until the contract freezes. Step C: Opus freezes `docs/CONTRACT_PHASE_8.md`, decides file ownership and tasks. Step D: database-dev and frontend-dev work on disjoint files; backend-dev starts after schema and contract are ready. Step E: integrate and run targeted tests. Step F: security/health independent gates and QA full regressions. Step G: Opus reviews evidence, updates docs and reports status in Turkish. Stop for Git approval.
