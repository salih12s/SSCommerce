using SSCommerce.Application.Common;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Application.Addresses;

// Teslimat adresi sözleşmesi (docs/CONTRACT_PHASES_5_6.md "Adresler", D-052). Adres KİŞİSEL VERİDİR: loga, URL'ye ve hata iletisine yazılmaz.

public sealed record AddressDto(
    Guid Id,
    string Label,
    string RecipientName,
    string Phone,
    string Province,
    string District,
    string StreetAddress,
    string? PostalCode,
    bool IsDefault);

/// <summary>POST ve PUT için ham gövde. <c>isDefault</c>, <c>userId</c> gibi alanlar yok sayılır (varsayılanı yalnız sunucu ve /default ucu belirler).</summary>
public sealed record AddressRequest(
    string? Label,
    string? RecipientName,
    string? Phone,
    string? Province,
    string? District,
    string? StreetAddress,
    string? PostalCode);

/// <summary>Doğrulanmış, normalize adres (telefon <c>5XXXXXXXXX</c>, metinler kırpılmış).</summary>
public sealed record AddressInput(
    string Label,
    string RecipientName,
    string Phone,
    string Province,
    string District,
    string StreetAddress,
    string? PostalCode);

public enum AddressOperationStatus
{
    Ok,
    NotFound,
    LimitReached,

    /// <summary>Kilit zaman aşımı: hiçbir şey yazılmadı (503).</summary>
    Busy,
}

public sealed record AddressOperationResult(AddressOperationStatus Status, AddressDto? Address = null);

public interface IAddressService
{
    /// <summary>Varsayılan önce.</summary>
    Task<IReadOnlyList<AddressDto>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Kullanıcı başına sıraya sokulur; limit kilit içinde denetlenir; ilk adres otomatik varsayılan olur.</summary>
    Task<AddressOperationResult> CreateAsync(Guid userId, AddressInput input, CancellationToken cancellationToken);

    Task<AddressOperationResult> UpdateAsync(Guid userId, Guid id, AddressInput input, CancellationToken cancellationToken);

    Task<AddressOperationResult> SetDefaultAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Varsayılan silinirse en son güncellenen adres varsayılan olur.</summary>
    Task<AddressOperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}

/// <summary>Adres gövdesinin saf doğrulaması. Alan kuralları <see cref="AddressRules"/>'tadır; hata iletileri girilen değeri içermez.</summary>
public static class AddressRequestValidator
{
    public static RequestValidation<AddressInput> Validate(AddressRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var label = Text(request.Label, AddressRules.LabelMaxLength, "label", "Adres adı", errors);
        var recipient = Text(request.RecipientName, AddressRules.RecipientNameMaxLength, "recipientName", "Alıcı adı", errors);
        var phone = Check(() => AddressRules.NormalizePhone(request.Phone), "phone", errors);
        var province = Check(() => AddressRules.RequireProvince(request.Province), "province", errors);
        var district = Text(request.District, AddressRules.DistrictMaxLength, "district", "İlçe", errors);
        var street = Text(request.StreetAddress, AddressRules.StreetAddressMaxLength, "streetAddress", "Açık adres", errors);
        var postal = Check(() => AddressRules.OptionalPostalCode(request.PostalCode), "postalCode", errors);

        return errors.Count > 0
            ? RequestValidation<AddressInput>.Invalid(errors)
            : RequestValidation<AddressInput>.Valid(new AddressInput(label!, recipient!, phone!, province!, district!, street!, postal));
    }

    private static string? Text(string? value, int maxLength, string field, string what, Dictionary<string, string[]> errors)
    {
        // Ham değer önce ortak kuralla denetlenir (Trim, U+2028/U+2029'u atmadan); Domain kuralı ikinci savunma hattıdır.
        if (TextRules.ContainsUnsafeCharacter(value))
        {
            errors[field] = [$"{what}: kontrol, biçim ya da satır ayırıcı karakter içeremez."];
            return null;
        }

        return Check(() => AddressRules.RequireText(value, maxLength, what), field, errors);
    }

    private static string? Check(Func<string?> rule, string field, Dictionary<string, string[]> errors)
    {
        try
        {
            return rule();
        }
        catch (AddressRuleException exception)
        {
            errors[field] = [exception.Message];
            return null;
        }
    }
}
