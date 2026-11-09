using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Administration;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Inventory;

/// <summary>
/// Stok yazma servisi (F5.3, D-045..D-047). Akış tek transaction'dır (READ COMMITTED) ve sırası sözleşmededir:
/// <list type="number">
/// <item>SET LOCAL lock_timeout = 5s (kilit beklemesi sonsuz sürmez; 55P03 => <see cref="StockOperationStatus.Busy"/>).</item>
/// <item><c>pg_advisory_xact_lock(hashtextextended(key, 0))</c>: AYNI anahtarlı istekler sıraya girer. Kilit commit/rollback'te kendiliğinden düşer.</item>
/// <item>Anahtar daha önce işlendiyse parmak izi karşılaştırılır: aynı => ilk sonuç (replay), farklı => <see cref="StockOperationStatus.KeyReused"/>.
/// Okuma kilidi aldıktan SONRA yapılır; READ COMMITTED'da her ifade en son commit'i gördüğünden, kilidi bekleyen kopya ilk kopyanın hareketini görür.</item>
/// <item>Depo ve varyant FOR SHARE ile okunur: depo, işlem sürerken eşzamanlı pasifleştirilemez (PATCH UPDATE'i commit'e kadar bekler).</item>
/// <item>Tek koşullu SQL ile bakiye: artışta <c>INSERT … ON CONFLICT DO UPDATE … WHERE on_hand + delta &lt;= max</c>, azalışta
/// <c>UPDATE … WHERE on_hand + delta &gt;= 0</c>; ikisi de RETURNING ile yeni bakiyeyi verir. Okuma-değiştirme-yazma YOKTUR: PostgreSQL satır kilidi
/// eşzamanlı ikinci UPDATE'i bekletir ve koşulu yeni değerle yeniden değerlendirir, son birim iki kez tüketilemez. 0 satır => rollback + 409.</item>
/// <item>Hareket eklenir; commit. Reddedilen her yol rollback'tir: anahtar tüketilmez, hareket kalmaz.</item>
/// </list>
/// CHECK kısıtları ve unique indeks son savunma hattıdır. Parametrik SQL dışında hiçbir şey birleştirilmez.
/// </summary>
internal sealed class InventoryService(SSCommerceDbContext db, TimeProvider clock) : IInventoryService
{
    /// <summary>Kilit bekleme üst sınırı (advisory lock + satır kilidi). Sabit metin: kullanıcı girdisi içermez.</summary>
    private const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    public async Task<StockOperationResult> ApplyStockOperationAsync(StockCommand command, CancellationToken cancellationToken)
    {
        var fingerprint = command.Fingerprint();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var result = await ApplyAsync(transaction, command, fingerprint, cancellationToken);
            if (result.Status == StockOperationStatus.Applied)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (IsLockTimeout(exception))
        {
            // Dispose rollback yapar; hiçbir şey yazılmadı.
            return new StockOperationResult(StockOperationStatus.Busy, null);
        }
    }

    private async Task<StockOperationResult> ApplyAsync(
        IDbContextTransaction transaction,
        StockCommand command,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using (var timeout = CreateCommand(transaction, LockTimeoutSql))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var advisory = CreateCommand(transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))"))
        {
            advisory.Parameters.AddWithValue("key", command.OperationKey);
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await ReadExistingAsync(transaction, command.OperationKey, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? new StockOperationResult(StockOperationStatus.Replayed, existing.Movement with { Replayed = true })
                : new StockOperationResult(StockOperationStatus.KeyReused, null);
        }

        var warehouseActive = await ReadActiveFlagAsync(
            transaction, "SELECT is_active FROM warehouses WHERE id = @id FOR SHARE", command.WarehouseId, cancellationToken);
        if (warehouseActive is null)
        {
            return Rejected(StockOperationStatus.WarehouseNotFound);
        }

        var variant = await ReadVariantAsync(transaction, command.VariantId, cancellationToken);
        if (variant is null)
        {
            return Rejected(StockOperationStatus.VariantNotFound);
        }

        if (warehouseActive is false)
        {
            return Rejected(StockOperationStatus.WarehouseInactive);
        }

        if (!variant.IsActive)
        {
            return Rejected(StockOperationStatus.VariantInactive);
        }

        var now = clock.GetUtcNow();
        var onHandAfter = command.Delta > 0
            ? await IncreaseAsync(transaction, command, now, cancellationToken)
            : await DecreaseAsync(transaction, command, now, cancellationToken);
        if (onHandAfter is null)
        {
            return Rejected(command.Delta > 0 ? StockOperationStatus.StockLimitExceeded : StockOperationStatus.InsufficientStock);
        }

        var movement = new StockMovement(
            command.WarehouseId,
            command.VariantId,
            variant.Sku,
            command.Delta,
            command.Type,
            command.ActorUserId,
            command.Reason,
            command.Reference,
            command.OperationKey,
            fingerprint,
            onHandAfter.Value,
            now);
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync(cancellationToken);

        return new StockOperationResult(StockOperationStatus.Applied, ToDto(movement, replayed: false));
    }

