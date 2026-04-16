using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Yastık6.Api.Infrastructure.Swagger;

public class ApiKeyHeaderFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Eğer metodun hiç parametresi yoksa listeyi başlat
        operation.Parameters ??= new List<IOpenApiParameter>();

        // Her API ucuna (endpoint) bizim gizli şifre kutucuğumuzu ekle
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Mobile-Client-Key",
            In = ParameterLocation.Header,
            Description = "Mobile Client Gizli Şifresi",
            Required = true, // Reviewer'ın boş geçmesini engeller
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String            }
        });
    }
}