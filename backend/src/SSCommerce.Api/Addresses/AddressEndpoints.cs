using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Application.Addresses;

namespace SSCommerce.Api.Addresses;

/// <summary>
/// Adres uçları (F6.3, D-052). <c>/api/v1/addresses</c> grubu <c>RequireAuthorization()</c> ister; yalnız KENDİ adresleri görülür/değiştirilir
/// (başkasınınki 404 address_not_found). Adres kişisel veridir: loglanmaz, URL'ye konmaz. Varsayılanı yalnız sunucu ve <c>/default</c> ucu belirler
/// (gövdedeki <c>isDefault</c> yok sayılır).
/// </summary>
public static class AddressEndpoints
{
    public static IEndpointRouteBuilder MapAddressEndpoints(this IEndpointRouteBuilder api)
    {
        var addresses = api.MapGroup("/addresses").WithTags("Addresses").RequireAuthorization();

        addresses.MapGet(string.Empty, List)
            .WithName("ListAddresses")
            .WithSummary("Kullanıcının adresleri, varsayılan önce.")
            .Produces<IReadOnlyList<AddressDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        addresses.MapPost(string.Empty, Create)
            .WithName("CreateAddress")
            .WithSummary("Adres ekler (en çok 10). İlk adres otomatik varsayılan olur. Sınır 409 address_limit.")
            .Accepts<AddressRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AddressDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        addresses.MapPut("/{id}", Update)
            .WithName("UpdateAddress")
            .WithSummary("Adresi tam günceller (varsayılan bayrağı hariç).")
            .Accepts<AddressRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AddressDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        addresses.MapPost("/{id}/default", SetDefault)
            .WithName("SetDefaultAddress")
            .WithSummary("Adresi varsayılan yapar (eski varsayılan kalkar).")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AddressDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        addresses.MapDelete("/{id}", Delete)
            .WithName("DeleteAddress")
            .WithSummary("Adresi siler. Varsayılan silinirse en son güncellenen adres varsayılan olur.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static async Task<IResult> List(ClaimsPrincipal user, IAddressService addresses, CancellationToken cancellationToken) =>
        Results.Ok(await addresses.ListAsync(user.GetUserId(), cancellationToken));

    private static async Task<IResult> Create(HttpContext http, ClaimsPrincipal user, IAddressService addresses, CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<AddressRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = AddressRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        var result = await addresses.CreateAsync(user.GetUserId(), validation.Value!, cancellationToken);
        return result.Status == AddressOperationStatus.Ok
            ? Results.Json(result.Address, statusCode: StatusCodes.Status201Created)
            : ToProblem(result);
    }

    private static async Task<IResult> Update(
        string id,
        HttpContext http,
        ClaimsPrincipal user,
        IAddressService addresses,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<AddressRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = AddressRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        if (!Guid.TryParse(id, out var addressId))
        {
            return NotFound();
        }

        var result = await addresses.UpdateAsync(user.GetUserId(), addressId, validation.Value!, cancellationToken);
        return result.Status == AddressOperationStatus.Ok ? Results.Ok(result.Address) : ToProblem(result);
    }

    private static async Task<IResult> SetDefault(string id, ClaimsPrincipal user, IAddressService addresses, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var addressId))
        {
            return NotFound();
        }

        var result = await addresses.SetDefaultAsync(user.GetUserId(), addressId, cancellationToken);
        return result.Status == AddressOperationStatus.Ok ? Results.Ok(result.Address) : ToProblem(result);
    }

    private static async Task<IResult> Delete(string id, ClaimsPrincipal user, IAddressService addresses, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var addressId))
        {
            return NotFound();
        }

        var result = await addresses.DeleteAsync(user.GetUserId(), addressId, cancellationToken);
        return result.Status == AddressOperationStatus.Ok ? Results.NoContent() : ToProblem(result);
    }

    private static IResult ToProblem(AddressOperationResult result) => result.Status switch
    {
        AddressOperationStatus.NotFound => NotFound(),
        AddressOperationStatus.LimitReached => ApiResults.Problem(
            StatusCodes.Status409Conflict, "En çok 10 adres kaydedilebilir", ApiErrorCodes.AddressLimit),
        AddressOperationStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy),
        _ => throw new InvalidOperationException("Bilinmeyen adres işlem durumu."),
    };

    private static IResult NotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Adres bulunamadı", ApiErrorCodes.AddressNotFound);
}