    private static StockOperationResult Rejected(StockOperationStatus status) => new(status, null);

    /// <summary>Artış: yoksa ekler, varsa üst sınır koşuluyla artırır. 0 satır = sınır aşılırdı.</summary>
    private async Task<int?> IncreaseAsync(IDbContextTransaction transaction, StockCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var sql = CreateCommand(
            transaction,
            """
            INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
            VALUES (@id, @warehouse, @variant, @delta, 1, @now, @now)
            ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE
            SET on_hand_quantity = inventory_balances.on_hand_quantity + EXCLUDED.on_hand_quantity,
                version = inventory_balances.version + 1,
                updated_at = EXCLUDED.updated_at
            WHERE inventory_balances.on_hand_quantity + EXCLUDED.on_hand_quantity <= @max
            RETURNING on_hand_quantity
            """);
        sql.Parameters.AddWithValue("id", Guid.CreateVersion7());
        sql.Parameters.AddWithValue("warehouse", command.WarehouseId);
        sql.Parameters.AddWithValue("variant", command.VariantId);
        sql.Parameters.AddWithValue("delta", command.Delta);
        sql.Parameters.AddWithValue("now", now);
        sql.Parameters.AddWithValue("max", InventoryRules.MaxOnHandQuantity);
        return await sql.ExecuteScalarAsync(cancellationToken) as int?;
    }

    /// <summary>Azalış: yalnız bakiye yeterliyse düşer. Satır yok ya da yetersiz = 0 satır.</summary>
    private async Task<int?> DecreaseAsync(IDbContextTransaction transaction, StockCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var sql = CreateCommand(
            transaction,
            """
            UPDATE inventory_balances
            SET on_hand_quantity = on_hand_quantity + @delta,
                version = version + 1,
                updated_at = @now
            WHERE warehouse_id = @warehouse AND product_variant_id = @variant AND on_hand_quantity + @delta >= 0
            RETURNING on_hand_quantity
            """);
        sql.Parameters.AddWithValue("delta", command.Delta);
        sql.Parameters.AddWithValue("now", now);
        sql.Parameters.AddWithValue("warehouse", command.WarehouseId);
        sql.Parameters.AddWithValue("variant", command.VariantId);
        return await sql.ExecuteScalarAsync(cancellationToken) as int?;
    }

