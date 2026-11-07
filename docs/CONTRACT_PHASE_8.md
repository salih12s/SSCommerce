# Phase 8 contract: ERP / admin operations

Status: **FROZEN v1 (2026-11-02)** after baseline reviews by security-auditor (P8-SEC-0), database-dev (P8-DB-0) and health-observer (P8-HEALTH-0). Written by the lead before implementation. Specialists follow it; if something must change, tell the lead before implementing. Goal: `GOAL_PHASE_8_ERP_ADMIN_EN.md`. Earlier contracts (`docs/CONTRACT_PHASES_5_6.md`, `docs/CONTRACT_PHASE_7.md`) still apply unless this file says otherwise. Decisions: `docs/DECISIONS.md` D-068 onward.

## Boundary

Built: `/admin` area in the existing Next.js app; Admin catalog management (categories, products, variants, prices, approved images); warehouse and stock screens over the existing Phase 5 endpoints; staff order list/detail and transitions over the existing Phase 7 service; Admin-only delivery view; server-side unpaid-order limits; dashboard and bounded reports; immutable admin audit log; staff role management; a local first-Admin bootstrap command; an additive DB guard for order status transitions.

Not built: payments, paid/refunded flags, refunds, shipping fees, grand totals, reservations, expiry jobs, file upload or URL ingestion, exports, ERP sync, invoices, warehouse-scoped permissions, e-mail, Phase 9 deployment.

## Roles and policies

| Policy | Who | Used by |
|---|---|---|
| `AdminOnly` | Admin | `/api/v1/admin/*` group (existing), every new catalog, stock, dashboard, report, audit, user and delivery route |
| `WarehouseOrAdmin` | Warehouse, Admin | `/api/v1/admin/orders` group only (existing): list, detail, forward transitions |

Warehouse gets no catalog, stock, dashboard, report, audit, role or delivery access (D-045 stays). Admin and Warehouse accounts are not exempt from customer rules when they shop for themselves (D-051).

## Persistence (`AddAdminOperations`, one forward-only additive migration, owner: database-dev)

Conventions from Phase 7 apply (UUIDv7 from the application, `snake_case`, `timestamptz`, named `pk_/fk_/ck_/ux_/ix_/trg_`). Existing migrations are not edited.

1. `version integer NOT NULL DEFAULT 1` with `ck_<table>_version_positive CHECK (version >= 1)` on `categories`, `products`, `product_variants`. EF: `HasColumnType("integer").HasDefaultValue(1)` (raw INSERTs in tests and fixtures omit it). Domain: `public int Version { get; private set; } = 1` plus a method that increments by exactly 1. Not an EF concurrency token: the check is lock + compare (below). Existing rows start at 1. Fixture/raw SQL updates do not bump it; tests that need a stale version bump it explicitly.
2. `admin_audit_log` (append-only), PK `pk_admin_audit_log`:

| Column | Type | Rule |
|---|---|---|
| `id` | uuid NOT NULL | PK |
| `actor_user_id` | uuid NOT NULL | `fk_admin_audit_log_actor_user` → `AspNetUsers` RESTRICT; index `ix_admin_audit_log_actor_user_id` |
| `action` | varchar(64) NOT NULL | `ck_admin_audit_log_action_format` `^[a-z]+(\.[a-z_]+)+$` |
| `target_type` | varchar(32) NOT NULL | `ck_admin_audit_log_target_type` in `category, product, variant, warehouse, user, order` |
| `target_id` | uuid NOT NULL | |
| `changes` | jsonb NOT NULL | `ck_admin_audit_log_changes_object`: object, `octet_length(changes::text) <= 8192`, no forbidden top-level key (SQL below) |
| `occurred_at` | timestamptz NOT NULL | from the application clock, no DB default |

   Changes CHECK: `jsonb_typeof(changes) = 'object' AND octet_length(changes::text) <= 8192 AND NOT (changes ?| ARRAY['password','password_hash','security_stamp','token','email','phone','address','street_address'])`.
   Indexes `ix_admin_audit_log_occurred_at_id (occurred_at DESC, id DESC)` and `ix_admin_audit_log_target_type_target_id_occurred_at_id (target_type, target_id, occurred_at DESC, id DESC)`. New generic function `append_only_reject_change()` (23001), triggers `trg_admin_audit_log_no_update_delete` and `trg_admin_audit_log_no_truncate`. `Down` refuses when the table has rows. Never stores passwords, hashes, stamps, tokens, any e-mail, phone or address. Actions: `category.create|update`, `product.create|update|images_replace`, `variant.create|update`, `warehouse.create|update`, `role.grant|revoke|bootstrap`, `order.delivery_view`. A write that changes nothing writes no audit row and does not bump `version`.
