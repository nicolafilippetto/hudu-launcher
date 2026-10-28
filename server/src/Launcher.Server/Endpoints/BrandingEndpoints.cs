using Launcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Endpoints;

public static class BrandingEndpoints
{
    public const string Logo = "logo";
    public const string Favicon = "favicon";
    public const int MaxBytes = 512 * 1024;

    public static void MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/branding/{kind}", async (string kind, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (kind is not (Logo or Favicon))
            {
                return Results.NotFound();
            }

            var asset = await db.Branding.AsNoTracking().FirstOrDefaultAsync(b => b.Kind == kind, ct);
            if (asset is null)
            {
                return kind == Favicon ? Results.Redirect("/img/favicon.svg") : Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.File(asset.Data, asset.ContentType, lastModified: asset.UpdatedAt);
        }).AllowAnonymous();
    }

    /// <summary>
    /// Riconosce il formato dai primi byte. Sono ammessi solo formati raster (niente SVG, che
    /// potrebbe contenere script).
    /// </summary>
    public static string? DetectImageType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.Length > 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
        {
            return "image/x-icon";
        }

        return null;
    }
}
