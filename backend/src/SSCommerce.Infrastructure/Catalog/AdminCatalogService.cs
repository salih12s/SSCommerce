using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Administration;
using SSCommerce.Domain.Catalog;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Catalog;

/// <summary>
/// Admin katalog yazma servisi (Faz 8, D-068/D-069, docs/CONTRACT_PHASE_8.md "Catalog"). Her yazma TEK READ COMMITTED transaction'dır:
/// <list type="number">
/// <item><c>SET LOCAL lock_timeout = 5s</c>: kilit beklemesi sonsuz sürmez; 55P03 ve 40P01 (deadlock) =&gt; <see cref="CatalogWriteStatus.Busy"/>.</item>
/// <item>KATEGORİ yazmaları: <c>pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0))</c> ÖNCE (veritabanındaki
/// <c>trg_categories_guard_parent</c> aynı kilidi alır; sıra "advisory → satır" olmazsa trigger kilidi ile FK <c>FOR KEY SHARE</c> kilidi deadlock verebilir).</item>
/// <item>Hedef satır <c>SELECT * … FOR UPDATE</c> ile KİLİTLENİR ve varlık o sorgudan yüklenir; gönderilen <c>version</c> bu KİLİTLİ satırdaki sürümle
/// karşılaştırılır (daha önce yüklenmiş bir varlıkla değil): bekleyen rakip commit'ini görür, kayıp güncelleme olmaz. Uyuşmazlık =&gt; 409 + <c>currentVersion</c>.</item>
/// <item>Değişiklik yalnız gerçekten farklı alanlarda uygulanır; hiçbir şey değişmediyse sürüm artmaz ve denetim yazılmaz (no-op).</item>
/// <item><c>BumpVersion()</c> tam +1; denetim satırı (eski değerler kilitli satırdan) AYNI <c>SaveChanges</c> toplu işleminde yazılır: denetim yazılamazsa
/// değişiklik de geri alınır.</item>
/// </list>
/// Kilit sırası: kategori ağacı advisory → kategori satırı → ürün satırı → varyant satırı; ürün taşıması yeni kategoriyi kendi satırından SONRA
/// <c>FOR SHARE</c> alır. Sipariş verme <c>FOR SHARE OF v, p, c</c> aldığından admin yazmaları devam eden siparişleri (en çok 5 sn) bekler ve döngü oluşmaz.
/// Slug ve SKU değişmezdir. Benzersizlik ve döngü/derinlik ihlalleri veritabanı kısıtlarından gelir ve belirli 409 kodlarına eşlenir.
/// </summary>
internal sealed class AdminCatalogService(SSCommerceDbContext db, TimeProvider clock, AdminCatalogQueries queries) : IAdminCatalogService
{
    private const string CategoryTreeLockSql = "SELECT pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0))";

    // ---------- Kategori ----------

    public Task<CatalogWriteResult<AdminCategoryDto>> CreateCategoryAsync(Guid actorUserId, NewCategory category, CancellationToken cancellationToken) =>
        RunAsync<AdminCategoryDto>(true, async transaction =>
        {
            if (category.ParentId is { } parentId && !await ExistsSharedAsync(transaction, "categories", parentId, cancellationToken))
            {
                return Fail<AdminCategoryDto>(CatalogWriteStatus.CategoryNotFound);
            }

            var entity = new Category(category.Name, category.Slug, category.ParentId, category.SortOrder);
            entity.SetActive(category.IsActive);
            db.Categories.Add(entity);
            AdminAudit.Add(db, clock, actorUserId, "category.create", AdminAuditRules.TargetCategory, entity.Id, new
            {
                name = entity.Name,
                slug = entity.Slug,
                parentId = entity.ParentId,
                sortOrder = entity.SortOrder,
                isActive = entity.IsActive,
            });
            await db.SaveChangesAsync(cancellationToken);
            return Ok(ToDto(entity, 0));
        }, cancellationToken);

