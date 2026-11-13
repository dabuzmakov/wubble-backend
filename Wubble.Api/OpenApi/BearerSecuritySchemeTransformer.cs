using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Wubble.Api.OpenApi;

/// <summary>
/// Добавляет в openapi.json схему Bearer, чтобы в Scalar можно было вставить access-токен
/// и вызывать защищённые эндпоинты. Типы из Microsoft.OpenApi 2.x (.NET 10).
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access-токен из cookie wubble_access.",
        };
        return Task.CompletedTask;
    }
}
