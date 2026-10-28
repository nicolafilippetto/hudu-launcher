using Launcher.Server.Audit;
using Launcher.Server.Data;
using Launcher.Server.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Pages.Admin;

public sealed class BrandingModel(AppDbContext db, AuditService audit) : LauncherPageModel
{
    public bool HasLogo { get; private set; }
    public bool HasFavicon { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var kinds = await db.Branding.Select(b => b.Kind).ToListAsync(ct);
        HasLogo = kinds.Contains(BrandingEndpoints.Logo);
        HasFavicon = kinds.Contains(BrandingEndpoints.Favicon);
    }

    public async Task<IActionResult> OnPostUploadAsync(string kind, IFormFile? file, CancellationToken ct)
    {
        if (kind is not (BrandingEndpoints.Logo or BrandingEndpoints.Favicon) || file is null || file.Length == 0)
        {
            FlashError = "Scegli un file.";
            return RedirectToPage();
        }

        if (file.Length > BrandingEndpoints.MaxBytes)
        {
            FlashError = "Il file supera 512 KB.";
            return RedirectToPage();
        }

        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, ct);
        var data = memory.ToArray();
        var contentType = BrandingEndpoints.DetectImageType(data);
        if (contentType is null)
        {
            FlashError = "Formato non supportato: usa PNG, JPEG, WebP o ICO.";
            return RedirectToPage();
        }

        var existing = await db.Branding.FindAsync([kind], ct);
        if (existing is null)
        {
            db.Branding.Add(new BrandingAsset { Kind = kind, ContentType = contentType, Data = data, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            existing.ContentType = contentType;
            existing.Data = data;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("branding.update", Email, Details: kind), HttpContext, ct);
        Flash = kind == BrandingEndpoints.Logo ? "Logo aggiornato." : "Favicon aggiornata.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string kind, CancellationToken ct)
    {
        var existing = await db.Branding.FindAsync([kind], ct);
        if (existing is not null)
        {
            db.Branding.Remove(existing);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("branding.update", Email, Details: kind + " rimosso"), HttpContext, ct);
        }

        Flash = "Ripristinato quello predefinito.";
        return RedirectToPage();
    }
}
