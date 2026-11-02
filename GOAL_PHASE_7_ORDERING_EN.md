# SSCommerce — Phase 7: Transactional Ordering (Autonomous Multi-Agent Goal)

**Goal version:** 1.0 — 2026-11-01  
**Repository:** `salih12s/SSCommerce`  
**Target:** Phase 7 only. **Do not start Phase 8 or Phase 9.**  
**Language:** All engineering instructions, contract descriptions, and agent handoffs in English; progress and final user reports in Turkish.

## 0. Execution contract and existing project

This is an **existing application**, not a greenfield project. Read the repository's actual local files and project brain before editing. Pay special attention to:

- `CLAUDE.md`, `.claude/settings.json`, `.claude/agents/*.md`, existing GOALs, project brain / `Durum.md` / `Last-Session` / `Threads` / knowledge (where available).
- `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/PROGRESS.md`, `docs/CONTRACT_PHASES_5_6.md`.
- Existing Catalog, Identity, Inventory, Cart, Addresses and Checkout services, endpoints, tests, EF migrations and API-to-Next proxy.
- `git branch --show-current`, `git status --porcelain=v1 -uall`, tracked/untracked modifications, `git log -5 --oneline`, local and remote tracking state.

The last **verified GitHub state** before this goal was `feat/phase-3-4-complete` at `a689910d5a83c4a2bfddbc1615819e5b350d8f5f`, two commits ahead of `main` (`9c562f7`). **Do not assume the current local state still matches this.** Preserve every uncommitted change, accepted storefront design, image, migration, record, database volume and backup. Never use destructive Git commands to make the tree clean.

Phases 0–6 were reported **locally passed**, with 1,286 .NET tests, 138 Playwright tests and 6 Node tests in the final earlier run. These are historical figures, **not new evidence**. CI was previously blocked; check its present status instead of assuming it has recovered.

**Prerequisite gate:** verify functional Phase 3–6 code and migrations. If a genuine prerequisite is broken, repair only what is necessary for Phase 7 and document it. Never silently rewrite earlier phases. If it cannot be safely repaired, stop with a single `BLOCKED` report.

## 1. Lead and subagent architecture

- Lead: **Claude Opus 5.5, high effort**. Own architecture, scope, dependency sequencing, integration, risk decisions and acceptance. Delegate meaningful implementation/review work rather than personally coding the entire phase.
- Specialists: **Claude Sonnet 5.5, high effort** using the repository's existing explicit subagent definitions. Do not invent nonexistent sessions or claim model telemetry not observed.
- **Actually invoke all six existing specialist agents** with distinct, useful Phase 7 tasks:
  1. `database-dev`: data model, additive EF migration, constraints, stock-allocation and idempotency persistence review.
  2. `backend-dev`: ordering application/domain/API implementation, state machine, transaction coordinator, unit/integration tests.
  3. `frontend-dev`: order confirmation, order history/detail/cancellation and checkout UI integration, Playwright tests, approved visual language.
  4. `security-auditor`: authenticated ownership/IDOR, CSRF, replay/key reuse, privilege boundaries, PII/log leakage, order-state tampering review.
  5. `health-observer`: ready/migration checks, DB outages, transaction failures, cold-start and recovery behavior, operational assertions.
  6. `qa-reviewer`: independent, preferably read-only, acceptance and regression review, negative/concurrency testing and evidence audit.
- Assign work in dependency order. Parallelize **non-overlapping** file ownership only. Database migrations/model snapshot have a **single owner**. Backend/service and frontend/API contract changes must be coordinated, not raced.
- Subagents report **directly to the lead** using: `Task | Changed/reviewed paths | Decisions | Actual commands/results | Findings | Risks | Next action`, preferably ≤25 lines. No giant logs or repeated full-repo scans. The lead must verify decisive evidence before declaring PASS.
- Never run six redundant full regression suites merely to prove that six agents were used. Each specialist must contribute substantively, without duplicated busywork or idle Agent Teams.

## 2. Phase boundary (non-negotiable)

**Build:** A real order placement workflow from the authenticated customer's current cart and selected delivery address; secure persisted orders and immutable snapshots; stock consumed atomically; idempotency; customer order history and detail; safe cancellation/restock; appropriate minimal staff-side status transitions; Next.js customer flow.

**Do not build:** payment gateway, payment capture, fake `paid` flag, refunds, shipping-provider integration, fabricated shipping charge or grand total, cart stock reservations, background payment jobs, ERP dashboards, broad admin CRUD UI, multi-vendor/B2B, coupon/tax engines, or Phase 8+ features.

Currency is TRY. Domain/DB money is `decimal` / `numeric(18,2)`; API money is integer minor units (`long`, `...Minor`). Persist time in UTC (`timestamptz`). **The backend is the single authority** for prices, quantity, stock, user ownership and order state. Never trust request-provided price, subtotal, stock, order status or user ID.