    private async Task<ExistingMovement?> ReadExistingAsync(IDbContextTransaction transaction, string operationKey, CancellationToken cancellationToken)
    {
        await using var sql = CreateCommand(
            transaction,
            """
            SELECT id, warehouse_id, product_variant_id, sku, quantity_delta, operation_type, reason, reference, on_hand_after, occurred_at, request_fingerprint, actor_user_id
            FROM stock_movements
            WHERE operation_key = @key
            """);
        sql.Parameters.AddWithValue("key", operationKey);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var movement = new StockMovementDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetGuid(11),
            Replayed: false);
        return new ExistingMovement(movement, reader.GetString(10).Trim());
    }

    private async Task<bool?> ReadActiveFlagAsync(IDbContextTransaction transaction, string statement, Guid id, CancellationToken cancellationToken)
    {
        await using var sql = CreateCommand(transaction, statement);
        sql.Parameters.AddWithValue("id", id);
        return await sql.ExecuteScalarAsync(cancellationToken) as bool?;
    }

    private async Task<VariantRow?> ReadVariantAsync(IDbContextTransaction transaction, Guid variantId, CancellationToken cancellationToken)
    {
        await using var sql = CreateCommand(transaction, "SELECT sku, is_active FROM product_variants WHERE id = @id FOR SHARE");
        sql.Parameters.AddWithValue("id", variantId);
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new VariantRow(reader.GetString(0), reader.GetBoolean(1)) : null;
    }

    private NpgsqlCommand CreateCommand(IDbContextTransaction transaction, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandTimeout = DependencyInjection.CommandTimeoutSeconds;
        return command;
    }

    /// <summary>Depo + <c>warehouse.create</c> denetim satırı TEK transaction'da (Faz 8). Kod tekrarı unique indeksle belirlenir.</summary>
    public async Task<WarehouseWriteResult> CreateWarehouseAsync(Guid actorUserId, NewWarehouse warehouse, CancellationToken cancellationToken)
    {
        var entity = new Warehouse(warehouse.Code, warehouse.Name);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            db.Warehouses.Add(entity);
            AdminAudit.Add(db, clock, actorUserId, "warehouse.create", AdminAuditRules.TargetWarehouse, entity.Id, new
            {
                code = entity.Code,
                name = entity.Name,
                isActive = entity.IsActive,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new WarehouseWriteResult(WarehouseWriteStatus.CodeTaken, null);
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new WarehouseWriteResult(WarehouseWriteStatus.Busy, null);
        }

        return new WarehouseWriteResult(WarehouseWriteStatus.Created, ToDto(entity));
    }

    /// <summary>
    /// Depo satırı <c>FOR UPDATE</c> ile kilitlenir (stok işlemi depoyu <c>FOR SHARE</c> okur; bu UPDATE onların commit'ini bekler ve tersi: pasifleştirme
    /// yarışı olmaz). Eski değerler kilitli satırdandır; yalnız gerçekten değişen alan varsa denetim satırı yazılır.
    /// </summary>
    public async Task<WarehouseWriteResult> UpdateWarehouseAsync(Guid actorUserId, Guid id, WarehouseChange change, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            var entity = (await db.Warehouses
                .FromSqlInterpolated($"SELECT * FROM warehouses WHERE id = {id} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (entity is null)
            {
                return new WarehouseWriteResult(WarehouseWriteStatus.NotFound, null);
            }

            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();
            if (change.Name is not null && change.Name != entity.Name)
            {
                oldValues["name"] = entity.Name;
                newValues["name"] = change.Name;
                entity.Rename(change.Name);
            }

            if (change.IsActive is { } isActive && isActive != entity.IsActive)
            {
                oldValues["isActive"] = entity.IsActive;
                newValues["isActive"] = isActive;
                entity.SetActive(isActive);
            }

            if (newValues.Count > 0)
            {
                AdminAudit.Add(db, clock, actorUserId, "warehouse.update", AdminAuditRules.TargetWarehouse, id, new { old = oldValues, @new = newValues });
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new WarehouseWriteResult(WarehouseWriteStatus.Updated, ToDto(entity));
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new WarehouseWriteResult(WarehouseWriteStatus.Busy, null);
        }
    }

    internal static WarehouseDto ToDto(Warehouse warehouse) =>
        new(warehouse.Id, warehouse.Code, warehouse.Name, warehouse.IsActive, warehouse.CreatedAt, warehouse.UpdatedAt);

    internal static StockMovementDto ToDto(StockMovement movement, bool replayed) => new(
        movement.Id,
        movement.WarehouseId,
        movement.ProductVariantId,
        movement.Sku,
        movement.QuantityDelta,
        StockOperationTypes.ToWire(movement.OperationType),
        movement.Reason,
        movement.Reference,
        movement.OnHandAfter,
        movement.OccurredAt,
        movement.ActorUserId,
        replayed);

    /// <summary>Kilit zaman aşımı (55P03) ya da deadlock (40P01): hiçbir şey yazılmadı, güvenle tekrar denenebilir.</summary>
    private static bool IsLockTimeout(Exception exception) => TransactionalSql.IsLockTimeout(exception);

    private sealed record ExistingMovement(StockMovementDto Movement, string Fingerprint);

    private sealed record VariantRow(string Sku, bool IsActive);
}
