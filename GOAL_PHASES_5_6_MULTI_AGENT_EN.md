# SSCommerce — GOAL: Phase 5 Inventory + Phase 6 Cart / Checkout

**Objective:** Complete Phase 5 (warehouses and inventory) and Phase 6 (cart, customer addresses, and pre-order checkout preview) in the **existing SSCommerce repository**, preserving all Phase 0–4 implementation work and the approved storefront design.

**Out of scope:** Phase 7 order placement; Phase 8 full ERP/admin dashboard; real payments and payment providers; stock reservations; shipping-provider/rate integrations; coupons, discounts, and returns.

**Execution model:** Claude Code main agent **Opus 5.5 / high** is the lead/orchestrator, not the default implementer. Six specialized project subagents run on **Sonnet 5.5 / high** and each report to Opus: backend-dev, database-dev, frontend-dev, security-auditor, health-observer, qa-reviewer. Preserve the four existing project agent definitions; install the two additional narrowly scoped definitions provided with this goal if missing. Check actual model routing in `.claude/settings.json` and `/tasks`; do not falsely report model activation. **Use ordinary Claude Code subagents rather than experimental Agent Teams.** Do not recreate the project or its memory system.

## 0 — Establish the actual project state (mandatory prerequisite)

1. Locate and read the existing `CLAUDE.md`, `AGENTS.md` (if any), prior GOAL files, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/PROGRESS.md`, and the existing project memory/brain (e.g. `Durum.md`, Last-Session, Threads, knowledge) **where they currently live**. Do not pretend a missing file exists. Read the brain's own maintenance instructions before changing it.
2. Inventory **staged, unstaged, and untracked** files using `git status --short --branch`, `git branch -vv`, `git log -5 --oneline`, `git diff --stat`, and `git diff --cached --stat`. Do not infer the local state from the older GitHub `main` branch. Do not discard existing work through a branch switch, reset, clean, or checkout.
3. Inspect the actual local Phase 3 Catalog implementation (`InitialCatalog` migration, ProductVariant/SKU, catalog APIs and web pages) and Phase 4 Identity implementation (`AddIdentity` migration, cookies, CSRF, role/policies, sign-in/sign-up/account pages), plus available verification records. If foundational work is missing or broken, do **not** blindly start Phase 5. Preserve working contracts. This GOAL does not authorize silently rebuilding Phase 3–4: record `BLOCKED_PREREQUISITE` and request permission for the narrowly necessary fix.
4. The user **approved the Phase 2 storefront design**. Preserve its colors, page hierarchy, imagery, and mobile bottom navigation. Only make UI changes needed to implement Phase 5–6 inventory/cart/address functionality; no redesign.
5. Check Docker Desktop and the existing PostgreSQL container and named volume. Never print real `.env` contents or bypass configured secret-file read restrictions. Use the existing safe connection/test setup. **Never execute `docker compose down -v`, database drop/reset/truncate, or destructive migrations.**
6. Give one concise initial status summary: Phase 3/4 `VERIFIED`, `PARTIALLY_VERIFIED`, or `BLOCKED`; current Git working tree; and Docker availability. Continue authorized in-scope work without repeatedly requesting a new user prompt.

## 1 — Technical contract and security boundaries

- Backend: ASP.NET Core 10 / C# / EF Core 10 / PostgreSQL 17. Frontend: Next.js App Router / React / TypeScript / Tailwind in the existing single `apps/web` app. Preserve the same-origin `/api/backend/*` proxy.
- Architecture: pragmatic modular monolith; Domain → Application → Infrastructure, with API as the composition root. Avoid speculative MediatR/CQRS/generic repositories/event buses.
- **The backend and PostgreSQL are the authoritative sources for product prices and stock state.** Never accept price, discounts, stock, ownership, or totals supplied by the browser. Use `decimal` in the domain, `numeric(18,2)` in PostgreSQL, and `long` integer **TRY minor units (kuruş)** in public APIs (`priceMinor`, `subtotalMinor`). Persist timestamps in UTC.
- Do not expose internal warehouse/employee information through public catalog endpoints. Management endpoints require authentication and authorization. Preserve Phase 4 CSRF protection on every state-changing cookie-authenticated request. API auth failures must return appropriate 401/403 responses, not misleading 200s or unexpected redirects.
- Users cannot read or modify other users' carts or addresses. Derive user identity from the authenticated server-side Identity principal, never from a user ID in the request body. Test XSS/HTML injection, mass assignment, overly detailed error responses, privilege escalation, and rate-limit bypass.
- **Adding to cart never reserves or deducts stock**; checkout preview does not create an order or payment. Phase 7 will revalidate stock. Never display a fabricated “payment successful” or “order placed” state.
- Do not implement real payment processing, stock reservations, or order-status workflows in Phase 5–6.

## 2 — Phase 5: Inventory

### F5.1 — Data model and invariants

Design a small relational model compatible with existing Catalog/Identity entities and migration history:

- `Warehouse`: unique code, name, `IsActive`, UTC audit timestamps; support multiple warehouses.
- `InventoryBalance`: enforce uniqueness on (`WarehouseId`, `ProductVariantId`); nonnegative `OnHandQuantity`; select a reliable versioning/concurrency approach. Aggregate stock may be derived; do not model or assume reservations.
- `StockMovement`: immutable audit history containing warehouse, variant/SKU, signed quantity delta, operation type (receiving/adjustment/correction, etc.), actor user ID, UTC timestamp, reason/reference, and a unique operation/idempotency key. Balance updates and movement insertion must succeed or fail together.
- Define foreign keys, unique indexes, check constraints, and numeric bounds explicitly. Do not conflict with Phase 3 SKU uniqueness. Zero stock is valid; negative stock is not. Quantities are bounded integers subject to domain validation.
- Do **not** interpret a `Warehouse` role as unrestricted permission to alter every warehouse's stock unless warehouse-scoped authorization already exists. **Admin-only inventory writes are acceptable for the initial version**. If Warehouse users may write, implement and test server-side warehouse-scope authorization. Record the decision in `docs/DECISIONS.md`.

### F5.2 — Additive EF migration and migration verification

- Do not edit, rename, or delete prior `InitialCatalog` / `AddIdentity` migrations. Generate a new **additive Inventory migration**.
- Review generated SQL and migration ordering. Preserve existing customer and catalog data. Confirm backups as appropriate before touching the development database; stop for explicit approval if any destructive step is necessary. Verify both fresh database creation and upgrade from the previous schema on **dedicated test databases**.
- Coordinate `DbContext`, entity configuration, and migration ownership between database-dev and backend-dev. Do not allow concurrent writes to the same files.

### F5.3 — Atomic stock operations, concurrency, and auditability

- Receiving and stock adjustments must be atomic: update on-hand balance and insert the corresponding movement **in the same transaction**; either both commit or neither does.
- Two concurrent stock deductions cannot consume the same final unit twice. Choose an appropriate PostgreSQL strategy (atomic conditional `UPDATE`, row locking, or correct optimistic concurrency) and prove it with a **real PostgreSQL concurrency test**. Avoid unprotected read-modify-write races.
- Replaying an identical idempotency key must not apply a stock change twice; reusing that key with a different payload must fail with a conflict. A rolled-back transaction must not leave a movement behind. Reject changes that would make stock negative using consistent API errors (e.g. 409/422); document the chosen semantics.
- Test zero stock, missing/inactive variant, inactive warehouse, invalid delta, unauthorized role, missing CSRF, concurrent requests, and duplicate operation keys.

### F5.4 — APIs and public inventory availability

- Implement appropriately authorized warehouse listing, balance reading, and receive/adjust APIs consistent with existing API conventions. Document their OpenAPI contracts where useful.
- Expose a clear and consistent `inStock` / `availableQuantity` policy in the public catalog and product details **without revealing per-warehouse internal details**. If a variant has no stock, the UI must not falsely imply availability.
- Do **not** build the full `/admin` dashboard; that belongs to Phase 8. Minimal maintenance endpoints and test fixtures are enough. Do not seed fictional stock as real available inventory; label demo/test data accurately.

**Phase 5 acceptance gate:** additive migration fresh-install + upgrade tests PASS; real PostgreSQL stock operations PASS; negative-stock, concurrency, and idempotency tests PASS; role and CSRF tests PASS; accurate public availability; existing catalog and identity regression tests PASS. Do not claim Phase 5 is complete without this evidence.

## 3 — Phase 6: Cart / Checkout (NO order placement)

### F6.1 — Persistent per-user cart

- Implement **one active cart per authenticated user** based on the existing Identity user ID, unless an existing secure model already defines a different compatible contract. Introduce `Cart`, `CartItem`, and required foreign keys/unique indexes. Each (`CartId`, `ProductVariantId`) pair appears once.
- Quantity must be a positive bounded integer. Specify the API's handling of quantity `0` explicitly (a separate removal endpoint is preferred). Validate that the selected variant is valid and active. Cart items do not reserve stock; availability warnings are provisional and can change.
- Browser-controlled prices must never be persisted as authoritative amounts. On every cart retrieval, read the current catalog price and return a **server-calculated subtotal**. Price changes after adding an item must be reflected truthfully, not silently frozen to an obsolete amount.
- Prevent duplicate `CartItem` rows, lost updates, or inconsistent totals under concurrent requests by the same user. Prove relevant guarantees with unique constraints and real database tests.

### F6.2 — Cart API and cookie security

Follow current project route conventions; adapt the following contract only with documented justification:

- `GET /api/v1/cart` — authenticated customer's cart with server-derived amounts
- `POST /api/v1/cart/items` — variantId and quantity
- `PATCH /api/v1/cart/items/{itemId}` — change quantity
- `DELETE /api/v1/cart/items/{itemId}` — remove item

Require cookie authentication and CSRF for all mutations. Enforce ownership checks for cart/item IDs. Reject or ignore client-controlled `userId`, `priceMinor`, `subtotalMinor`, and `discount` fields. Return appropriate 400/401/403/404/409/422 responses using ProblemDetails. Show an explicit sign-in requirement when unauthenticated.

**Guest cart:** The default scope is authenticated-user carts. If a secure guest-cart contract already exists, inspect and preserve it. Do not enlarge scope by introducing guest cart merging solely for this phase.

### F6.3 — Customer delivery addresses

- Add a user-owned address entity: label, recipient name, phone (only if necessary; follow data minimization), province/district, street address, postal code (where applicable), and `IsDefault`. Avoid unnecessary globally generalized address schemas; document validation appropriate for Türkiye.
- A user must never read, modify, or select another user's address by guessing an ID. Implement address management APIs and the necessary UI (`/hesabim/adresler` or within the existing account pages). Do not log PII or put it into URL query parameters. Test default-address uniqueness and ownership.

### F6.4 — Checkout preview (read-only business operation)

- Provide an endpoint such as `POST /api/v1/checkout/preview` that returns a **pre-order summary** from the authenticated user's cart and selected owned address. The endpoint **must not create orders or payments or deduct stock**. It may use POST for input, but its business effect must be read-only; apply existing CSRF policy.
- The backend must revalidate variant active status, current price, stock availability, quantities, and address ownership. Calculate `subtotalMinor` on the server. If shipping/tax is not configured, **do not fabricate a definitive payable total**: expose a truthful state such as `shippingNotCalculated` and set `grandTotalMinor` to `null` when appropriate.
- Even after a successful preview, price or stock may change before Phase 7's order placement; explain this in the UI. No payment form, “payment completed,” or “order confirmed” screen.

### F6.5 — Storefront UX and integration

- Preserve the user-approved SSCommerce visual design. Implement `/sepet` and `/checkout`; connect address selection/editing and cart quantity/removal actions to the real backend. Make Header / mobile BottomNav cart navigation functional. Show when authentication is required; do not leave inert mock cart controls in place.
- Test responsive behavior at 390px / 768px / 1440px, keyboard interaction, and accessibility. Cover empty cart, unauthenticated user, out-of-stock variant, changed price, invalid address, API error, loading, and recoverable failure states.
- Do not imply that an order can already be placed or a payment processed. A clearly disabled “Ordering coming soon” control/explanation is acceptable until Phase 7.
- Test browser cookie + CSRF behavior through the actual Next.js proxy and ASP.NET API. Investigate host/port mismatch and `Set-Cookie` / `Secure` issues rather than masking them.

**Phase 6 acceptance gate:** Real authenticated-user flow of sign in → add/edit/remove cart item → create/select owned address → checkout preview works; cross-user access is blocked; prices are current and calculated by the backend; no reservation/order/payment is created; backend + Playwright + any Node tests + lint/typecheck/build PASS.

## 4 — Verification and quality standards

- Run focused tests after significant changes and a **complete regression suite** at the end of each phase.
- Run `dotnet build backend/SSCommerce.slnx -c Release`, `dotnet test backend/SSCommerce.slnx -c Release` with **actual PostgreSQL** configured, and in the web app `npm run lint`, `npm run typecheck`, `npm run build`, `npm run test:e2e` (and existing Node test commands if any). Inspect the actual scripts and environment before invoking them.
- Concurrency/inventory tests must use real PostgreSQL, not merely in-memory or fake databases. Verify two simultaneous requests cannot produce negative stock, lose a movement, or reuse a key incorrectly.
- Verify fresh schema migration and schema upgrade using **test databases only**. Never drop the existing development volume or user data.
- Regression tests must cover 401/403, absent/invalid CSRF, other users' carts and addresses, idempotency replay, manipulated price, excessive quantities, zero inventory, concurrent updates, and safe error responses.
- Playwright at 390 / 768 / 1440, including keyboard controls, responsive behavior, and axe. At least one critical end-to-end flow must use a real browser via the Next.js proxy to the actual ASP.NET API. If no physical-device test occurs, mark it `NOT_VERIFIED`.
- Run `git diff --check`; inventory staged, unstaged, and untracked files and scan for secrets. Do not stage `.env`, test outputs, or screenshots.
- When remote GitHub Actions are prevented by account billing or another external issue, report `BLOCKED_EXTERNAL` **separately** from local verification. `PASS` requires an actually executed check. Historical results (such as 539/81) are not current-run evidence.

## 5 — Mandatory orchestrator-to-specialist delegation and token discipline

**Design:** The Opus 5.5/high lead must **actually spawn and use specialized Sonnet 5.5/high subagents** through Claude Code's native Agent tool. Merely mentioning their roles in a plan does not count as delegation. Ordinary **Subagents**, not experimental **Agent Teams**, are preferred because each specialist works in its own context and reports a concise result to Opus. The lead retains final ownership of architectural decisions, merge/conflict resolution, quality gates, documentation, and the report to the user.

**Required specialist assignments during this goal (all six roles must receive at least one substantive, independently scoped assignment):**

| Subagent | Required deliverable | Ownership / restrictions |
|---|---|---|
| `database-dev` | Phase 5 stock schema, indexes/checks, additive migration, SQL review, dedicated test-database migration evidence; Phase 6 cart/address migration coordination | Sole owner of EF migrations and persistence mappings while assigned; never overlap file edits with backend-dev |
| `backend-dev` | Phase 5 stock APIs, atomic movement logic and concurrency tests; Phase 6 cart/address/preview API contracts and business rules | Backend application/API/domain changes; agree on DTO and persistence contracts first |
| `frontend-dev` | Phase 5 truthful availability UI; Phase 6 `/sepet`, `/checkout`, and account/address flows with approved visual design | `apps/web` ownership; only after stable API contracts |
| `security-auditor` | Separate security review of authentication, cookie/CSRF, role authorization, inventory mutation, user cart/address isolation, idempotency and proxy trust; severity-tagged findings with file locations | Review-only: may inspect but must not silently patch findings or approve its own code; report to Opus |
| `health-observer` | Independent review of `/health/live`, `/health/ready`, database readiness, Docker/Compose condition, proxy response, failure behavior, and visibility of service status after each major phase | Operational reliability assessment; no service restarts or infrastructure changes without approval |
| `qa-reviewer` | Independent review and verification of backend/real PostgreSQL tests, concurrency proof, frontend/Node tests, Playwright critical flow, lint/typecheck/build; record pass/fail/skip and exit codes | Owns evidence gathering, not implementation; may run authorized tests and report failures to Opus |

**Dispatch choreography:**

1. **Preflight:** Opus reads current Git state and project brain. Delegate a focused baseline health inspection to `health-observer` and a Phase 3/4 auth/security baseline to `security-auditor`. These may run in parallel because they must not modify files. Their output is a short risk and prerequisite summary, not a full repository dump.
2. **Phase 5 design:** Opus gives `database-dev` the inventory schema/migration and gives `backend-dev` a non-overlapping inventory domain/API contract assignment. Never allow both to edit `DbContext`, migrations, entity mappings, or the same file concurrently. Once contracts are fixed, implement and verify atomic inventory operations.
3. **Phase 5 gate:** `qa-reviewer` executes focused real-PostgreSQL concurrency and regression checks; `security-auditor` independently reviews authorization, CSRF and idempotency; `health-observer` inspects readiness/health behavior. Opus integrates their findings, assigns fixes to the actual code owner, then decides PASS or NOT_VERIFIED/FAIL.
4. **Phase 6 build:** `backend-dev` implements cart/address/preview logic with `database-dev` owning the additive persistence migration, sequenced to prevent overlap. `frontend-dev` builds the customer UX after the endpoints/DTOs are stable; frontend can run concurrently only on disjoint files and confirmed contracts.
5. **Phase 6 gate:** `qa-reviewer` runs real integrated browser and backend suites; `security-auditor` audits user isolation, cookie/CSRF, price spoofing and trusted proxy assumptions; `health-observer` verifies runtime readiness/proxy health. Opus gives bounded corrections to owners and runs a final acceptance review.
6. **Final:** Opus collects each agent's signed-off status, reconciles disagreements, updates progress/brain, and presents the consolidated Turkish report. Do not claim a specialist ran unless an actual invocation and response occurred.

**Parallelism and cost controls:**

- Use up to **three concurrently active specialists** when their tasks are truly independent, and schedule the remaining roles in waves. **Six specialist roles must participate across Phases 5–6; six persistent sessions are not required.** Do not start multiple agents to perform identical repository scans, identical tests, or repeated security reviews without a new finding.
- Keep the lead in **coordination/review mode** for substantial implementation. Opus may perform minor integration edits, but must not take over a delegated large implementation task merely because direct editing is available.
- Give each agent a tightly scoped objective, paths/contracts, explicit forbidden actions, and exact acceptance checks. Start from a relevant file list instead of sending the entire repo or 200k-token conversation. Ask for errors and paths, **not full terminal logs, entire diffs, or pasted source files**.
- Every subagent must return the exact compact contract below; Opus must track a delegation ledger in the final report:

  ```text
  AGENT: <agent-name> / Claude Sonnet 5.5 / high (verify actual model)
  TASK: <phase and work-package ID>
  RESULT: PASS | FAIL | PARTIAL | BLOCKED
  FILES: <changed or reviewed paths, concise>
  EVIDENCE: <actual commands + exit codes + pass/fail/skip counts>
  FINDINGS: <severity, affected code paths, unresolved risks>
  HANDOFF: <the next owner/action; no giant logs>
  ```

- The lead must **not equate context reduction with guaranteed total token savings**: specialist requests also use tokens and count toward usage limits. Check `/usage` periodically; cut redundant work, idle agents and oversized delegation prompts. Use `/tasks` to verify actual model/effort assignments. Do not enable experimental Agent Teams by default.
- Preserve existing `.claude` permissions, Git controls and project brain procedures. Update `docs/PROGRESS.md` for F5.1–F5.4 / F6.1–F6.5 and brain / Last-Session / Threads / knowledge as their own instructions prescribe; record only tested facts. Give a checkpoint after 4–6 work packages and stop before Phase 7.

## 6 — Git, permissions, and stop conditions

- **Protect the current branch and any uncommitted Phase 2–4 work.** Inspect `git status`; recommend a safe commit strategy if needed, but do not commit without permission.
- `git commit`, `git push`, remote changes, history rewrites, force pushes, hard resets/cleans, branch deletion, destructive migrations, and production service/database modifications each require separate explicit user approval.
- Do not install unrelated tools, purchase services, perform speculative data restoration, or weaken security controls.
- This GOAL authorizes only **reversible local implementation and testing for Phases 5–6**. Do not begin Phase 7.
- If an approval or external dependency truly blocks progress, issue **one** clear `BLOCKED` / `PENDING_APPROVAL` report and stop. Do not emit repeating messages via scheduled wakeups or loops. If the user must run `/goal clear`, explain it once; use a stop action yourself when one is available.

## 7 — Objective completion criteria (strict evaluator gates)

Declare `LOCAL_COMPLETED` **only when every condition below has actually been verified**:

1. Existing local Phase 3/4 components are usable; migrations and authentication still work; prerequisite regression tests passed.
2. Phase 5 additive inventory migration, atomic balance/movement writes, negative-stock protection, concurrency, and idempotency all pass **real PostgreSQL tests**.
3. Inventory APIs have proper authorization, 401/403 and CSRF behavior, and public catalog stock availability is truthful.
4. Phase 6 persistent per-user cart, server-side price/subtotal, owned addresses, and checkout preview work; no order/payment/reservation is created.
5. Sign in → cart → address → checkout preview passes in **Chrome/Playwright via actual Next.js proxy + ASP.NET API**.
6. Full backend suite (real PostgreSQL, **0 failed and 0 skipped**), any Node tests, lint/typecheck/build, and E2E tests have documented commands, pass/fail/skip counts, and exit codes. Explicitly flag any skipped test and which gate it prevents.
7. `docs/PROGRESS.md`, ROADMAP / ARCHITECTURE / DECISIONS, and the existing project brain have truthful updates. `git diff --check` passes; no secrets or test artifacts are staged.
8. If commits/push are awaiting authorization, report `LOCAL_COMPLETED / PENDING_GIT_APPROVAL` separately; if CI is unavailable, report `BLOCKED_EXTERNAL` independently. Never claim code was pushed unless confirmed.

### Final report (in Turkish; concise)

- Phase 5: PASS / FAIL / NOT_VERIFIED; migration, endpoints, concurrency and idempotency evidence.
- Phase 6: PASS / FAIL / NOT_VERIFIED; cart, address, checkout preview, and E2E evidence.
- Tests table: actual command, pass/fail/skip counts, exit code.
- Security: risks, resolved issues, any remaining medium/high findings.
- Git: branch, HEAD, staged/unstaged/untracked summary, proposed commit boundary (awaiting authorization).
- External blockers, confirmation that Phase 7 was not started, and the next decision needed.

**If completion is impossible:** Stop with an `INCOMPLETE` or `BLOCKED` report explaining exactly why and how to continue. Do not invent a PASS. Do not waste tokens repeating a request for authorization.

## 8 — Technical references (check against the installed .NET 10 version before implementation)

- EF Core optimistic concurrency: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
- EF Core transactions: https://learn.microsoft.com/en-us/ef/core/saving/transactions
- ASP.NET Core 10 API cookie challenge behavior: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/api-endpoint-auth?view=aspnetcore-10.0
- ASP.NET Core 10 antiforgery: https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0