**Important product disclosure:** No shipping rate or tax engine is configured. Snapshot the **merchandise subtotal**, set `shippingStatus = shippingNotCalculated`, `shippingMinor = null`, and `grandTotalMinor = null`. Do not present the merchandise subtotal as a fully payable grand total. Order placement must visibly state that **no payment is collected** and shipping is not yet calculated. A Phase 7 order is a real *unpaid / awaiting fulfillment* operational record, not a completed paid sale. Document that consuming inventory without collecting payment is an intentional limited MVP trade-off; no automatic expiry/release is implemented.

## 3. Architecture and API contract gate (lead + database/backend)

Before coding, record a compact, reviewable API + persistence contract under `docs/CONTRACT_PHASE_7.md`; record consequential decisions in `docs/DECISIONS.md`. Follow established API conventions (`/api/v1`, ASP.NET Identity HttpOnly cookie, CSRF/Origin, ProblemDetails, rate limit, no-store, same-origin proxy). Avoid generic repositories, unnecessary MediatR/CQRS or external queues.

Suggested additive tables/records (adapt names to existing conventions, explain any differences):

- `orders`: UUID, `user_id`, idempotency key and request fingerprint (unique **per user**, or equivalent safe uniqueness), status, immutable delivery-address snapshot, item subtotal, nullable shipping/grand total, currency, created/updated time, optional state transition/version fields. **No live reference is substituted for address snapshot.**
- `order_items`: order FK, variant ID where useful, immutable SKU/name/variant label, unit price, quantity, line subtotal, currency and safe optional image metadata; check constraints and valid numeric ranges.
- `order_inventory_allocations` or equivalent: order item + warehouse + variant + quantity consumed; preserved to restock the **original warehouse(s)** exactly once. No silent single-warehouse assumption.
- `order_status_history`: order FK, prior/new state, actor, UTC timestamp, safe reason, audit trail. Status transitions must be explicit and tested.
- Additional integrity constraints/indexes as justified. Order history and allocations are append-only or suitably mutation-restricted; avoid updates that erase financial snapshots. Update the existing `stock_movements` operation-type constraint and ledger vocabulary **additively and consistently** to represent order decrement and cancellation reversal, without exposing those internal operations through public admin adjustment inputs.

**Migration:** create one forward-only, additive `AddOrdering` migration and EF snapshot; inspect generated SQL. Do not edit or squash the four existing migrations. Test **both** fresh database migration and upgrade on a backed-up existing database; verify previous catalog, identity, stock, carts and addresses survive unchanged. Do not run `down -v`, reset the dev DB, or drop user data.

Recommended HTTP surface (adapt minimally if existing routing conventions require it):

| Method | Route | Expected behavior |
|---|---|---|
| `POST` | `/api/v1/orders` | Authenticated, CSRF/Origin protected; `Idempotency-Key` required; body selects `addressId`, no client prices. Success `201` with stable order representation, replay `200` (or consistent documented replay contract); mismatched reuse `409`. |
| `GET` | `/api/v1/orders` | Current user's paginated orders only; never other users' orders; no-store. |
| `GET` | `/api/v1/orders/{id}` | Current user's order detail, items, immutable address snapshot and status history; others' orders return indistinguishable `404`. |
| `POST` | `/api/v1/orders/{id}/cancel` | Customer cancellation only in explicitly permitted state; idempotent cancellation / no double restock; appropriate status on illegal transitions. |
| `GET` | `/api/v1/admin/orders` | Minimal protected staff listing only if essential to Phase 7 status management; no ERP UI. |
| `POST` | `/api/v1/admin/orders/{id}/status` | Minimum role-protected staff transition needed for Phase 7 fulfillment lifecycle; reject arbitrary transitions; CSRF required. |

Use the existing `mutation` rate limiter where appropriate. Define stable error codes for empty cart, inactive item, changed price requiring explicit confirmation, insufficient stock, invalid address, idempotency conflict, lock contention, illegal transition and DB unavailable. Response behavior must match existing `ApiResults` conventions. Never expose email/phone/address in URLs or logs. `GET` has no side effects.

## 4. Transactional order placement (critical acceptance path)

Implement **one PostgreSQL transaction** that atomically encompasses: idempotency claim, cart read under user-scoped lock, address validation, current product/variant/warehouse checks, current server-side price calculation, deterministic per-warehouse stock allocation and conditional decrement, order + item/address snapshots, stock ledger records, status history, and cart clearing. **Commit everything or nothing.**

Order of operations, locks and idempotency must be explicit and consistent:

1. Validate authenticated principal; Idempotency-Key format/length/entropy policy and request body. Scope key uniqueness to user (or carefully justified stricter policy). Fingerprint only canonical **client-intended request fields**, not mutable cart state, so successful retries replay after the cart has been cleared.
2. Acquire a transaction-scoped lock for `(userId, key)` before reading the cart; check persisted request fingerprint first. Identical replay returns the first successful order without stock/cart mutations; changed payload with same key returns `409`. **Never expose another user's order via key collision.**
3. Lock the user's cart row (`FOR UPDATE`) to serialize placement with cart writes; validate selected address belongs to the principal. Refresh all variant activity, availability and prices **inside the transaction**, never from prior checkout preview or `unit_price_at_add`.
4. If prices changed since the UI preview, require explicit fresh user confirmation rather than silently executing at an unexpected price. Define a server-checked preview/quotation token, expected merchandise subtotal with concurrency-safe validation, or another sound minimal mechanism. A stale or tampered client value must not set the actual order price.
5. Allocate stock from **active warehouses** in a deterministic ordering; use row-level locks and **conditional UPDATE ... WHERE on_hand_quantity >= @quantity RETURNING** (or demonstrably equivalent) against the same transaction. Keep quantities non-negative. Multiple line items and warehouses must have deterministic lock ordering to limit deadlocks. Document what happens if fulfillment requires multiple warehouses.
6. Store immutable price, name/SKU and delivery address snapshots; record stock-movement entries with useful order references and allocations for future cancellation. Stock decrements and audit entries must be in this same transaction.
7. Clear or consume the cart only after the order has been validated, within that same transaction. A failed transaction leaves original cart, inventory, idempotency namespace, and history unchanged. Store idempotent success before committing.
8. Commit, then return persisted result. A lost response followed by the same request/key must replay the original success, even if the cart is now empty or the product's price has changed. Do not automatically retry a partially unknown transaction by generating a fresh idempotency key.

A stock reservation is **not** created during cart or preview. Stock is consumed at successful order commit only. A rejected order must not consume stock, produce a stock movement, clear a cart or claim a successful idempotency key.

## 5. Status transitions and cancellation

Define a small explicit state machine with names appropriate to a no-payment MVP. Suggested states: `placed`, `processing`, `shipped`, `completed`, `cancelled`.

- `placed -> processing | cancelled`
- `processing -> shipped | cancelled` (only if cancellation is operationally safe; otherwise document and test a narrower rule)
- `shipped -> completed`
- `completed`, `cancelled`: terminal.

Customers may cancel only their own `placed` orders. Staff transitions must require `WarehouseOrAdmin` or `AdminOnly` policy according to current business rules, and server-side authorization; no role may be supplied by the client.

**Exact-once cancellation inventory:** Two parallel cancels must yield at most one status transition and exactly one reversal per allocation. Restock the original warehouse(s), even if a warehouse has since been deactivated (inventory still physically belongs to it), unless a documented, tested alternative preserves stock accounting. If restoring would violate max-on-hand, fail atomically with a recoverable operational status; never silently lose stock. Preserve immutable movement history. Repeated cancel should be an idempotent no-op or a consistent conflict; never double-add. Status updates and cancellation must serialize to avoid a shipped-and-cancelled order.

## 6. Customer experience (frontend-dev)

Preserve the **approved Phase 2 storefront design**: dark/charcoal navigation, warm light content, orange accents, responsive product layouts and mobile bottom navigation. No redesign.

- Extend `/checkout` to display the current valid address, cart items, server-calculated merchandise subtotal, **shipping not calculated** and **no payment is collected**. Do not display fabricated grand totals.
- Show an explicit action e.g. **"Sipariş oluştur — ödeme alınmaz"**, enable it only after validation/fresh confirmation and CSRF readiness; prevent accidental double click and retain the same idempotency key for retries of the same user intention.
- On success, navigate to `/siparisler/{id}` or a safe confirmation route. Add `/siparisler` and order-detail pages for the current user; expose the permitted cancel operation with confirmation. Never display another person's order, including via cached HTML/RSC/prefetch.
- Correct loading, empty, price-changed, out-of-stock, inactive item, double-submit, request timeout, login expiry, 401/403/404/409/429/503 and unknown-result states. When a response is lost, query/retry **with the same key**, not a new order.
- User-specific routes and API responses must be private/no-store and inaccessible to other sessions. Maintain accessible names, keyboard flow, focus, labels, 390/768/1440 responsive checks and no bottom-bar overlap.
- If the dev database has zero stock or no admin account, **do not fake stock or build an insecure public admin bootstrap**. Use the isolated E2E environment for actual order-flow verification; note the limitation.

## 7. Evidence gates and required adversarial tests

All new and relevant old tests must run against a **real isolated PostgreSQL 17** database, as in earlier phases. Mock-only or InMemory tests are not enough for transactional correctness. No claimed PASS without actual exit codes, counts and skipped-test status.

### Persistence / correctness