3. Category parent guard: function `categories_guard_parent()`, trigger `trg_categories_guard_parent BEFORE INSERT OR UPDATE OF parent_id ON categories`. Returns immediately when `NEW.parent_id IS NULL`, `NEW.parent_id = NEW.id` (the existing `ck_categories_parent_not_self` rejects that) or, on UPDATE, the parent is unchanged. Otherwise: `pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0))`, walk ancestors of the new parent without row locks (counter-bounded), raise `check_violation` / `ck_categories_no_cycle` if it reaches `NEW.id`; then require `ancestors + 1 + subtree height of NEW.id ≤ 5` (recursive CTE, bounded) or raise `check_violation` / `ck_categories_max_depth`. `RAISE … USING ERRCODE='check_violation', CONSTRAINT=…, TABLE='categories'`. Volatile; correct only under READ COMMITTED (all admin writes are). **Lock order for every category write in the backend: the same advisory lock first, then the row `FOR UPDATE`** — otherwise trigger lock vs. FK `FOR KEY SHARE` can deadlock (40P01). Deadlock or lock timeout → 503 `busy`.
4. Order status guard (closes D-067 item 2): function `orders_guard_status_transition()`, trigger `trg_orders_status_transition BEFORE UPDATE OF status ON orders FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)`. Unknown target status → `RETURN NEW` (the existing `ck_orders_status` rejects it). Allowed pairs: `placed→processing`, `placed→cancelled`, `processing→shipped`, `processing→cancelled`, `shipped→completed`; anything else `check_violation` / `ck_orders_status_transition`. Plus `trg_orders_insert_placed BEFORE INSERT ON orders`: status must be `placed` (same constraint name family, `ck_orders_insert_status`). Fires after `trg_orders_restrict_update` (alphabetical). `OrderStateMachine` stays the authority for roles. Residual: a DB owner or superuser can disable triggers; the runtime role must not own tables in production (Phase 9).
5. Indexes: `ix_orders_open_created_at ON orders (created_at, id) WHERE status IN ('placed','processing')` (aged open orders, oldest first); `ix_stock_movements_occurred_at_id (occurred_at DESC, id DESC)`; `ix_order_status_history_occurred_at_id (occurred_at DESC, id DESC)`. `ix_orders_open_user ON orders (user_id) WHERE status IN (...)` only if `EXPLAIN (ANALYZE, BUFFERS)` on a seeded test DB (≥100k orders, one user with 2k) shows a clear gain; decided before the migration is generated. Queries that should use a partial index write the status list as SQL literals, never as a parameter.

Migration proof: fresh DB; upgrade from a restored dev dump (row md5 of every existing table unchanged); second run no-op; idempotent script twice; `has-pending-model-changes` clean; script contains no DML. Existing migration tests that assert "5 migrations" or a single pending `AddOrdering` are updated by database-dev (test-only change).

## API

All routes under `/api/v1`, cookie auth, Origin + CSRF on mutations (group filter), `no-store` (existing `/api/v1/admin` prefix), `mutation` rate limit on writes, ProblemDetails with `code`, money `...Minor` (`long`, 0..2^53-1), UTC ISO timestamps. Unknown body fields are ignored. Ids in routes are UUIDs (`400 validation_failed` otherwise). Text fields go through `TextRules` (Cc/Cf/Zl/Zp rejected) and Domain limits. Body limit 16 KB, product create/update 64 KB.

### Catalog (AdminOnly)

