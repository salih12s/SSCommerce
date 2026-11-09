using System.Data;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Addresses;
using SSCommerce.Domain.Customers;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Addresses;

/// <summary>
/// Adres servisi (F6.3, D-052). Her YAZMA kullanıcı başına sıraya sokulur: <c>pg_advisory_xact_lock(int4, int4)</c> (iki argümanlı biçim).
/// Anahtar = (sabit ad alanı, <c>hashtext(user_id)</c>). Stok anahtarları TEK argümanlı <c>pg_advisory_xact_lock(bigint)</c> kullanır; PostgreSQL'de
/// iki farklı kilit alanıdır, bu yüzden çakışma/gereksiz bekleme olmaz. Limit denetimi, ilk-adres-varsayılan kararı ve varsayılan taşıma
/// kilit İÇİNDE yapılır; kısmi unique indeks (<c>ux_customer_addresses_user_id_default</c>) son savunma hattıdır: varsayılanı taşırken ESKİSİ
/// önce temizlenir, sonra yenisi işaretlenir (aynı transaction). Her sorgu <c>user_id</c> ile kapsamlıdır; başkasının adresi "yok"tur.
/// </summary>
internal sealed class AddressService(SSCommerceDbContext db, TimeProvider clock) : IAddressService
{
    /// <summary>Adres kilit ad alanı (ASCII "ADDR"). Sabit; stok anahtarlarının tek argümanlı kilit alanından ayrıdır.</summary>
    private const int LockNamespace = 0x41444452;

    public async Task<IReadOnlyList<AddressDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.CustomerAddresses
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.UpdatedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public Task<AddressOperationResult> CreateAsync(Guid userId, AddressInput input, CancellationToken cancellationToken) =>
        WriteAsync(userId, async ct =>
        {
            var count = await db.CustomerAddresses.CountAsync(a => a.UserId == userId, ct);
            if (count >= AddressRules.MaxAddressesPerUser)
            {
                return new AddressOperationResult(AddressOperationStatus.LimitReached);
            }

            var address = new CustomerAddress(
                userId,
                input.Label,
                input.RecipientName,
                input.Phone,
                input.Province,
                input.District,
                input.StreetAddress,
                input.PostalCode,
                isDefault: count == 0); // ilk adres otomatik varsayılan
            db.CustomerAddresses.Add(address);
            await db.SaveChangesAsync(ct);
            return new AddressOperationResult(AddressOperationStatus.Ok, ToDto(address));
        }, cancellationToken);

    public Task<AddressOperationResult> UpdateAsync(Guid userId, Guid id, AddressInput input, CancellationToken cancellationToken) =>
        WriteAsync(userId, async ct =>
        {
            var address = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
            if (address is null)
            {
                return new AddressOperationResult(AddressOperationStatus.NotFound);
            }

            address.Update(input.Label, input.RecipientName, input.Phone, input.Province, input.District, input.StreetAddress, input.PostalCode);
            await db.SaveChangesAsync(ct);
            return new AddressOperationResult(AddressOperationStatus.Ok, ToDto(address));
        }, cancellationToken);

    public Task<AddressOperationResult> SetDefaultAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        WriteAsync(userId, async ct =>
        {
            var exists = await db.CustomerAddresses.AnyAsync(a => a.Id == id && a.UserId == userId, ct);
            if (!exists)
            {
                return new AddressOperationResult(AddressOperationStatus.NotFound);
            }

            var now = clock.GetUtcNow();
            // Önce ESKİ varsayılan temizlenir, sonra yenisi işaretlenir: kısmi unique indeks hiçbir anda iki varsayılan görmez.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE customer_addresses SET is_default = false, updated_at = {now} WHERE user_id = {userId} AND is_default AND id <> {id}", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE customer_addresses SET is_default = true, updated_at = {now} WHERE user_id = {userId} AND id = {id} AND NOT is_default", ct);
            var address = await db.CustomerAddresses.AsNoTracking().SingleAsync(a => a.Id == id && a.UserId == userId, ct);
            return new AddressOperationResult(AddressOperationStatus.Ok, ToDto(address));
        }, cancellationToken);

    public Task<AddressOperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        WriteAsync(userId, async ct =>
        {
            var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM customer_addresses WHERE id = {id} AND user_id = {userId}", ct);
            if (deleted == 0)
            {
                return new AddressOperationResult(AddressOperationStatus.NotFound);
            }

            var now = clock.GetUtcNow();
            // Varsayılan silindiyse (artık hiç varsayılan yok) en son güncellenen kalan adres varsayılan olur. Varsayılan varsa dokunulmaz.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE customer_addresses SET is_default = true, updated_at = {now}
                WHERE id = (SELECT id FROM customer_addresses WHERE user_id = {userId} ORDER BY updated_at DESC, id DESC LIMIT 1)
                  AND NOT EXISTS (SELECT 1 FROM customer_addresses WHERE user_id = {userId} AND is_default)
                """, ct);
            return new AddressOperationResult(AddressOperationStatus.Ok);
        }, cancellationToken);

    private async Task<AddressOperationResult> WriteAsync(
        Guid userId,
        Func<CancellationToken, Task<AddressOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
            // İki argümanlı biçim: (int4, int4). Kullanıcı kimliği metne çevrilip hashtext ile int4'e indirgenir; parametrik.
            await TransactionalSql.ExecuteAsync(
                db,
                transaction,
                "SELECT pg_advisory_xact_lock(@ns, hashtext(@user))",
                cancellationToken,
                ("ns", LockNamespace),
                ("user", userId.ToString("D")));

            var result = await operation(cancellationToken);
            if (result.Status == AddressOperationStatus.Ok)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new AddressOperationResult(AddressOperationStatus.Busy);
        }
    }

    internal static AddressDto ToDto(CustomerAddress a) =>
        new(a.Id, a.Label, a.RecipientName, a.Phone, a.Province, a.District, a.StreetAddress, a.PostalCode, a.IsDefault);
}