- Fresh migrations + upgrade migrations on a backed-up database; old 10-product/4-category/19-variant/8-image development data remains intact (check actual current counts rather than assuming historical counts).
- Order placed from valid cart/address; stock decremented; correct ledger + allocations + immutable money/name/address snapshots; cart cleared; customer history/detail correct.
- Invalid input, empty cart, missing or other-user address, inactive variant, missing stock, price update and price overflow: **no partial writes** or cart loss.
- Changing a product price or shipping-address record after ordering does **not** change historic order snapshots.
- Order items reflect purchased variant and per-warehouse quantity; warehouse aggregates reconcile to movements.

### Idempotency / races

- **20+ simultaneous requests with the same key**: exactly one order, one set of stock movements and one cart clear; remaining identical requests replay; different payload same key `409`.
- **20+ users compete for final unit**: at most one successful order; non-negative inventory; losers retain their cart and receive a meaningful conflict.
- Concurrent admin stock adjustment vs ordering, multiple items/warehouses, cart mutation vs order placement, two parallel cancels, cancellation vs staff shipment; no lost updates, oversell, double-restock or deadlock loops.
- Forced failure after first stock decrement but before order completion must fully roll back stock, order, movements, cart and idempotency claim.
- Lost HTTP response after commit, retry with same key: no duplicate, original order returned. Cancellation retried after lost response: no double stock return.

### Security / resilience

- 401 on anonymous requests; CSRF/Origin enforcement; cross-user IDs return 404; role-protected staff transitions return 403 to customers; no IDOR, no overposting (`userId`, `priceMinor`, `status`, `paid` ignored/rejected).
- No cache or HTML leaks of order PII; no delivery phone/address or secret values in logs/error details; safe ProblemDetails for failures.
- Wrong/malformed/reused keys and large payloads; rate limit; login/logout/session invalidation; actual Next proxy + browser cookie/CSRF path.
- DB outages / lock timeout return safe 503; no falsely successful order. Probe health still honest; avoid hiding the known cold-start readiness issue using test-only retries.

### Builds / UI / independent review

- `dotnet build backend/SSCommerce.slnx -c Release`
- Real-DB `dotnet test backend/SSCommerce.slnx -c Release` with all applicable tests, **0 failed and 0 skipped** at final acceptance; run a second full or focused concurrency/replay pass for confidence.
- `apps/web`: `npm run lint`, `npm run typecheck`, `npm run test:server`, `npm run test:e2e`, `npm run test:proxy` (adapt only where existing scripts require it). Report exact counts and whether E2E started a real API + PostgreSQL.
- `git diff --check`, explicit tracked/untracked audit, no real `.env`/keys, credentials, local reference PNG, backups, test artifacts, sensitive data or machine-specific paths staged.
- `security-auditor`, `health-observer` and `qa-reviewer` each provide an independent report, with unresolved high/critical findings preventing PASS and medium findings explicitly fixed or accepted with rationale and regression coverage.

## 8. Documentation, acceptance and handoff

Update existing `docs/CONTRACT_PHASE_7.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/ROADMAP.md`, `docs/PROGRESS.md`, and the existing project brain/Last-Session/Threads/knowledge in accordance with their current schemas. Correct stale commit-approval labels for Phases 2–6 where confirmed. Include concise, reproducible PowerShell instructions in README for development and tests; never embed credentials.

Final report in **Turkish**, containing:

1. Phase 7 work packages and status (`PASS`, `FAIL`, `NOT_VERIFIED`, `BLOCKED_EXTERNAL`, `PENDING_GIT_APPROVAL`).
2. Actual files/migrations/endpoints/state transitions; decisive implementation choices.
3. Real-DB and UI test command outcomes, counts, failures/skips and concurrency evidence.
4. Independent six-specialist handoffs, security/health findings and exceptions.
5. Current Git branch, HEAD, tracked/untracked changes, recommended two-commit or cohesive staging strategy, remote CI status.
6. Known risks (no payment/shipping totals, delayed stock release, missing real TLS/phone/load tests, health-check cold start) and next steps.

**Local technical completion is different from publication and remote CI.** Stop after Phase 7 and **do not commit, push, merge, reset, force-push, destroy data, create paid external resources or begin Phase 8** without explicit approval. You have approval to do safe, reversible Phase 7 development and isolated test-DB work, not irreversible operations. If a truly required decision or permission blocks progress, provide **one** succinct BLOCKED report, do not create recurring wakeups or idle loops. If the autonomous goal runner has its own limits, obey those; never promise background continuation.

### Sources for implementation review

- https://www.postgresql.org/docs/17/transaction-iso.html
- https://www.postgresql.org/docs/17/explicit-locking.html
- https://learn.microsoft.com/en-us/ef/core/saving/transactions
- Existing SSCommerce ADRs and Phase 5–6 contract are the primary project-specific authority.
