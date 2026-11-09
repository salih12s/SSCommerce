# Phase 7 contract: transactional ordering

Written by the lead before implementation. Specialists follow it; if something must change, tell the lead before implementing. Rationale: `docs/DECISIONS.md` D-059..D-067. Goal: `GOAL_PHASE_7_ORDERING_EN.md`. Existing conventions from `docs/CONTRACT_PHASES_5_6.md` still apply.

## Boundary

Built: order placement from the current cart, immutable snapshots, atomic stock consumption, idempotency, customer history/detail/cancel, minimal staff status changes.

Not built: payment or capture, any `paid` flag, refunds, shipping integration, fabricated shipping or grand total, cart stock reservation, expiry jobs, ERP UI, coupons, tax, Phase 8.

A Phase 7 order is an unpaid operational record. Stock is consumed at order commit with no automatic release. This is an intentional limited MVP trade-off (D-061).

## Persistence (`AddOrdering`, one forward-only additive migration, owner: database-dev)

Conventions: UUIDv7 ids generated in the application, `snake_case`, `timestamptz` UTC, `numeric(18,2)`, FK `ON DELETE RESTRICT`, CHECK constants shared with Domain rules, named `pk_/fk_/ck_/ux_/ix_`.

`orders`

| Column | Type | Rule |
|---|---|---|
| `id` | uuid | PK |
| `order_number` | bigint identity, start 100001 | unique; shown as `SS-100001`; routes never use it |
| `user_id` | uuid | FK `AspNetUsers` RESTRICT; ownership |
| `idempotency_key` | varchar(128) | CHECK `^[A-Za-z0-9_-]{16,128}$`; `ux_orders_user_id_idempotency_key` |
| `request_fingerprint` | char(64) | CHECK `^[0-9a-f]{64}$` |
| `status` | varchar(32) | CHECK in `placed, processing, shipped, completed, cancelled` |
| `delivery_label` | varchar(50) | snapshot |
| `delivery_recipient_name` | varchar(100) | snapshot |
| `delivery_phone` | char(10) | snapshot, CHECK `^5[0-9]{9}$` |
| `delivery_province` | varchar(32) | snapshot |
| `delivery_district` | varchar(64) | snapshot |
| `delivery_street_address` | varchar(300) | snapshot |
| `delivery_postal_code` | varchar(5) null | snapshot |
| `currency` | char(3) | CHECK `= 'TRY'` |
| `item_subtotal` | numeric(18,2) | CHECK `>= 0` |
| `shipping_status` | varchar(32) | CHECK `= 'shippingNotCalculated'` |
| `shipping_amount` | numeric(18,2) null | CHECK `IS NULL` in Phase 7 |
| `grand_total` | numeric(18,2) null | CHECK `IS NULL` in Phase 7 |
| `created_at`, `updated_at` | timestamptz | |

Index `(user_id, created_at DESC, id DESC)` for customer history and `(status, created_at DESC, id DESC)` for staff listing. No FK to `customer_addresses`: the snapshot survives address edits and deletes.

`order_items`: `id`; `order_id` FK; `product_variant_id` FK; `sku`, `product_name`, `variant_name`, `product_slug` snapshots; `image_url`, `image_alt` nullable snapshots; `unit_price` numeric(18,2) CHECK `>= 0`; `quantity` int CHECK `1..20`; `line_subtotal` numeric(18,2) CHECK `= unit_price * quantity`; `currency` CHECK TRY. Unique `(order_id, product_variant_id)`.

`order_inventory_allocations`: `id`; `order_item_id` FK; `warehouse_id` FK; `product_variant_id` FK; `quantity` int CHECK `> 0`; `placement_movement_id` FK `stock_movements`. Unique `(order_item_id, warehouse_id)`.

`order_status_history`: `id`; `order_id` FK; `from_status` null for the first row; `to_status`; `actor_user_id` FK; `actor_kind` CHECK in `customer, staff`; `reason` varchar(200) null; `occurred_at`. Index `(order_id, occurred_at, id)`.

`stock_movements`: `ck_stock_movements_operation_type` gains `order_placement` and `order_cancellation`. Dropping and re-adding this one CHECK is the only allowed `DROP` in the migration. `StockOperationType` gains both values. Placement delta must be negative, cancellation delta positive (Domain ctor). Public admin inputs keep accepting only `adjustment` and `correction`.

Immutability (triggers, in the migration):

- `order_items`, `order_inventory_allocations`, `order_status_history`: reject UPDATE, DELETE, TRUNCATE.
- `orders`: reject DELETE and TRUNCATE; reject any UPDATE that changes a column other than `status` and `updated_at`.

Migration proof: fresh database, upgrade from a restored dev-style dump (row md5 of catalog, identity, stock, carts, addresses unchanged), second run no-op, idempotent script. The four existing migrations are not edited.

