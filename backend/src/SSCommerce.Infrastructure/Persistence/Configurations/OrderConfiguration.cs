using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SSCommerce.Domain.Customers;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>orders</c> (D-059..D-064, docs/CONTRACT_PHASE_7.md). Satır değişmezdir: yalnız <c>status</c> ve <c>updated_at</c> değişebilir ve bunu
/// migration'daki <c>trg_orders_restrict_update</c> trigger'ı zorlar. Teslimat adresi snapshot sütunlarındadır, adrese FK YOKTUR.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>Durum ↔ DB metni dönüşümü; durum geçmişi yapılandırması da kullanır.</summary>
    internal static readonly ValueConverter<OrderStatus, string> StatusConverter =
        new(status => OrderStatuses.ToWire(status), wire => OrderStatuses.FromWire(wire));

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        var statuses = string.Join(", ", OrderStatuses.All.Select(s => $"'{s}'"));

        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_idempotency_key_format", $"idempotency_key ~ '{OrderRules.IdempotencyKeyPattern}'");
            t.HasCheckConstraint("ck_orders_request_fingerprint_format", $"request_fingerprint ~ '{OrderRules.RequestFingerprintPattern}'");
            t.HasCheckConstraint("ck_orders_status", $"status IN ({statuses})");
            t.HasCheckConstraint("ck_orders_delivery_label_not_blank", "btrim(delivery_label) <> ''");
            t.HasCheckConstraint("ck_orders_delivery_recipient_name_not_blank", "btrim(delivery_recipient_name) <> ''");
            t.HasCheckConstraint("ck_orders_delivery_phone_format", $"delivery_phone ~ '{AddressRules.PhonePattern}'");
            t.HasCheckConstraint("ck_orders_delivery_province_not_blank", "btrim(delivery_province) <> ''");
            t.HasCheckConstraint("ck_orders_delivery_district_not_blank", "btrim(delivery_district) <> ''");
            t.HasCheckConstraint("ck_orders_delivery_street_address_not_blank", "btrim(delivery_street_address) <> ''");
            t.HasCheckConstraint(
                "ck_orders_delivery_postal_code_format",
                $"delivery_postal_code IS NULL OR delivery_postal_code ~ '{AddressRules.PostalCodePattern}'");
            t.HasCheckConstraint("ck_orders_currency", $"currency = '{OrderRules.Currency}'");
            t.HasCheckConstraint("ck_orders_item_subtotal_non_negative", "item_subtotal >= 0");
            t.HasCheckConstraint("ck_orders_shipping_status", $"shipping_status = '{OrderRules.ShippingNotCalculated}'");
            // Faz 7: kargo ve genel toplam uydurulmaz (D-061). Faz 8+ kargo gelirse bu iki CHECK yeni migration'la gevşetilir.
            t.HasCheckConstraint("ck_orders_shipping_amount_null", "shipping_amount IS NULL");
            t.HasCheckConstraint("ck_orders_grand_total_null", "grand_total IS NULL");
        });

        builder.HasKey(o => o.Id).HasName("pk_orders");
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        // Veritabanı identity'si (100001'den). ALWAYS: uygulama ya da elle INSERT sıra numarası veremez. EF ekleme sonrası değeri geri okur.
        builder.Property(o => o.OrderNumber)
            .HasColumnName("order_number")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd()
            .UseIdentityAlwaysColumn()
            .HasIdentityOptions(startValue: OrderRules.InitialOrderNumber);
        builder.Property(o => o.UserId).HasColumnName("user_id");
        builder.Property(o => o.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(OrderRules.IdempotencyKeyMaxLength).IsRequired();
        builder.Property(o => o.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasColumnType($"char({OrderRules.RequestFingerprintLength})")
            .IsRequired();
        builder.Property(o => o.Status).HasColumnName("status").HasMaxLength(32).HasConversion(StatusConverter).IsRequired();
        builder.Property(o => o.DeliveryLabel).HasColumnName("delivery_label").HasMaxLength(AddressRules.LabelMaxLength).IsRequired();
        builder.Property(o => o.DeliveryRecipientName)
            .HasColumnName("delivery_recipient_name")
            .HasMaxLength(AddressRules.RecipientNameMaxLength)
            .IsRequired();
        builder.Property(o => o.DeliveryPhone).HasColumnName("delivery_phone").HasColumnType($"char({AddressRules.PhoneLength})").IsRequired();
        builder.Property(o => o.DeliveryProvince).HasColumnName("delivery_province").HasMaxLength(AddressRules.ProvinceMaxLength).IsRequired();
        builder.Property(o => o.DeliveryDistrict).HasColumnName("delivery_district").HasMaxLength(AddressRules.DistrictMaxLength).IsRequired();
        builder.Property(o => o.DeliveryStreetAddress)
            .HasColumnName("delivery_street_address")
            .HasMaxLength(AddressRules.StreetAddressMaxLength)
            .IsRequired();
        builder.Property(o => o.DeliveryPostalCode).HasColumnName("delivery_postal_code").HasColumnType($"char({AddressRules.PostalCodeLength})");
        builder.Property(o => o.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        builder.Property(o => o.ItemSubtotal).HasColumnName("item_subtotal").HasColumnType("numeric(18,2)");
        builder.Property(o => o.ShippingStatus).HasColumnName("shipping_status").HasMaxLength(32).IsRequired();
        builder.Property(o => o.ShippingAmount).HasColumnName("shipping_amount").HasColumnType("numeric(18,2)");
        builder.Property(o => o.GrandTotal).HasColumnName("grand_total").HasColumnType("numeric(18,2)");
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        // Hesaplanan özellikler sütun değildir.
        builder.Ignore(o => o.DisplayNumber);
        builder.Ignore(o => o.CanCustomerCancel);

        builder.HasIndex(o => o.OrderNumber).IsUnique().HasDatabaseName("ux_orders_order_number");
        // Kullanıcı başına anahtar tekilliği; user_id FK'sinin indeksi değildir ama anahtar araması bu indeksle yapılır (her zaman user_id ile).
        builder.HasIndex(o => new { o.UserId, o.IdempotencyKey }).IsUnique().HasDatabaseName("ux_orders_user_id_idempotency_key");
        // Müşteri geçmişi (en yeni önce) ve user_id FK'si için.
        builder.HasIndex(o => new { o.UserId, o.CreatedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_user_id_created_at_id");
        // Personel listesi: duruma göre filtre, en yeni önce.
        builder.HasIndex(o => new { o.Status, o.CreatedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_status_created_at_id");

        // Faz 8: açık siparişler (placed, processing). Durum listesi SQL SABİTİDİR; sorgular da sabit yazmalı ki kısmi indeks seçilsin.
        // Yaşlanan açık sipariş sayımı / en eski 10 açık sipariş (EXPLAIN: Seq/Bitmap ~2 ms -> Index Scan ~0.03 ms, 3000 açık satırda).
        builder.HasIndex(o => new { o.CreatedAt, o.Id })
            .HasFilter("status IN ('placed', 'processing')")
            .HasDatabaseName("ix_orders_open_created_at");
        // Kullanıcı başına açık sipariş sayımı (yerleştirme yolunda sepet kilidi altında). EXPLAIN: 100k siparişte 0,67 ms/53 tampon -> 0,06 ms/5.
        builder.HasIndex(o => o.UserId)
            .HasFilter("status IN ('placed', 'processing')")
            .HasDatabaseName("ix_orders_open_user");

        // RESTRICT: siparişi olan kullanıcı silinemez (D-064); kişisel veri silme akışı sonraki sürüme kalır.
        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .HasConstraintName("fk_orders_user")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