| Method | Route | Result |
|---|---|---|
| GET | `/admin/categories` | all categories incl. inactive: `id, name, slug, parentId, sortOrder, isActive, version, productCount, updatedAt`; ordered `sortOrder, name, id`; capped 500 |
| POST | `/admin/categories` | `{name, slug, parentId?, sortOrder?, isActive?}` → 201; slug taken 409 `category_slug_taken`; unknown parent 404 `category_not_found`; cycle 409 `category_cycle` |
| PATCH | `/admin/categories/{id}` | `{version, name?, parentId?, sortOrder?, isActive?}`; slug immutable → 200; stale 409 `version_conflict` |
| GET | `/admin/products?q=&categoryId=&status=active\|inactive\|all&page=&pageSize=` | paged summaries (`pageSize` ≤ 50): `id, name, slug, categoryId, categoryName, isActive, isDemo, variantCount, activeVariantCount, minPriceMinor, primaryImageUrl, version, updatedAt`; order `updatedAt DESC, id DESC` |
| GET | `/admin/products/{id}` | detail incl. inactive variants and images, every `version` |
| POST | `/admin/products` | `{categoryId, name, slug, brand?, description?, specifications?, isActive?}` → 201; default `isActive: false`; `isDemo` always false; slug taken 409 `product_slug_taken` |
| PATCH | `/admin/products/{id}` | `{version, categoryId?, name?, brand?, description?, specifications?, isActive?}`; slug immutable |
| PUT | `/admin/products/{id}/images` | `{version, images: [{storageKey, altText, isPrimary}]}` replaces the set (≤ 8, order = array order, exactly one primary when non-empty); `storageKey` must be in the approved asset list (409 `image_not_approved`); bumps product version |
| GET | `/admin/catalog/image-assets` | approved local assets `[{storageKey, creditKey, author}]` |
| POST | `/admin/products/{id}/variants` | `{sku, displayName, priceMinor, options?, isActive?}` → 201; SKU taken 409 `sku_taken`; same option signature 409 `variant_options_taken` |
| PATCH | `/admin/variants/{id}` | `{version, displayName?, priceMinor?, isActive?}`; SKU and options immutable |
| GET | `/admin/variants?q=&page=&pageSize=` | SKU/product search for stock screens: `id, sku, productName, displayName, isActive, productIsActive, onHandTotal` |

Rules: no DELETE routes; deactivate instead. Slug and SKU are immutable after creation because order snapshots and stock movements reference them. `priceMinor` for admin writes is 1..9007199254740991 (existing zero prices stay readable). `version` is required on every PATCH/PUT: missing, non-integer, < 1 → 400 `validation_failed` (never "skip the check").

Every write runs in one READ COMMITTED transaction: `lock_timeout 5s` → (category writes: advisory `catalog:category-tree`) → `SELECT … FOR UPDATE` of the target row → compare `version` **read from that locked row**, never from an entity loaded earlier (409 `version_conflict` with `currentVersion`) → mutate → `version + 1` → audit row with `old` values taken from the locked row → commit. Lock timeout or deadlock → 503 `busy`. Lock order: category tree advisory → category row → product row → variant row; a product move takes the new category `FOR SHARE` after its own row. Placement takes `FOR SHARE OF v, p, c` and no exclusive catalog lock, so admin writes wait for in-flight placements (bounded by 5 s) and no cycle exists.

Catalog POSTs carry no Idempotency-Key: a retry after an unknown outcome may get 409 `*_taken`; the UI treats that as "possibly your own earlier request" and reloads the list. A price change never touches `order_items`, carts' `unit_price_at_add`, or stock movements; it affects new previews and makes stale quotes fail with `order_quote_changed`. Public catalog queries remain active-only.

Approved images: a server-side allowlist `Application/Catalog/ApprovedProductImages` listing `/images/urun/*.webp` keys with credit keys, kept in sync with `apps/web/src/content/image-credits.ts` and the files on disk by a test. No upload, no external URL, no other path.

### Inventory (AdminOnly, existing Phase 5 routes kept)

`GET /admin/warehouses`, `POST /admin/warehouses`, `PATCH /admin/warehouses/{id}`, `GET /admin/inventory/balances`, `GET /admin/inventory/movements`, `POST /admin/inventory/receive`, `POST /admin/inventory/adjust` are unchanged in shape. Additive only:

- Warehouse create/update writes `warehouse.create` / `warehouse.update` audit rows in the same transaction.
- `StockMovementDto` gains `actorEmail` (string, null) filled only when the actor currently holds Admin or Warehouse; customers stay anonymous ("Müşteri").
- `GET /admin/inventory/balances` gains `variantId` filter.

### Orders (staff)

| Method | Route | Policy | Result |
|---|---|---|---|
| GET | `/admin/orders?status=&open=true&page=&pageSize=` | WarehouseOrAdmin | existing list; `open=true` means `placed`+`processing`, oldest first; no PII |
| GET | `/admin/orders/{id}` | WarehouseOrAdmin | `AdminOrderDto` + `allocations[{sku, warehouseCode, quantity}]`; no PII |
| POST | `/admin/orders/{id}/status` | WarehouseOrAdmin; cancel Admin | existing, unchanged |
| POST | `/admin/orders/{id}/delivery-view` | AdminOnly (registered in the AdminOnly group; CSRF; mutation limiter) | `{recipientName, phone, province, district, streetAddress, postalCode, label}`; writes audit `order.delivery_view` (no PII in `changes`) in the same transaction; `no-store`; never in lists, URLs, logs |

