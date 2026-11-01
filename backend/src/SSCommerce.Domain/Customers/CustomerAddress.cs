using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Customers;

/// <summary>
/// Kullanıcının teslimat adresi (D-052). Telefon <c>5XXXXXXXXX</c> olarak normalize saklanır. Varsayılan adres kullanıcı başına tektir
/// (kısmi benzersiz indeks <c>ux_customer_addresses_user_id_default</c>); hangi adresin varsayılan olacağına uygulama karar verir,
/// değişiklikler kullanıcı başına sıraya sokulur (pg_advisory_xact_lock). Kişisel veridir: loga ve URL'ye yazılmaz.
/// </summary>
public sealed class CustomerAddress : AuditedEntity
{
    private CustomerAddress()
    {
        Label = string.Empty;
        RecipientName = string.Empty;
        Phone = string.Empty;
        Province = string.Empty;
        District = string.Empty;
        StreetAddress = string.Empty;
    }

    public CustomerAddress(
        Guid userId,
        string label,
        string recipientName,
        string phone,
        string province,
        string district,
        string streetAddress,
        string? postalCode,
        bool isDefault = false)
        : this()
    {
        if (userId == Guid.Empty)
        {
            throw new AddressRuleException("Kullanıcı kimliği zorunlu.");
        }

        UserId = userId;
        IsDefault = isDefault;
        Update(label, recipientName, phone, province, district, streetAddress, postalCode);
    }

    public Guid UserId { get; private set; }

    public string Label { get; private set; }

    public string RecipientName { get; private set; }

    /// <summary>Normalize: tam 10 hane, <c>5</c> ile başlar.</summary>
    public string Phone { get; private set; }

    public string Province { get; private set; }

    public string District { get; private set; }

    public string StreetAddress { get; private set; }

    public string? PostalCode { get; private set; }

    public bool IsDefault { get; private set; }

    /// <summary>Tam güncelleme (varsayılan bayrağı hariç). Önce hepsi doğrulanır; biri geçersizse hiçbir alan değişmez.</summary>
    public void Update(
        string label,
        string recipientName,
        string phone,
        string province,
        string district,
        string streetAddress,
        string? postalCode)
    {
        var validLabel = AddressRules.RequireText(label, AddressRules.LabelMaxLength, "Adres adı");
        var validRecipient = AddressRules.RequireText(recipientName, AddressRules.RecipientNameMaxLength, "Alıcı adı");
        var validPhone = AddressRules.NormalizePhone(phone);
        var validProvince = AddressRules.RequireProvince(province);
        var validDistrict = AddressRules.RequireText(district, AddressRules.DistrictMaxLength, "İlçe");
        var validStreet = AddressRules.RequireText(streetAddress, AddressRules.StreetAddressMaxLength, "Açık adres");
        var validPostalCode = AddressRules.OptionalPostalCode(postalCode);

        Label = validLabel;
        RecipientName = validRecipient;
        Phone = validPhone;
        Province = validProvince;
        District = validDistrict;
        StreetAddress = validStreet;
        PostalCode = validPostalCode;
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;
}
