using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Yastık6.Api.Application.Features.Prices;
using Yastık6.Api.Controllers;

namespace Yastık6.Api.Infrastructure.Swagger;

// Enums serialize as numbers, so Swagger would offer range=0..7. On the public page, show the
// three names the public endpoint accepts instead (model binding parses names as well as numbers).
public class PublicHistoryRangeFilter : IParameterFilter
{
    public void Apply(IOpenApiParameter parameter, ParameterFilterContext context)
    {
        if (context.ParameterInfo?.Member.DeclaringType != typeof(PublicPricesController) ||
            context.ApiParameterDescription.Type != typeof(PriceHistoryRange) ||
            parameter is not OpenApiParameter editable)
        {
            return;
        }

        editable.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = PublicPricesController.AllowedHistoryRanges.Select(r => (JsonNode)JsonValue.Create(r.ToString())).ToList(),
            Default = JsonValue.Create(nameof(PriceHistoryRange.Day))
        };
    }
}