## Quote (price confirmation)

`POST /checkout/preview` gains `quote` (string, null when `canProceed` is false) and `orderingAvailable` becomes `true`.

`quote` = lowercase hex SHA-256 of the canonical string `v1|TRY|<subtotalMinor>|<line>;<line>...` where `<line>` is `<variantId:N>:<quantity>:<unitPriceMinor>`, lines sorted by variant id ordinal. Computed by one shared function in `Application` from the same server calculation used by both preview and placement. It holds no secret: forging one only matters if it equals the true current value. It covers offsetting price changes that a bare subtotal comparison would miss.

## API

All routes are under `/api/v1`, cookie authenticated, Origin + CSRF on mutations, `no-store` (add `/api/v1/orders`), `mutation` rate limiter on POST, ProblemDetails with `code`. Money is `...Minor`. PII never appears in URLs or logs.

| Method | Route | Policy | Result |
|---|---|---|---|
| POST | `/orders` | authenticated | `201 OrderDto`; replay `200 OrderDto` with `replayed: true` |
| GET | `/orders?page=&pageSize=` | authenticated | own orders, newest first; `pageSize` default 10, max 50 |
| GET | `/orders/{id}` | authenticated | own detail; otherwise `404 order_not_found` |
| POST | `/orders/{id}/cancel` | authenticated | `200 OrderDto`; already cancelled `200` with `replayed: true` |
| GET | `/admin/orders?status=&page=&pageSize=` | `WarehouseOrAdmin` | summaries without PII |
| POST | `/admin/orders/{id}/status` | `WarehouseOrAdmin`; cancelling needs Admin | `200 AdminOrderDto`; same status `200` with `replayed: true` |

`POST /orders`: header `Idempotency-Key` (`^[A-Za-z0-9_-]{16,128}$`, else `400 validation_failed`); body `{ "addressId": uuid, "quote": 64-hex }`. Unknown fields (`userId`, `priceMinor`, `status`, `paid`) are ignored and proven to have no effect. Body limit: existing 16 KB rule. No server-side entropy score: the key is scoped per user, so a weak key can only collide with the same user's own orders; the web client uses `crypto.randomUUID()`.

Fingerprint: lowercase hex SHA-256 of `v1|<addressId:N>|<quote>`. It deliberately excludes cart state, so a retry after the cart was cleared still replays.

`OrderDto`: `id, orderNumber, status, createdAt, updatedAt, currency, itemCount, subtotalMinor, shippingMinor (null), shippingStatus, grandTotalMinor (null), items[{ id, variantId, sku, productName, variantName, productSlug, imageUrl, imageAlt, unitPriceMinor, quantity, lineTotalMinor }], deliveryAddress{ label, recipientName, phone, province, district, streetAddress, postalCode }, statusHistory[{ fromStatus, toStatus, occurredAt }], canCancel, replayed`.

`canCancel` is computed by the server for the customer. `statusHistory` hides actor id, kind and reason. Allocations and warehouses never appear in customer responses. Replay returns the order's current state, which may already be cancelled.

`AdminOrderDto` is `OrderDto` without `deliveryAddress` and `canCancel`; its `statusHistory` adds `actorKind` and `reason`, never an actor id. Staff order detail is Phase 8, so staff responses carry no customer data. This replaces the original plan of returning `OrderDto` to staff.

Errors:

| Status | Code | Meaning |
|---|---|---|
| 400 | `validation_failed` | bad key, body, id, paging, status |
| 401 / 403 | `unauthenticated`, `forbidden`, `csrf_invalid`, `origin_not_allowed` | existing rules |
| 404 | `order_not_found` | missing or someone else's order, identical body |
| 404 | `address_not_found` | missing or someone else's address |
| 409 | `cart_empty` | no lines |
| 409 | `cart_total_too_large` | Money overflow |
| 409 | `order_item_inactive` | extension `itemIds` |
| 409 | `order_quote_changed` | server quote differs; fresh confirmation required; nothing written |
| 409 | `insufficient_stock` | extension `itemIds` |
| 409 | `idempotency_key_reused` | same key, different fingerprint |
| 409 | `order_transition_invalid` | illegal transition |
| 409 | `stock_limit_exceeded` | restock would pass max on hand; rolled back, safe to retry after correction |
| 429 | `rate_limited` | existing rule |
| 503 | `busy` | lock timeout; nothing written; retry with the same key |
| 503 | `service_unavailable` | database unreachable; middleware |

## Placement transaction (READ COMMITTED, one transaction, this exact order)

