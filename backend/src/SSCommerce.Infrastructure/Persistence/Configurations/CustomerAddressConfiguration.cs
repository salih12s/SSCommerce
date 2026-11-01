using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Customers;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> builder)
    {
        builder.ToTable("customer_addresses", t =>
        {
            t.HasCheckConstraint("ck_customer_addresses_phone_format", $"phone ~ '{AddressRules.PhonePattern}'");
            t.HasCheckConstraint(
                "ck_customer_addresses_postal_code_format",
                $"postal_code IS NULL OR postal_code ~ '{AddressRules.PostalCodePattern}'");
            t.HasCheckConstraint("ck_customer_addresses_label_not_blank", "btrim(label) <> ''");
            t.HasCheckConstraint("ck_customer_addresses_recipient_name_not_blank", "btrim(recipient_name) <> ''");
            t.HasCheckConstraint("ck_customer_addresses_province_not_blank", "btrim(province) <> ''");
            t.HasCheckConstraint("ck_customer_addresses_district_not_blank", "btrim(district) <> ''");
            t.HasCheckConstraint("ck_customer_addresses_street_address_not_blank", "btrim(street_address) <> ''");
        });

        builder.HasKey(a => a.Id).HasName("pk_customer_addresses");
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.Label).HasColumnName("label").HasMaxLength(AddressRules.LabelMaxLength).IsRequired();
        builder.Property(a => a.RecipientName).HasColumnName("recipient_name").HasMaxLength(AddressRules.RecipientNameMaxLength).IsRequired();
        builder.Property(a => a.Phone).HasColumnName("phone").HasColumnType($"char({AddressRules.PhoneLength})").IsRequired();
        builder.Property(a => a.Province).HasColumnName("province").HasMaxLength(AddressRules.ProvinceMaxLength).IsRequired();
        builder.Property(a => a.District).HasColumnName("district").HasMaxLength(AddressRules.DistrictMaxLength).IsRequired();
        builder.Property(a => a.StreetAddress).HasColumnName("street_address").HasMaxLength(AddressRules.StreetAddressMaxLength).IsRequired();
        builder.Property(a => a.PostalCode).HasColumnName("postal_code").HasColumnType($"char({AddressRules.PostalCodeLength})");
        builder.Property(a => a.IsDefault).HasColumnName("is_default");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // Aynı sütunda iki indeks (biri kısmi benzersiz) olduğundan model düzeyinde adlandırılmış indeks gerekir.
        builder.HasIndex(a => a.UserId, "ix_customer_addresses_user_id").HasDatabaseName("ix_customer_addresses_user_id");
        // Kullanıcı başına en çok bir varsayılan adres (kısmi benzersiz indeks, D-052).
        builder.HasIndex(a => a.UserId)
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ux_customer_addresses_user_id_default");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .HasConstraintName("fk_customer_addresses_user")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
