using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>order_status_history</c>: yalnız eklenir (migration'daki trigger, D-064). İlk satır <c>null → placed</c>'dir. Aktör kimliği, türü ve
/// neden müşteri yanıtlarına çıkmaz.
/// </summary>
internal sealed class OrderStatusHistoryEntryConfiguration : IEntityTypeConfiguration<OrderStatusHistoryEntry>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistoryEntry> builder)
    {
        var statuses = string.Join(", ", OrderStatuses.All.Select(s => $"'{s}'"));
        var actorKinds = string.Join(", ", OrderActorKinds.All.Select(k => $"'{k}'"));

        builder.ToTable("order_status_history", t =>
        {
            t.HasCheckConstraint("ck_order_status_history_from_status", $"from_status IS NULL OR from_status IN ({statuses})");
            t.HasCheckConstraint("ck_order_status_history_to_status", $"to_status IN ({statuses})");
            t.HasCheckConstraint("ck_order_status_history_actor_kind", $"actor_kind IN ({actorKinds})");
            // Başlangıç satırı (from NULL) yalnız 'placed'e gider ve 'placed'e yalnız başlangıç satırı gider; aynı duruma geçiş kaydı yoktur.
            t.HasCheckConstraint("ck_order_status_history_first_row", $"(from_status IS NULL) = (to_status = '{OrderStatuses.Placed}')");
            t.HasCheckConstraint("ck_order_status_history_status_changed", "from_status IS NULL OR from_status <> to_status");
            t.HasCheckConstraint("ck_order_status_history_reason_not_blank", "reason IS NULL OR btrim(reason) <> ''");
        });

        builder.HasKey(h => h.Id).HasName("pk_order_status_history");
        builder.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(h => h.OrderId).HasColumnName("order_id");
        builder.Property(h => h.FromStatus)
            .HasColumnName("from_status")
            .HasMaxLength(32)
            .HasConversion(new ValueConverter<OrderStatus?, string?>(
                status => status == null ? null : OrderStatuses.ToWire(status.Value),
                wire => wire == null ? null : OrderStatuses.FromWire(wire)));
        builder.Property(h => h.ToStatus).HasColumnName("to_status").HasMaxLength(32).HasConversion(OrderConfiguration.StatusConverter).IsRequired();
        builder.Property(h => h.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(h => h.ActorKind)
            .HasColumnName("actor_kind")
            .HasMaxLength(16)
            .HasConversion(new ValueConverter<OrderActorKind, string>(
                kind => OrderActorKinds.ToWire(kind),
                wire => OrderActorKinds.FromWire(wire)))
            .IsRequired();
        builder.Property(h => h.Reason).HasColumnName("reason").HasMaxLength(OrderRules.ReasonMaxLength);
        builder.Property(h => h.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");

        // order_id önde olduğu için order FK'si ayrıca indekslenmez; sipariş detayı geçmişi bu sırayla okur.
        builder.HasIndex(h => new { h.OrderId, h.OccurredAt, h.Id }).HasDatabaseName("ix_order_status_history_order_id_occurred_at_id");
        builder.HasIndex(h => h.ActorUserId).HasDatabaseName("ix_order_status_history_actor_user_id");
        // Faz 8 panosu: son sipariş olayları (en yeni önce, LIMIT).
        builder.HasIndex(h => new { h.OccurredAt, h.Id }).IsDescending(true, true).HasDatabaseName("ix_order_status_history_occurred_at_id");

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(h => h.OrderId)
            .HasConstraintName("fk_order_status_history_order")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(h => h.ActorUserId)
            .HasConstraintName("fk_order_status_history_actor_user")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
