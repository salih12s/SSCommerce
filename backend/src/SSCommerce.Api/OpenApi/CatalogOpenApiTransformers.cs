using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.OpenApi;

/// <summary>
/// <c>GET /api/v1/products</c> sorgu parametreleri kodda <c>string</c> bağlanır (tip hatası 400 <c>validation_failed</c> olsun diye,
/// bkz. CatalogEndpoints). OpenAPI belgesi ise gerçek sözleşmeyi göstermelidir: tam sayı, sınırlar, varsayılanlar, sort listesi.
/// Sınırlar <see cref="ProductListQueryValidator"/> sabitlerinden gelir; tek kaynak, drift yok.
/// </summary>
internal static class CatalogOpenApiTransformers
{
    public static OpenApiOptions AddCatalogQueryParameterTypes(this OpenApiOptions options) =>
        options.AddOperationTransformer((operation, context, _) =>
        {
            var description = context.Description;
            if (!string.Equals(description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(description.RelativePath, "api/v1/products", StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
            {
                switch (parameter.Name)
                {
                    case "page":
                        parameter.Schema = Integer("int32", 1, ProductListQueryValidator.MaxPage, ProductListQueryValidator.DefaultPage);
                        break;
                    case "pageSize":
                        parameter.Schema = Integer("int32", 1, ProductListQueryValidator.MaxPageSize, ProductListQueryValidator.DefaultPageSize);
                        break;
                    case "minPriceMinor":
                    case "maxPriceMinor":
                        parameter.Schema = Integer("int64", 0, ProductListQueryValidator.MaxPriceMinor, defaultValue: null);
                        break;
                    case "sort":
                        parameter.Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Default = JsonValue.Create("newest"),
                            Enum = ["newest", "price_asc", "price_desc", "name_asc"],
                        };
                        break;
                    case "q":
                        parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = ProductListQueryValidator.MaxSearchLength };
                        break;
                }
            }

            return Task.CompletedTask;
        });

    private static OpenApiSchema Integer(string format, long minimum, long maximum, int? defaultValue) => new()
    {
        Type = JsonSchemaType.Integer,
        Format = format,
        Minimum = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Maximum = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Default = defaultValue is { } d ? JsonValue.Create(d) : null,
    };
}
