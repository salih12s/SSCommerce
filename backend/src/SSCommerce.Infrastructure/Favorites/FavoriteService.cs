using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Application.Catalog;
using SSCommerce.Application.Favorites;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;
using SSCommerce.Infrastructure.Catalog;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Favorites;

/// <summary>
/// Favori servisi (MD-1, D-088). EKLEME kullanıcı başına sıraya sokulur: <c>pg_advisory_xact_lock(int4, int4)</c> (adres servisiyle aynı iki argümanlı
/// biçim, AYRI ad alanı). Sayım ve "zaten var mı" kararı kilit İÇİNDE verilir, yani eşzamanlı eklemelerde 100 sınırı aşılamaz ve aynı ürüne iki
/// eşzamanlı PUT'tan ikincisi kilidi alınca satırı görür (yazmaz). Kilit atlansa bile bileşik PK son savunma hattıdır: benzersizlik ihlali (23505)
/// başarı sayılır. SİLME tek koşullu DELETE'tir (kilitsiz, idempotent): yalnız sayıyı azaltır, sınırı bozamaz.
/// Her sorgu <c>user_id</c> ile kapsamlıdır; kullanıcı kimliği yalnız çağıranın cookie'sinden gelir.
/// </summary>
internal sealed class FavoriteService(SSCommerceDbContext db, TimeProvider clock) : IFavoriteService
{
    /// <summary>Favori kilit ad alanı (ASCII "FAVS"). Adres ("ADDR") ve stok (tek argümanlı) kilit alanlarından ayrıdır.</summary>
    internal const int LockNamespace = 0x46415653;

    public async Task<FavoriteListDto> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Join: favori satırında ürün gezinme özelliği yoktur. FK RESTRICT olduğundan ürün her zaman vardır (satıştan kalkan ürün de listede kalır).
        var page = await (
                from f in db.CustomerFavorites.AsNoTracking()
                where f.UserId == userId
                join p in db.Products.AsNoTracking() on f.ProductId equals p.Id
                orderby f.CreatedAt descending, p.Id
                select new
                {
                    p.Id,
                    p.Slug,
                    p.Name,
                    p.IsDemo,
                    ProductActive = p.IsActive,
                    CategoryActive = p.Category!.IsActive,
                    CategorySlug = p.Category.Slug,
                    CategoryName = p.Category.Name,
                    f.CreatedAt,
                    MinActivePrice = p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.Price),
                    MinAnyPrice = p.Variants.Min(v => (decimal?)v.Price),
                    ImageUrl = db.ProductImages.Where(i => i.ProductId == p.Id && i.IsPrimary).Select(i => i.StorageKey).FirstOrDefault(),
                    ImageAlt = db.ProductImages.Where(i => i.ProductId == p.Id && i.IsPrimary).Select(i => i.AltText).FirstOrDefault(),
                    InStock = db.ProductVariants
                        .Any(v => v.ProductId == p.Id && v.IsActive &&
                            StockAvailability.ActiveBalances(db).Any(b => b.ProductVariantId == v.Id && b.OnHandQuantity > 0)),
                })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(x =>
            {
                var available = FavoriteRules.IsAvailable(x.ProductActive, x.CategoryActive, x.MinActivePrice is not null);
                return new FavoriteItemDto(
                    x.Id,
                    x.Slug,
                    x.Name,
                    new CategoryRefDto(x.CategorySlug, x.CategoryName),
                    x.ImageUrl is null ? null : new PrimaryImageDto(x.ImageUrl, x.ImageAlt!),
                    FavoriteRules.StartingPriceMinor(
                        x.MinActivePrice is { } active ? Money.ToMinor(active) : null,
                        x.MinAnyPrice is { } any ? Money.ToMinor(any) : null),
                    Money.Currency,
                    x.IsDemo,
                    available && x.InStock, // satıştan kalkan ürün "stokta" görünmez
                    x.CreatedAt,
                    available);
            })
            .ToList();
        return new FavoriteListDto(items);
    }

    public async Task<FavoriteIdsDto> ListIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var ids = await db.CustomerFavorites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt).ThenBy(f => f.ProductId)
            .Select(f => f.ProductId)
            .ToListAsync(cancellationToken);
        return new FavoriteIdsDto(ids);
    }

    public async Task<FavoriteWriteStatus> AddAsync(Guid userId, Guid productId, CancellationToken cancellationToken)
    {
        // Satışta olmayan ürün favoriye eklenemez (katalogdaki görünür ürün tanımı). Kilit dışı: geçersiz istek kilit tutmasın.
        var visible = await db.Products.AsNoTracking()
            .AnyAsync(p => p.Id == productId && p.IsActive && p.Category!.IsActive && p.Variants.Any(v => v.IsActive), cancellationToken);
        if (!visible)
        {
            return FavoriteWriteStatus.ProductNotFound;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            await TransactionalSql.ExecuteAsync(
                db,
                transaction,
                "SELECT pg_advisory_xact_lock(@ns, hashtext(@user))",
                cancellationToken,
                ("ns", LockNamespace),
                ("user", userId.ToString("D")));

            // Kilitten SONRA: READ COMMITTED'da her ifade yeni anlık görüntü alır, önceki kilit sahiplerinin commit'lediği satırlar görünür.
            var alreadyFavorite = await db.CustomerFavorites.AnyAsync(f => f.UserId == userId && f.ProductId == productId, cancellationToken);
            var count = alreadyFavorite ? 0 : await db.CustomerFavorites.CountAsync(f => f.UserId == userId, cancellationToken);

            switch (FavoriteRules.DecideAdd(alreadyFavorite, count))
            {
                case FavoriteAddDecision.AlreadyFavorite:
                    await transaction.RollbackAsync(cancellationToken); // yazılacak bir şey yok
                    return FavoriteWriteStatus.Ok;
                case FavoriteAddDecision.LimitReached:
                    await transaction.RollbackAsync(cancellationToken);
                    return FavoriteWriteStatus.LimitReached;
            }

            db.CustomerFavorites.Add(new CustomerFavorite(userId, productId, clock.GetUtcNow()));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // Aynı (kullanıcı, ürün) çiftine başka bir yazar önce ulaştı: hedef durum zaten sağlandı.
                await transaction.RollbackAsync(cancellationToken);
                return FavoriteWriteStatus.Ok;
            }

            await transaction.CommitAsync(cancellationToken);
            return FavoriteWriteStatus.Ok;
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return FavoriteWriteStatus.Busy;
        }
    }

    public async Task<FavoriteWriteStatus> RemoveAsync(Guid userId, Guid productId, CancellationToken cancellationToken)
    {
        // Koşullu tek DELETE: favoride değilse 0 satır, yine başarı. Kullanıcı kapsamı WHERE içindedir.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM customer_favorites WHERE user_id = {userId} AND product_id = {productId}", cancellationToken);
        return FavoriteWriteStatus.Ok;
    }

    /// <summary>PostgreSQL 23505 (benzersizlik ihlali). İç istisna zincirinden okunur.</summary>
    internal static bool IsUniqueViolation(Exception exception) =>
        TransactionalSql.PostgresOf(exception) is { SqlState: PostgresErrorCodes.UniqueViolation };
}
