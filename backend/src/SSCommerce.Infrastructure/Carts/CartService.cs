using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SSCommerce.Application.Carts;
using SSCommerce.Domain.Cart;
using SSCommerce.Infrastructure.Catalog;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Carts;

/// <summary>
/// Sepet servisi (F6.1/F6.2, D-049..D-051). Yazma akışı tek transaction'dır (READ COMMITTED) ve KULLANICI BAŞINA sıralanır:
/// <list type="number">
/// <item><c>INSERT … ON CONFLICT (user_id) DO NOTHING</c>: sepet yoksa oluşturur (yalnız ekleme ucunda); eşzamanlı ilk eklemeler tek sepet verir.</item>
/// <item><c>SELECT … FROM carts WHERE user_id = @u FOR UPDATE</c>: aynı kullanıcının diğer yazmaları commit'e kadar bekler. Bu kilit altında
/// okuma-değiştirme-yazma güvenlidir (adet toplama, satır sayısı sınırı, tavan denetimi). <c>ux_cart_items_cart_id_product_variant_id</c> yine
/// son savunma hattıdır.</item>
/// <item>Varyantın fiyatı/aktifliği/stoğu kilit altında okunur; kurallar uygulanır; satır yazılır; sepet <c>updated_at</c> güncellenir.</item>
/// </list>
/// Sepet stok AYIRMAZ ve stok düşmez: yalnız ARTIŞ stoktan fazla olamaz, azaltma her zaman serbesttir (D-050). Tutar her zaman güncel katalog
/// fiyatından hesaplanır (<see cref="CartCalculator"/>); <c>unit_price_at_add</c> yalnız "fiyat değişti" ipucudur.
/// </summary>
internal sealed class CartService(SSCommerceDbContext db, CartLineReader reader, TimeProvider clock) : ICartService
{
    public async Task<CartOperationResult> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var lines = await reader.LoadAsync(userId, cancellationToken);
        return Present(CartCalculator.Calculate(lines));
    }

    public Task<CartOperationResult> AddItemAsync(Guid userId, AddCartItemCommand command, CancellationToken cancellationToken) =>
        WriteAsync(userId, createCart: true, (cartId, ct) => AddAsync(userId, cartId, command, ct), cancellationToken);

    public Task<CartOperationResult> SetQuantityAsync(Guid userId, Guid itemId, int quantity, CancellationToken cancellationToken) =>
        WriteAsync(userId, createCart: false, (cartId, ct) => SetAsync(cartId, itemId, quantity, ct), cancellationToken);

    public Task<CartOperationResult> RemoveItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        WriteAsync(userId, createCart: false, (cartId, ct) => RemoveAsync(cartId, itemId, ct), cancellationToken);

    /// <param name="Failure">Null değilse işlem reddedildi: rollback.</param>
    /// <param name="Increased">Sepeti BÜYÜTEN bir yazma mı (adet artışı / yeni satır)? Taşma durumunda yalnız bunlar geri alınır.</param>
    private sealed record Step(CartOperationResult? Failure, bool Increased);

    private async Task<CartOperationResult> WriteAsync(
        Guid userId,
        bool createCart,
        Func<Guid, CancellationToken, Task<Step>> operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            var now = clock.GetUtcNow();

            if (createCart)
            {
                await TransactionalSql.ExecuteAsync(
                    db,
                    transaction,
                    "INSERT INTO carts (id, user_id, created_at, updated_at) VALUES (@id, @user, @now, @now) ON CONFLICT (user_id) DO NOTHING",
                    cancellationToken,
                    ("id", Guid.CreateVersion7()),
                    ("user", userId),
                    ("now", now));
            }

            // Kullanıcı başına sıralama noktası. Sepet yoksa (PATCH/DELETE) satır da yoktur.
            var locked = await TransactionalSql.ScalarAsync(
                db, transaction, "SELECT id FROM carts WHERE user_id = @user FOR UPDATE", cancellationToken, ("user", userId));
            if (locked is not Guid cartId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CartOperationResult(CartOperationStatus.ItemNotFound, null);
            }

            var step = await operation(cartId, cancellationToken);
            if (step.Failure is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return step.Failure;
            }

            await TransactionalSql.ExecuteAsync(
                db,
                transaction,
                "UPDATE carts SET updated_at = @now WHERE id = @id",
                cancellationToken,
                ("now", now),
                ("id", cartId));

            var calculation = CartCalculator.Calculate(await reader.LoadAsync(userId, cancellationToken));
            if (calculation.TooLarge && step.Increased)
            {
                // Büyüten yazma toplamı Money sınırının üstüne çıkarırdı: geri al, sepet olduğu gibi kalır.
                await transaction.RollbackAsync(cancellationToken);
                return new CartOperationResult(CartOperationStatus.TotalTooLarge, null);
            }

            await transaction.CommitAsync(cancellationToken);
            return Present(calculation);
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new CartOperationResult(CartOperationStatus.Busy, null);
        }
    }

    private async Task<Step> AddAsync(Guid userId, Guid cartId, AddCartItemCommand command, CancellationToken cancellationToken)
    {
        _ = userId;
        var variant = await ReadVariantAsync(command.VariantId, cancellationToken);
        if (variant is null)
        {
            return Reject(CartOperationStatus.VariantNotFound);
        }

        if (!variant.Purchasable)
        {
            return Reject(CartOperationStatus.VariantInactive);
        }

        var existing = await db.CartItems.FirstOrDefaultAsync(
            i => i.CartId == cartId && i.ProductVariantId == command.VariantId, cancellationToken);
        var newQuantity = (existing?.Quantity ?? 0) + command.Quantity;
        if (newQuantity > CartRules.MaxLineQuantity)
        {
            return Reject(CartOperationStatus.QuantityLimit);
        }

        if (existing is null &&
            await db.CartItems.CountAsync(i => i.CartId == cartId, cancellationToken) >= CartRules.MaxDistinctLines)
        {
            return Reject(CartOperationStatus.LineLimit);
        }

        var available = StockAvailability.Cap(variant.Available);
        if (newQuantity > available)
        {
            return new Step(new CartOperationResult(CartOperationStatus.InsufficientStock, null, available), false);
        }

        if (existing is null)
        {
            db.CartItems.Add(new CartItem(cartId, command.VariantId, newQuantity, variant.Price));
        }
        else
        {
            existing.ChangeQuantity(newQuantity, variant.Price);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new Step(null, Increased: true);
    }

    private async Task<Step> SetAsync(Guid cartId, Guid itemId, int quantity, CancellationToken cancellationToken)
    {
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CartId == cartId, cancellationToken);
        if (item is null)
        {
            return Reject(CartOperationStatus.ItemNotFound);
        }

        var variant = await ReadVariantAsync(item.ProductVariantId, cancellationToken);
        if (variant is null)
        {
            // Varyant satırı RESTRICT FK ile korunur; yine de kaybolduysa satır güvenle yok sayılır.
            return Reject(CartOperationStatus.ItemNotFound);
        }

        var increase = quantity > item.Quantity;
        if (increase)
        {
            if (!variant.Purchasable)
            {
                return Reject(CartOperationStatus.VariantInactive);
            }

            var available = StockAvailability.Cap(variant.Available);
            if (quantity > available)
            {
                return new Step(new CartOperationResult(CartOperationStatus.InsufficientStock, null, available), false);
            }
        }

        // Azaltma/aynı adet her zaman serbest; ipucu fiyat güncel katalog fiyatıyla yenilenir.
        item.ChangeQuantity(quantity, variant.Price);
        await db.SaveChangesAsync(cancellationToken);
        return new Step(null, increase);
    }

    private async Task<Step> RemoveAsync(Guid cartId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.CartItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CartId == cartId, cancellationToken);
        if (item is null)
        {
            return Reject(CartOperationStatus.ItemNotFound);
        }

        db.CartItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return new Step(null, Increased: false);
    }

    private sealed record VariantInfo(decimal Price, bool Purchasable, long Available);

    private async Task<VariantInfo?> ReadVariantAsync(Guid variantId, CancellationToken cancellationToken) =>
        await (
            from variant in db.ProductVariants.AsNoTracking()
            where variant.Id == variantId
            join product in db.Products on variant.ProductId equals product.Id
            join category in db.Categories on product.CategoryId equals category.Id
            select new VariantInfo(
                variant.Price,
                variant.IsActive && product.IsActive && category.IsActive,
                StockAvailability.ActiveBalances(db).Where(b => b.ProductVariantId == variant.Id).Sum(b => (long?)b.OnHandQuantity) ?? 0L))
            .FirstOrDefaultAsync(cancellationToken);

    private static Step Reject(CartOperationStatus status) => new(new CartOperationResult(status, null), false);

    private static CartOperationResult Present(CartCalculation calculation) =>
        calculation.TooLarge
            ? new CartOperationResult(CartOperationStatus.TotalTooLarge, null)
            : new CartOperationResult(CartOperationStatus.Ok, calculation.Cart);
}