POST, not GET, because viewing PII leaves an audit trace and GETs never write. Warehouse never sees delivery data. Giving Warehouse delivery data for shipping is an open owner decision (D-071).

### Unpaid-order limits (F8.5)

Configuration `Ordering:MaxOpenOrdersPerUser` (default 3) and `Ordering:MaxOrdersPerRollingDay` (default 10), validated at startup (1..1000). Applies to every account that places an order, regardless of role. Production thresholds: **`PRODUCTION_ORDERING_POLICY_PENDING`** until the owner approves.

Placement order (Phase 7 order, one inserted step): 1 lock_timeout → 2 advisory `(user, key)` → 3 replay check (replays return here and are never counted) → 4 `carts FOR UPDATE` (per-user lock) → **4a limits** → 5 address → … unchanged.

Step 4a, under the cart row lock, non-locking reads:
- `SELECT count(*) FROM orders WHERE user_id = @u AND status IN ('placed','processing')` ≥ max → 409 `open_order_limit` (extension `limit`).
- `SELECT created_at FROM orders WHERE user_id = @u AND created_at > @now - 24h ORDER BY created_at` (count n) with n ≥ max → 429 `order_daily_limit` (distinct from the IP limiter's `rate_limited`; the UI never auto-retries either) with `Retry-After` = seconds until the (n − max + 1)-th oldest in-window order leaves the window, clamped 1..86400. `@now` comes from the same `TimeProvider` that stamps `created_at`.
- The daily count ignores status, so cancel/re-place cycles are bounded. Status lists are SQL literals (partial index).

Why atomic: every placement of the same user holds that user's `carts` row lock while counting and inserting, so two requests with different keys cannot both see "2 open". Cancellations and forward transitions only lower the open count, so they need no lock. No new lock is taken, so the lock graph is unchanged (no new deadlock edge). A user without a cart row cannot place an order at all. Rejections roll back: no key, stock, cart or history change.

Checkout UI shows both codes in Turkish with guidance (cancel an open order / try later), no new key generation loop.

### Dashboard and reports (AdminOnly, read-only)

`GET /admin/dashboard?lowStockThreshold=5&agedHours=48` (threshold 1..1000, hours 1..720):

```
{ generatedAt,
  orders: { placed, processing, shipped, completed, cancelled },
  openOrders: { count, unpaidSubtotalMinor, agedCount, agedHours, oldestCreatedAt },
  catalog: { products, activeProducts, variants, activeVariants },
  stock: { threshold, lowStockVariantCount, outOfStockVariantCount },
  agedOpenOrders: [ ≤10 AdminOrderSummaryDto, oldest first ],
  recentMovements: [ ≤10 StockMovementDto ],
  recentOrderEvents: [ ≤10 { orderId, orderNumber, fromStatus, toStatus, actorKind, occurredAt } ] }
```

`unpaidSubtotalMinor` is the sum of item subtotals of `placed` + `processing` orders. It is never called revenue. There is no collected-revenue field because there are no payment records. Stock counts consider **sellable** variants (variant, product and category active — the same definition placement uses) and active warehouses; "low" means `0 < onHand ≤ threshold`, "out" means `onHand = 0` (or no balance). The dashboard runs its queries sequentially on one connection; lists capped at 10. Report `page` is 1..1000.

Reports (paged, `pageSize` ≤ 50, stable order):
- `GET /admin/reports/low-stock?threshold=&page=&pageSize=`: `variantId, sku, productName, displayName, onHandTotal`, order `onHandTotal, sku`.
- `GET /admin/audit?targetType=&targetId=&page=&pageSize=`: audit rows, newest first, `actorEmail` (staff only).

No GET writes anything.

### Staff role management (AdminOnly)

| Method | Route | Result |
|---|---|---|
| GET | `/admin/users/{id}` | `id, email, emailConfirmed, createdAt, roles` |
| GET | `/admin/staff` | users holding Admin or Warehouse, capped 200 |
| PUT | `/admin/users/{id}/roles/{role}` | grant `Admin` or `Warehouse` → 200 `{changed, roles}` |
| DELETE | `/admin/users/{id}/roles/{role}` | revoke → 200 `{changed, roles}` |

**Identity by account id, not e-mail.** E-mail is unverified (D-036) and registration is open, so an attacker could pre-register a colleague's address. There is no e-mail search. The person who should become staff reads their **account id** on `/hesabim` and gives it to the Admin out of band; the Admin opens `/admin/users/{id}`, sees e-mail, `emailConfirmed: false` and `createdAt`, and confirms.

`{role}` is exact-match `Admin` or `Warehouse` (case, whitespace, encoded variants, `Customer` → 400). One transaction: `lock_timeout` → `pg_advisory_xact_lock(hashtextextended('identity:roles', 0))` → **fresh SQL** re-check that the actor still holds Admin (403 `forbidden`) → target exists (404 `user_not_found`) → self-demotion 409 `role_self_demotion` → revoking Admin when fewer than 2 Admins (counted by fresh SQL) remain → 409 `last_admin` → apply → revoke target sessions (security stamp, `ISessionRevoker`, same DbContext transaction) → audit `role.grant` / `role.revoke` with `{role}` only (target id is the user id; no e-mail) → commit. Already in the requested state → 200 `changed: false`, no stamp change, no audit. Two Admins demoting each other in parallel leave exactly one Admin. Registration never assigns staff roles.

### First-Admin bootstrap (no HTTP route)

`dotnet run --project backend/src/SSCommerce.Api -- --bootstrap-admin` (flag constant in Infrastructure, like `--seed-demo-catalog`):
- Development environment only; otherwise exit 1 and nothing written. The connection string host must be loopback (`127.0.0.1`, `::1`, `localhost`); otherwise exit 1 (a Development profile pointed at a remote DB is refused).
- The e-mail is never echoed, logged or written to the audit.
- Pending migrations → exit 1.
- Refuses when any Admin already exists (exit 1): it creates the **first** Admin only; later Admins use the role API.
- Reads the e-mail of an **already registered** account from stdin, asks for it again as confirmation; no password is read, stored, printed or generated. Unknown account → exit 1.
- One transaction: advisory lock `identity:roles` → re-check no Admin → add role → revoke sessions → audit `role.bootstrap` (actor = target) → commit. Exit 0. Errors print only exception type names.
- Tested on isolated databases only. The lead never runs it against the dev database.

## Web (`apps/web`)

Routes: `/admin` (dashboard, Admin), `/admin/urunler`, `/admin/urunler/yeni`, `/admin/urunler/[id]`, `/admin/kategoriler`, `/admin/stok`, `/admin/depolar`, `/admin/siparisler`, `/admin/siparisler/[id]`, `/admin/raporlar`, `/admin/kullanicilar`. Warehouse sees only `/admin/siparisler*`; `/admin` redirects Warehouse to `/admin/siparisler`.

- Same `/api/backend/*` proxy, cookie, fresh CSRF per write (`lib/api.ts`). All data client-fetched; no private data in HTML/RSC, storage (except the stock idempotency intent below), URLs or analytics.
- `server/privacy.mjs` adds `admin` to private segments (`private, no-store`).
- `src/proxy.ts` gains `/admin/:path*`: fetches `${API_BASE_URL}/api/v1/auth/me` (fixed base, `redirect: "manual"`, ~2 s timeout) with only the incoming `cookie` and the `x-forwarded-for` produced by `server.mjs`. Only **401** → 307 to a relative `/giris?next=` built with `safeNext()` (path + query). Only a **200 with the expected JSON shape** is trusted: no Admin/Warehouse role, or Warehouse on a non-order admin page → rewrite to the 403 page with HTTP 403. Anything else (network error, timeout, 429, 5xx, unexpected body) → pass through; the page itself shows an error state (an outage must not look like a logout). The 307, the 403 and every `/admin` response (HTML and RSC/prefetch) are `private, no-store`. The backend still enforces everything.
- `/hesabim` shows the account id (from `/auth/me`) with a copy button, for staff onboarding.
- Logout clears in-memory admin data and the stock idempotency intents in `sessionStorage`.
- ESLint `react/no-danger` is an error in `apps/web` (no `dangerouslySetInnerHTML` on admin-entered text). A strict CSP for `/admin` is Phase 9.
- Admin layout distinct from the storefront (no storefront header/footer/bottom nav), same tokens and fonts, light theme primary.
- Stock receive/adjust: `Idempotency-Key` generated once per intent and kept in `sessionStorage` bound to user + payload; unknown outcome retries with the same key and payload or reloads the ledger; never a silent new key (D-066 pattern).
- Money shown from `...Minor`; TL input converted to kuruş on the client only for sending; UTC → local time on the client.
- Labels: "Ödenmemiş sipariş ara toplamı", never "ciro/gelir/tahsilat". No grand total.
- 1440/768/390, keyboard, focus, loading/empty/error states, axe.

## Response shapes (binding, v1.1)

The web client types in `apps/web/src/lib/admin/types.ts` are the binding JSON shapes (camelCase) for the new admin routes. Resolved details:

- `GET /admin/categories`, `GET /admin/staff`, `GET /admin/warehouses`, `GET /admin/catalog/image-assets`: plain arrays. Every other list: `Paged` (`items, page, pageSize, totalCount, totalPages`).
- `GET /admin/products/{id}` → `ProductDetail` (incl. `categoryName`, `updatedAt`; images carry `sortOrder`, `url` = `storageKey`). Create routes return 201 with the created resource (`Category`, `ProductDetail`, `AdminVariant`) including `id` and `version`; PATCH/PUT return the updated resource with the new `version`. Variant PATCH returns `AdminVariant`; image PUT returns `ProductDetail`.
- PATCH semantics: an absent property means "unchanged"; explicit `null` clears nullable fields (`parentId` → root, `brand`, `description`, `specifications`). Unknown properties are ignored.
- `GET /admin/orders/{id}` → `AdminOrder` with `allocations`. `POST /admin/orders/{id}/delivery-view` → `DeliveryView`.
- `AuditRow`: `id, actorUserId, actorEmail (staff only, else null), action, targetType, targetId, changes, occurredAt`. `StaffUser`: `id, email, emailConfirmed, createdAt, roles`. Role routes → `RoleChange`.
- `version_conflict` problem carries `currentVersion`; `open_order_limit` and `order_daily_limit` carry `limit`.

## Errors (new)

| Status | Code |
|---|---|
| 404 | `category_not_found`, `product_not_found`, `variant_not_found` (existing), `user_not_found`, `order_not_found` (existing) |
| 409 | `version_conflict` (+`currentVersion`), `category_slug_taken`, `product_slug_taken`, `sku_taken`, `variant_options_taken`, `category_cycle`, `category_too_deep`, `image_not_approved`, `open_order_limit` (+`limit`), `role_self_demotion`, `last_admin` |
| 429 | `order_daily_limit` (+`limit`, `Retry-After`) |
| 503 | `busy` |

## Acceptance matrix (summary)

Added after review: two parallel PATCHes with the same version → one 200, one 409; PATCH without version → 400; forced audit failure leaves row and version unchanged; parallel category swap A→B / B→A → at most one succeeds, no cycle; depth 5 boundary and deep-subtree move; direct SQL `completed→placed` and INSERT with status ≠ placed rejected with the constraint name; Retry-After for n = max and n > max; replay over the limit still 200 and not counted; role route variants (`ADMIN`, `Customer`, ` Admin`) → 400; revoked admin's old cookie → 401; delivery-view writes exactly one audit row without PII; `/admin` Cache-Control on 200, 307, 403 and RSC responses; two clients with different XFF through the Next rewrite land in different limiter partitions (QA runtime check).

Backend/PostgreSQL: 401/403 on every new route (anonymous, Customer, Warehouse where AdminOnly); Warehouse 403 on stock writes and catalog; CSRF/Origin on every new write; malformed JSON, wrong types, forged fields; uniqueness and cycle (incl. parallel swap); stale version 409; price change reflected in public catalog and preview, stale quote 409, order snapshot and movements unchanged; delivery Admin-only, never in lists; role rules incl. parallel mutual demotion; session revoked on role change; limits incl. different-key race, replay not counted, rejected attempt consumes nothing, 429 Retry-After; DB transition trigger; audit append-only and written in the same transaction (forced failure leaves no audit and no change); dashboard numbers equal SQL counts; no GET writes; bootstrap command on isolated DB; mutation test (remove cart lock or move the limit check before step 4 → limit race test fails).

Web: Playwright 1440/768/390, axe, keyboard; anonymous → login redirect, Customer → 403, Warehouse → orders only; Admin creates category/product/variant, changes price and sees it on the storefront, deactivates; receive/adjust with retry-safe key; order processing and Admin cancellation with stock returned; dashboard matches seeded data; no PII in HTML/RSC/URL; existing suites green.

## Shared local rules

Disposable PostgreSQL 17 at `127.0.0.1:5434` via `bash <scratchpad>/with-test-db.sh <command>`; never print it. Dev DB on 5433: specialists never touch it. Ports 5081, 5082, 3200 belong to one E2E run at a time. Do not stop processes you did not start.