    public Task<CatalogWriteResult<AdminCategoryDto>> UpdateCategoryAsync(Guid actorUserId, Guid id, CategoryChange change, CancellationToken cancellationToken) =>
        RunAsync<AdminCategoryDto>(true, async transaction =>
        {
            var entity = (await db.Categories
                .FromSqlInterpolated($"SELECT * FROM categories WHERE id = {id} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (entity is null)
            {
                return Fail<AdminCategoryDto>(CatalogWriteStatus.CategoryNotFound);
            }

            if (entity.Version != change.Version)
            {
                return Conflict<AdminCategoryDto>(entity.Version);
            }

            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();
            if (change.Name is { IsSet: true, Value: { } name } && name != entity.Name)
            {
                oldValues["name"] = entity.Name;
                newValues["name"] = name;
                entity.Rename(name);
            }

            if (change.ParentId.IsSet && change.ParentId.Value != entity.ParentId)
            {
                if (change.ParentId.Value is { } newParent)
                {
                    if (newParent == id)
                    {
                        return Fail<AdminCategoryDto>(CatalogWriteStatus.CategoryCycle);
                    }

                    if (!await ExistsSharedAsync(transaction, "categories", newParent, cancellationToken))
                    {
                        return Fail<AdminCategoryDto>(CatalogWriteStatus.CategoryNotFound);
                    }
                }

                oldValues["parentId"] = entity.ParentId;
                newValues["parentId"] = change.ParentId.Value;
                entity.SetParent(change.ParentId.Value);
            }

            if (change.SortOrder.IsSet && (int)change.SortOrder.Value != entity.SortOrder)
            {
                oldValues["sortOrder"] = entity.SortOrder;
                newValues["sortOrder"] = (int)change.SortOrder.Value;
                entity.SetSortOrder((int)change.SortOrder.Value);
            }

            if (change.IsActive.IsSet && change.IsActive.Value != entity.IsActive)
            {
                oldValues["isActive"] = entity.IsActive;
                newValues["isActive"] = change.IsActive.Value;
                entity.SetActive(change.IsActive.Value);
            }

            if (newValues.Count > 0)
            {
                entity.BumpVersion();
                AdminAudit.Add(db, clock, actorUserId, "category.update", AdminAuditRules.TargetCategory, id, new { old = oldValues, @new = newValues });
                await db.SaveChangesAsync(cancellationToken);
            }

            var productCount = await db.Products.CountAsync(p => p.CategoryId == id, cancellationToken);
            return Ok(ToDto(entity, productCount));
        }, cancellationToken);

    // ---------- Ürün ----------

    public Task<CatalogWriteResult<AdminProductDetailDto>> CreateProductAsync(Guid actorUserId, NewProduct product, CancellationToken cancellationToken) =>
        RunAsync<AdminProductDetailDto>(false, async transaction =>
        {
            if (!await ExistsSharedAsync(transaction, "categories", product.CategoryId, cancellationToken))
            {
                return Fail<AdminProductDetailDto>(CatalogWriteStatus.CategoryNotFound);
            }

            // isDemo HER ZAMAN false: istemci bu alanı gönderemez (doğrulayıcı okumaz).
            var entity = new Product(product.CategoryId, product.Name, product.Slug, product.Brand, product.Description, product.Specifications, isDemo: false);
            entity.SetActive(product.IsActive);
            db.Products.Add(entity);
            AdminAudit.Add(db, clock, actorUserId, "product.create", AdminAuditRules.TargetProduct, entity.Id, new
            {
                categoryId = entity.CategoryId,
                name = entity.Name,
                slug = entity.Slug,
                brand = entity.Brand,
                description = AdminAudit.Clip(entity.Description),
                specificationCount = entity.Specifications?.Count ?? 0,
                isActive = entity.IsActive,
            });
            await db.SaveChangesAsync(cancellationToken);
            return Ok(await LoadDetailAsync(entity.Id, cancellationToken));
        }, cancellationToken);

    public Task<CatalogWriteResult<AdminProductDetailDto>> UpdateProductAsync(Guid actorUserId, Guid id, ProductChange change, CancellationToken cancellationToken) =>
        RunAsync<AdminProductDetailDto>(false, async transaction =>
        {
            var entity = await LockProductAsync(id, cancellationToken);
            if (entity is null)
            {
                return Fail<AdminProductDetailDto>(CatalogWriteStatus.ProductNotFound);
            }

            if (entity.Version != change.Version)
            {
                return Conflict<AdminProductDetailDto>(entity.Version);
            }

            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();
            if (change.CategoryId is { IsSet: true, Value: { } categoryId } && categoryId != entity.CategoryId)
            {
                // Taşıma: yeni kategori, ürünün kendi satırından SONRA FOR SHARE ile kilitlenir (sözleşme kilit sırası).
                if (!await ExistsSharedAsync(transaction, "categories", categoryId, cancellationToken))
                {
                    return Fail<AdminProductDetailDto>(CatalogWriteStatus.CategoryNotFound);
                }

                oldValues["categoryId"] = entity.CategoryId;
                newValues["categoryId"] = categoryId;
                entity.MoveToCategory(categoryId);
            }

            if (change.Name is { IsSet: true, Value: { } name } && name != entity.Name)
            {
                oldValues["name"] = entity.Name;
                newValues["name"] = name;
                entity.Rename(name);
            }

            if (change.Brand.IsSet && change.Brand.Value != entity.Brand)
            {
                oldValues["brand"] = entity.Brand;
                newValues["brand"] = change.Brand.Value;
                entity.SetBrand(change.Brand.Value);
            }

            if (change.Description.IsSet && change.Description.Value != entity.Description)
            {
                oldValues["description"] = AdminAudit.Clip(entity.Description);
                newValues["description"] = AdminAudit.Clip(change.Description.Value);
                entity.SetDescription(change.Description.Value);
            }

            if (change.Specifications.IsSet)
            {
                var normalized = Product.NormalizeSpecifications(change.Specifications.Value);
                if (!SameMap(normalized, entity.Specifications))
                {
                    oldValues["specificationCount"] = entity.Specifications?.Count ?? 0;
                    newValues["specificationCount"] = normalized?.Count ?? 0;
                    entity.SetSpecifications(normalized);
                }
            }

            if (change.IsActive.IsSet && change.IsActive.Value != entity.IsActive)
            {
                oldValues["isActive"] = entity.IsActive;
                newValues["isActive"] = change.IsActive.Value;
                entity.SetActive(change.IsActive.Value);
            }

            if (newValues.Count > 0)
            {
                entity.BumpVersion();
                AdminAudit.Add(db, clock, actorUserId, "product.update", AdminAuditRules.TargetProduct, id, new { old = oldValues, @new = newValues });
                await db.SaveChangesAsync(cancellationToken);
            }

            return Ok(await LoadDetailAsync(id, cancellationToken));
        }, cancellationToken);

    public Task<CatalogWriteResult<AdminProductDetailDto>> ReplaceImagesAsync(Guid actorUserId, Guid id, ImageReplacement replacement, CancellationToken cancellationToken) =>
        RunAsync<AdminProductDetailDto>(false, async transaction =>
        {
            var entity = await LockProductAsync(id, cancellationToken);
            if (entity is null)
            {
                return Fail<AdminProductDetailDto>(CatalogWriteStatus.ProductNotFound);
            }

            if (entity.Version != replacement.Version)
            {
                return Conflict<AdminProductDetailDto>(entity.Version);
            }

            // Onay listesi: yalnız tam eşleşen yerel varlıklar. Doğrulayıcı biçimi denetledi; burada üyelik denetlenir.
            var credits = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var image in replacement.Images)
            {
                if (!ApprovedProductImages.TryGet(image.StorageKey, out var asset))
                {
                    return Fail<AdminProductDetailDto>(CatalogWriteStatus.ImageNotApproved);
                }

                credits[image.StorageKey] = asset.CreditKey;
            }

            var current = await db.ProductImages.AsNoTracking()
                .Where(i => i.ProductId == id)
                .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
                .Select(i => new ImageInput(i.StorageKey, i.AltText, i.IsPrimary))
                .ToListAsync(cancellationToken);
            if (current.SequenceEqual(replacement.Images))
            {
                return Ok(await LoadDetailAsync(id, cancellationToken));
            }

            await db.ProductImages.Where(i => i.ProductId == id).ExecuteDeleteAsync(cancellationToken);
            var sortOrder = 0;
            foreach (var image in replacement.Images)
            {
                db.ProductImages.Add(entity.AddImage(image.StorageKey, image.AltText, sortOrder++, image.IsPrimary, credits[image.StorageKey]));
            }

            entity.BumpVersion();
            AdminAudit.Add(db, clock, actorUserId, "product.images_replace", AdminAuditRules.TargetProduct, id, new
            {
                old = current.Select(i => i.StorageKey).ToList(),
                @new = replacement.Images.Select(i => i.StorageKey).ToList(),
                primary = replacement.Images.FirstOrDefault(i => i.IsPrimary)?.StorageKey,
            });
            await db.SaveChangesAsync(cancellationToken);
            return Ok(await LoadDetailAsync(id, cancellationToken));
        }, cancellationToken);

    // ---------- Varyant ----------

    public Task<CatalogWriteResult<AdminVariantDto>> CreateVariantAsync(Guid actorUserId, Guid productId, NewVariant variant, CancellationToken cancellationToken) =>
        RunAsync<AdminVariantDto>(false, async transaction =>
        {
            // Ürün satırı FOR SHARE (kilit sırası: ürün → varyant). Yeni varyant hiçbir sepette olamaz; sipariş akışıyla çatışmaz.
            var product = (await db.Products
                .FromSqlInterpolated($"SELECT * FROM products WHERE id = {productId} FOR SHARE")
                .AsNoTracking()
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (product is null)
            {
                return Fail<AdminVariantDto>(CatalogWriteStatus.ProductNotFound);
            }

            var entity = product.AddVariant(variant.Sku, variant.DisplayName, Money.FromAdminPriceMinor(variant.PriceMinor), variant.Options);
            entity.SetActive(variant.IsActive);
            db.ProductVariants.Add(entity);
            AdminAudit.Add(db, clock, actorUserId, "variant.create", AdminAuditRules.TargetVariant, entity.Id, new
            {
                productId,
                sku = entity.Sku,
                displayName = entity.DisplayName,
                priceMinor = variant.PriceMinor,
                options = entity.Options,
                isActive = entity.IsActive,
            });
            await db.SaveChangesAsync(cancellationToken);
            return Ok(AdminCatalogQueries.ToDto(entity));
        }, cancellationToken);

    public Task<CatalogWriteResult<AdminVariantDto>> UpdateVariantAsync(Guid actorUserId, Guid id, VariantChange change, CancellationToken cancellationToken) =>
        RunAsync<AdminVariantDto>(false, async transaction =>
        {
            var entity = (await db.ProductVariants
                .FromSqlInterpolated($"SELECT * FROM product_variants WHERE id = {id} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (entity is null)
            {
                return Fail<AdminVariantDto>(CatalogWriteStatus.VariantNotFound);
            }

            if (entity.Version != change.Version)
            {
                return Conflict<AdminVariantDto>(entity.Version);
            }

            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();
            if (change.DisplayName is { IsSet: true, Value: { } displayName } && displayName != entity.DisplayName)
            {
                oldValues["displayName"] = entity.DisplayName;
                newValues["displayName"] = displayName;
                entity.Rename(displayName);
            }

            if (change.PriceMinor.IsSet)
            {
                var price = Money.FromAdminPriceMinor(change.PriceMinor.Value);
                if (price != entity.Price)
                {
                    oldValues["priceMinor"] = Money.ToMinor(entity.Price);
                    newValues["priceMinor"] = change.PriceMinor.Value;
                    entity.ChangePrice(price);
                }
            }

            if (change.IsActive.IsSet && change.IsActive.Value != entity.IsActive)
            {
                oldValues["isActive"] = entity.IsActive;
                newValues["isActive"] = change.IsActive.Value;
                entity.SetActive(change.IsActive.Value);
            }

            if (newValues.Count > 0)
            {
                entity.BumpVersion();
                AdminAudit.Add(db, clock, actorUserId, "variant.update", AdminAuditRules.TargetVariant, id, new { old = oldValues, @new = newValues });
                await db.SaveChangesAsync(cancellationToken);
            }

            return Ok(AdminCatalogQueries.ToDto(entity));
        }, cancellationToken);

    // ---------- Ortak akış ----------

    private async Task<CatalogWriteResult<T>> RunAsync<T>(
        bool lockCategoryTree,
        Func<IDbContextTransaction, Task<CatalogWriteResult<T>>> body,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            if (lockCategoryTree)
            {
                await TransactionalSql.ExecuteAsync(db, transaction, CategoryTreeLockSql, cancellationToken);
            }

            var result = await body(transaction);
            if (result.IsOk)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (MapFailure(exception) is { } status)
        {
            // Dispose rollback yapar; hiçbir şey kalıcı olmadı.
            return Fail<T>(status);
        }
    }

    /// <summary>Beklenen veritabanı hatalarını belirli durumlara eşler; tanınmayanlar null döner (istisna yukarı akar, 500).</summary>
    internal static CatalogWriteStatus? MapFailure(Exception exception)
    {
        if (TransactionalSql.IsLockTimeout(exception))
        {
            return CatalogWriteStatus.Busy;
        }

        var postgres = TransactionalSql.PostgresOf(exception);
        return (postgres?.SqlState, postgres?.ConstraintName) switch
        {
            (PostgresErrorCodes.UniqueViolation, "ux_categories_slug") => CatalogWriteStatus.CategorySlugTaken,
            (PostgresErrorCodes.UniqueViolation, "ux_products_slug") => CatalogWriteStatus.ProductSlugTaken,
            (PostgresErrorCodes.UniqueViolation, "ux_product_variants_sku") => CatalogWriteStatus.SkuTaken,
            (PostgresErrorCodes.UniqueViolation, "ux_product_variants_product_id_option_signature") => CatalogWriteStatus.VariantOptionsTaken,
            (PostgresErrorCodes.CheckViolation, "ck_categories_no_cycle") => CatalogWriteStatus.CategoryCycle,
            (PostgresErrorCodes.CheckViolation, "ck_categories_max_depth") => CatalogWriteStatus.CategoryTooDeep,
            _ => null,
        };
    }

    private async Task<Product?> LockProductAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.Products
            .FromSqlInterpolated($"SELECT * FROM products WHERE id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();

    /// <summary>Satırın var olup olmadığını <c>FOR SHARE</c> ile kilitleyerek sorar (tablo adı sabit listeden; kullanıcı girdisi değil).</summary>
    private async Task<bool> ExistsSharedAsync(IDbContextTransaction transaction, string table, Guid id, CancellationToken cancellationToken)
    {
        var sql = table switch
        {
            "categories" => "SELECT id FROM categories WHERE id = @id FOR SHARE",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        return await TransactionalSql.ScalarAsync(db, transaction, sql, cancellationToken, ("id", id)) is Guid;
    }

    private async Task<AdminProductDetailDto> LoadDetailAsync(Guid id, CancellationToken cancellationToken) =>
        await queries.GetProductAsync(id, cancellationToken) ?? throw new InvalidOperationException("Kalıcı ürün okunamadı.");

    private static bool SameMap(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || left.Count == 0)
        {
            return right is null || right.Count == 0;
        }

        return right is not null && left.Count == right.Count && left.All(kv => right.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }

    private static AdminCategoryDto ToDto(Category category, int productCount) => new(
        category.Id,
        category.Name,
        category.Slug,
        category.ParentId,
        category.SortOrder,
        category.IsActive,
        category.Version,
        productCount,
        category.UpdatedAt);

    private static CatalogWriteResult<T> Ok<T>(T value) where T : class => new(CatalogWriteStatus.Ok, value);

    private static CatalogWriteResult<T> Fail<T>(CatalogWriteStatus status) where T : class => new(status);

    private static CatalogWriteResult<T> Conflict<T>(int currentVersion) where T : class =>
        new(CatalogWriteStatus.VersionConflict, null, currentVersion);
}