1. `SET LOCAL lock_timeout = '5s'`.
2. `pg_advisory_xact_lock(hashtextextended('order:' || userId || ':' || key, 0))`.
3. Read `orders` by `(user_id, idempotency_key)`. Same fingerprint: commit-free replay. Different: `409`. Never look a key up without `user_id`.
4. `SELECT id FROM carts WHERE user_id = @u FOR UPDATE`. No row: `cart_empty`.
5. Read the address by `(id, user_id)`; missing: `404`.
6. Read lines from `cart_items` joined to variant, product, category, primary image: `ORDER BY v.id FOR SHARE OF v, p, c`. No lines: `cart_empty`. This row lock makes a concurrent price or activity update wait, so the price cannot change between validation and commit.
7. Compute with the shared calculator. Overflow: `409`. Any inactive line: `409 order_item_inactive`. Quote mismatch: `409 order_quote_changed`.
8. Lock balances: `SELECT ... FROM inventory_balances b JOIN warehouses w ... WHERE b.product_variant_id = ANY(@variants) AND w.is_active ORDER BY b.product_variant_id, w.code FOR UPDATE OF b FOR SHARE OF w`. This is the only lock order for balances in multi-row flows.
9. Allocate per line, greedily in `(variant id, warehouse code)` order. A line may split across warehouses and creates one allocation per warehouse (D-062). Any line short: `409 insufficient_stock`, listing every short line.
10. For each allocation: `UPDATE inventory_balances SET on_hand_quantity = on_hand_quantity - @q, version = version + 1, updated_at = @now WHERE id = @id AND on_hand_quantity >= @q RETURNING on_hand_quantity`. Zero rows: rollback and `busy` (unreachable under the lock; the guard stays).
11. Insert order, items, placement movements, allocations, first history row (`null -> placed`).
12. Delete the cart's `cart_items`, touch `carts.updated_at`. The cart row stays.
13. Commit, then map to the DTO.

Every rejection is a rollback. No path writes a key without a committed order, so `ux_orders_user_id_idempotency_key` is the last line of defence.

Placement movements: `operation_key = order-place-<allocationId:N>`, `reference = SS-<number>`, actor is the customer, fingerprint is SHA-256 of `v1|order|<orderId:N>|<allocationId:N>|<delta>`. Keys beginning `order-` are reserved: the admin receive and adjust endpoints reject them with `400 validation_failed`, so an admin key cannot pre-empt a reversal.

## State machine

```
placed -> processing | cancelled
processing -> shipped | cancelled
shipped -> completed
completed, cancelled: terminal
```

| Actor | Allowed |
|---|---|
| Customer (own order) | `placed -> cancelled` |
| Warehouse | forward transitions only |
| Admin | forward transitions and `placed/processing -> cancelled` |

Cancelling restocks inventory, and D-045 keeps stock writes Admin-only, so a Warehouse user cancelling gets `403 forbidden`. The transition table lives in Domain and is the only source of truth.

"Same status" is evaluated before the role check, so a Warehouse user sending `cancelled` for an already cancelled order gets `200` with `replayed: true`. Nothing is written and the response holds no customer data.

## Cancellation and staff transitions (READ COMMITTED, one transaction)

1. `lock_timeout = 5s`; `SELECT ... FROM orders WHERE id = @id [AND user_id = @u] FOR UPDATE`. Missing: `404`.
2. Already at target: `200` with `replayed: true`, no write.
3. Transition not allowed: `409 order_transition_invalid`.
4. Cancelling: for allocations ordered by `(product_variant_id, warehouse code)`, `INSERT ... ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE ... WHERE on_hand + q <= max RETURNING`. Zero rows: rollback, `409 stock_limit_exceeded`, order stays as it was. Inactive warehouses and variants are restocked anyway: the goods physically returned there.
5. Reversal movement per allocation: `operation_key = order-cancel-<allocationId:N>`. The existing unique index makes double reversal impossible at the database level.
6. Update status, append history, commit.

The `orders` row lock serialises cancel against cancel and cancel against shipment.

## Web

`/checkout` shows address, items, server subtotal, "Kargo hesaplanmadı", "Ödeme alınmaz", and never a grand total. Button text: **Sipariş oluştur — ödeme alınmaz**. `/siparisler` and `/siparisler/[id]` are private (`server/privacy.mjs`), client-fetched, and never carry order PII in HTML or RSC.

The idempotency key lives in `sessionStorage`, bound to user id + address id + quote, and survives reloads. It is reused for a retry of the same intention and replaced only when the quote or address changes or the order succeeds. An unknown outcome (network error, timeout, 5xx) never generates a new key.

## Shared local rules

- Disposable PostgreSQL 17: `127.0.0.1:5434`. Run with `bash <scratchpad>/with-test-db.sh <command>`. Never print it or the environment.
- Dev database on 5433: never touched by specialists.
- Ports 5081, 5082, 3200 belong to one E2E run at a time.
- Do not stop processes you did not start.
