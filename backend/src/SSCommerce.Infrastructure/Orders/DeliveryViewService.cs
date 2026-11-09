using System.Data;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Administration;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Admin teslimat görünümü (Faz 8, D-071). Tek READ COMMITTED transaction: teslimat kopyası okunur ve <c>order.delivery_view</c> denetim satırı AYNI
/// transaction'da yazılır; okuma yapılıp iz bırakılmamış bir durum yoktur (denetim yazılamazsa yanıt da dönmez). Denetim <c>changes</c> kişisel veri
/// (alıcı, telefon, adres, e-posta) TAŞIMAZ; yalnız kimin hangi siparişe baktığı kayda geçer. Sipariş yoksa hiçbir satır yazılmaz.
/// Yanıt kişisel veridir: loga, URL'ye ve listelere girmez; HTTP katmanı <c>no-store</c> ekler.
/// </summary>
internal sealed class DeliveryViewService(SSCommerceDbContext db, TimeProvider clock) : IDeliveryViewService
{
    public async Task<DeliveryViewResult> ViewAsync(Guid actorUserId, Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);

            var delivery = await db.Orders.AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => new DeliveryViewDto(
                    o.DeliveryRecipientName,
                    o.DeliveryPhone,
                    o.DeliveryProvince,
                    o.DeliveryDistrict,
                    o.DeliveryStreetAddress,
                    o.DeliveryPostalCode,
                    o.DeliveryLabel))
                .FirstOrDefaultAsync(cancellationToken);
            if (delivery is null)
            {
                return new DeliveryViewResult(DeliveryViewStatus.NotFound);
            }

            AdminAudit.Add(db, clock, actorUserId, "order.delivery_view", AdminAuditRules.TargetOrder, orderId, new { view = "delivery" });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DeliveryViewResult(DeliveryViewStatus.Viewed, delivery);
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new DeliveryViewResult(DeliveryViewStatus.Busy);
        }
    }
}
