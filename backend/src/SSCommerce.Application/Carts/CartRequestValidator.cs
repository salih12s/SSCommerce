using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Cart;

namespace SSCommerce.Application.Carts;

/// <summary>Sepet gövdelerinin saf doğrulaması. Tüm alanlar tek geçişte doğrulanır.</summary>
public static class CartRequestValidator
{
    public static RequestValidation<AddCartItemCommand> ValidateAdd(AddCartItemRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.VariantId is not { } variantId || variantId == Guid.Empty)
        {
            errors["variantId"] = ["variantId zorunludur."];
        }

        var quantity = ValidateQuantity(request.Quantity, errors);
        return errors.Count > 0
            ? RequestValidation<AddCartItemCommand>.Invalid(errors)
            : RequestValidation<AddCartItemCommand>.Valid(new AddCartItemCommand(request.VariantId!.Value, quantity));
    }

    /// <summary>PATCH mutlak adet: 1..20. <c>0</c> ve negatif 400'dür (silme DELETE ile).</summary>
    public static RequestValidation<QuantityBox> ValidateQuantityOnly(UpdateCartItemRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var quantity = ValidateQuantity(request.Quantity, errors);
        return errors.Count > 0
            ? RequestValidation<QuantityBox>.Invalid(errors)
            : RequestValidation<QuantityBox>.Valid(new QuantityBox(quantity));
    }

    private static int ValidateQuantity(long? raw, Dictionary<string, string[]> errors)
    {
        if (raw is not { } quantity)
        {
            errors["quantity"] = ["Adet zorunludur."];
            return 0;
        }

        if (quantity < 1 || quantity > CartRules.MaxLineQuantity)
        {
            errors["quantity"] = [$"Adet 1 ile {CartRules.MaxLineQuantity} arasında olmalıdır."];
            return 0;
        }

        return (int)quantity;
    }
}

/// <summary>Doğrulanmış tek adet değeri (RequestValidation sınıf tipi ister).</summary>
public sealed record QuantityBox(int Quantity);
