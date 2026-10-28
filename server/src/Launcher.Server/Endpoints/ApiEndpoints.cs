using System.Security.Claims;
using System.Text.Json.Serialization;
using Launcher.Server.Audit;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Integrations;
using Launcher.Server.Inventory;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Launcher.Server.Endpoints;

/// <summary>Richiesta di connessione; Mode "direct" = senza jump host RustDesk.</summary>
public sealed record ConnectRequest(int AssetId, string Key, string? Mode = null);

public sealed record ConnectResponse(string Url);

public sealed record RedeemRequest(string Ticket);

/// <summary>Asset da preparare per l'uso senza rete (cache d'emergenza nel Launcher).</summary>
public sealed record OfflineRequest(int[]? AssetIds);

/// <summary>Pacchetto offline: connessioni dirette con le credenziali, valide per pochi minuti.</summary>
public sealed record OfflineBundleResponse(int ValidSeconds, IReadOnlyList<RedeemResponse> Connections);

public sealed record ConnectorConfigResponse(string? TenantId, string? ClientId, string? Scope, string PortalName);

/// <summary>Jump host RustDesk; ClientPath = client personalizzato indicato dall'admin (facoltativo).</summary>
public sealed record RedeemGateway(string Id, string? Password, string? ClientPath = null);

public sealed record RedeemTarget(string Host, int Port);

public sealed record RedeemCredential(string? Username, string? Password);

public sealed record RedeemResponse(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ConnectionProtocol>))] ConnectionProtocol Protocol,
    string Name,
    string Company,
    string Label,
    RedeemGateway? Gateway,
    RedeemTarget Target,
    RedeemCredential? Credential,
    string HostKeyAlias,
    string? Warning,
    RdpOptions? Rdp,
    TerminalOptions? Terminal,
    string? HuduUrl);

public static class RateLimitPolicies
{
    public const string Connect = "connect";
    public const string Redeem = "redeem";
    public const string Login = "login";
}

public static class ApiEndpoints
{
    public const string ProtocolScheme = "hudulauncher";

    public static void MapLauncherApi(this IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api")
            .RequireAuthorization(Policies.Technician)
            .AddEndpointFilter(ValidateAntiforgeryAsync);

        portal.MapPost("/connect", ConnectAsync).RequireRateLimiting(RateLimitPolicies.Connect);
        portal.MapPost("/offline", PrepareOfflineAsync).RequireRateLimiting(RateLimitPolicies.Connect);
        portal.MapPost("/favorites/{assetId:int}", ToggleFavoriteAsync);

        var connector = app.MapGroup("/api/connector");
        connector.MapGet("/config", (SettingsService settings) =>
        {
            var entra = settings.Entra;
            return Results.Ok(new ConnectorConfigResponse(
                entra.TenantId,
                entra.ClientId,
                entra.IsConfigured ? entra.ConnectorScope : null,
                settings.Portal.Name));
        }).AllowAnonymous();
        // Anonimo: il token 365 si verifica dentro, perché i biglietti degli admin locali non lo richiedono.
        connector.MapPost("/redeem", RedeemAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Redeem)
            .DisableAntiforgery();
        connector.MapPost("/redeem-offline", RedeemOfflineAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Redeem)
            .DisableAntiforgery();
    }

    private static async ValueTask<object?> ValidateAntiforgeryAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return Results.Problem("Richiesta non valida, ricaricare la pagina.", statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }

    private static async Task<IResult> ConnectAsync(
        ConnectRequest request, HttpContext http, InventoryService inventory, TicketService tickets, SettingsService settings,
        AuditService audit, CancellationToken ct)
    {
        var email = http.User.GetEmail()!;
        if (!TicketService.IsValidConnectionKey(request.Key ?? ""))
        {
            return Results.BadRequest();
        }

        var asset = await inventory.GetAssetAsync(request.AssetId, email, ct);
        var connection = asset?.Connections.FirstOrDefault(c => c.Key == request.Key);
        if (asset is null || connection is null)
        {
            return Results.NotFound();
        }

        // Con RustDesk spento esiste solo la connessione diretta.
        var direct = !settings.RustDesk.Enabled || string.Equals(request.Mode, "direct", StringComparison.Ordinal);
        if (direct ? !connection.CanConnectDirect : !connection.CanConnect)
        {
            return Results.Problem(
                direct ? "Per la connessione diretta serve l'indirizzo IP." : "Connessione incompleta: " + string.Join(", ", connection.Issues),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (LauncherAccess(http, settings) is not { } method)
        {
            return LocalAccessDenied();
        }

        var token = await tickets.CreateAsync(email, asset.Asset.Id, connection.Key, http.Connection.RemoteIpAddress?.ToString(), method, direct, ct);
        var notes = new[] { direct ? DirectNote : null, method == LauncherClaims.MethodLocal ? LocalTicketNote : null }.OfType<string>().ToList();
        await audit.WriteAsync(new AuditEntry("ticket.create", email, CompanyName: asset.CompanyName, AssetId: asset.Asset.Id,
            AssetName: asset.Asset.Name, Target: Describe(connection, direct),
            Details: notes.Count == 0 ? null : string.Join("; ", notes)), http, ct);
        return Results.Ok(new ConnectResponse(BuildLaunchUrl(token, $"{http.Request.Scheme}://{http.Request.Host}", method)));
    }

    private const string LocalTicketNote = "senza 365 (admin locale)";

    /// <summary>"entra" o "local" se l'utente può usare il Launcher, altrimenti null.</summary>
    private static string? LauncherAccess(HttpContext http, SettingsService settings)
    {
        if (http.User.FindFirstValue(LauncherClaims.AuthMethod) == LauncherClaims.MethodEntra)
        {
            return LauncherClaims.MethodEntra;
        }

        return http.User.IsAdmin() && settings.Portal.AllowLocalAdminConnections ? LauncherClaims.MethodLocal : null;
    }

    private static IResult LocalAccessDenied() => Results.Problem(
        "Il Launcher richiede l'accesso con Microsoft 365 (oppure un admin può abilitare le connessioni senza 365 in Impostazioni → Portale).",
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Prepara la cache d'emergenza: un biglietto che il Launcher scambia con le connessioni dirette
    /// (credenziali comprese) degli asset scelti, da usare senza rete per pochi minuti.
    /// </summary>
    private static async Task<IResult> PrepareOfflineAsync(
        OfflineRequest request, HttpContext http, InventoryService inventory, TicketService tickets, SettingsService settings,
        AuditService audit, CancellationToken ct)
    {
        var email = http.User.GetEmail()!;
        var max = settings.Portal.OfflineMaxAssets;
        if (max == 0)
        {
            return Results.Problem("La cache offline è disattivata dall'amministratore.", statusCode: StatusCodes.Status403Forbidden);
        }

        var ids = (request.AssetIds ?? []).Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > max)
        {
            return Results.Problem($"Scegliere da 1 a {max} asset.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (LauncherAccess(http, settings) is not { } method)
        {
            return LocalAccessDenied();
        }

        var names = new List<string>();
        foreach (var id in ids)
        {
            var asset = await inventory.GetAssetAsync(id, email, ct);
            if (asset is null)
            {
                return Results.NotFound();
            }

            if (!asset.Connections.Any(c => c.CanConnectDirect))
            {
                return Results.Problem($"{asset.Asset.Name}: manca l'indirizzo IP, non si può usare offline.", statusCode: StatusCodes.Status409Conflict);
            }

            names.Add($"{asset.Asset.Name} ({asset.CompanyName})");
        }

        var token = await tickets.CreateAsync(email, 0, "offline", http.Connection.RemoteIpAddress?.ToString(), method, direct: true, ct, ids);
        await audit.WriteAsync(new AuditEntry("offline.prepare", email,
            Details: $"{ids.Count} asset per {settings.Portal.OfflineMinutes} min: {string.Join(", ", names)}"
                     + (method == LauncherClaims.MethodLocal ? "; " + LocalTicketNote : "")), http, ct);
        return Results.Ok(new ConnectResponse(BuildLaunchUrl(token, $"{http.Request.Scheme}://{http.Request.Host}", method, "offline")));
    }
    private const string DirectNote = "connessione diretta";

    /// <summary>
    /// Link per il Launcher: il biglietto, l'indirizzo del portale (il Launcher lo propone al primo avvio)
    /// e il tipo di accesso (local = senza token 365).
    /// </summary>
    public static string BuildLaunchUrl(string token, string portalOrigin, string authMethod, string action = "connect") =>
        $"{ProtocolScheme}://{action}?ticket={Uri.EscapeDataString(token)}&portal={Uri.EscapeDataString(portalOrigin)}&auth={authMethod}";

    private static async Task<IResult> ToggleFavoriteAsync(int assetId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var email = http.User.GetEmail()!;
        var existing = await db.Favorites.FindAsync([email, assetId], ct);
        if (existing is not null)
        {
            db.Favorites.Remove(existing);
        }
        else if (await db.Assets.AnyAsync(a => a.Id == assetId, ct))
        {
            db.Favorites.Add(new Favorite { UserEmail = email, AssetId = assetId, CreatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            return Results.NotFound();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { favorite = existing is null });
    }

    private static async Task<IResult> RedeemAsync(
        RedeemRequest request, HttpContext http, TicketService tickets, InventoryService inventory, HuduClient hudu,
        SettingsService settings, PortalPrincipalFactory principals, AuditService audit, ILogger<TicketService> logger, CancellationToken ct)
    {
        var (ticket, error) = await AuthorizeRedeemAsync(request, http, tickets, settings, principals, audit, offline: false, ct);
        if (ticket is null)
        {
            return error!;
        }

        var email = ticket.UserEmail;
        var asset = await inventory.GetAssetAsync(ticket.AssetId, email, ct);
        var connection = asset?.Connections.FirstOrDefault(c => c.Key == ticket.ConnectionKey);
        if (asset is null || connection is null || (ticket.Direct ? !connection.CanConnectDirect : !connection.CanConnect))
        {
            return Results.Problem("La connessione non è più disponibile.", statusCode: StatusCodes.Status409Conflict);
        }

        var (response, credentialNote) = await BuildConnectionAsync(asset, connection, ticket.Direct, settings, hudu, logger, ct);
        await audit.WriteAsync(new AuditEntry("ticket.redeem", email, CompanyName: asset.CompanyName, AssetId: asset.Asset.Id,
            AssetName: asset.Asset.Name, Target: Describe(connection, ticket.Direct),
            Details: credentialNote + (ticket.Direct ? "; " + DirectNote : "") + (ticket.AuthMethod == LauncherClaims.MethodLocal ? "; " + LocalTicketNote : "")),
            http, ct);
        return Results.Ok(response);
    }

    /// <summary>Il Launcher scambia il biglietto offline con le connessioni dirette degli asset scelti.</summary>
    private static async Task<IResult> RedeemOfflineAsync(
        RedeemRequest request, HttpContext http, TicketService tickets, InventoryService inventory, HuduClient hudu,
        SettingsService settings, PortalPrincipalFactory principals, AuditService audit, ILogger<TicketService> logger, CancellationToken ct)
    {
        var (ticket, error) = await AuthorizeRedeemAsync(request, http, tickets, settings, principals, audit, offline: true, ct);
        if (ticket is null)
        {
            return error!;
        }

        if (settings.Portal.OfflineMaxAssets == 0)
        {
            return Results.Problem("La cache offline è disattivata dall'amministratore.", statusCode: StatusCodes.Status403Forbidden);
        }

        var connections = new List<RedeemResponse>();
        foreach (var assetId in TicketService.OfflineAssetIds(ticket).Take(settings.Portal.OfflineMaxAssets))
        {
            var asset = await inventory.GetAssetAsync(assetId, ticket.UserEmail, ct);
            if (asset is null)
            {
                continue;
            }

            var notes = new List<string>();
            foreach (var connection in asset.Connections.Where(c => c.CanConnectDirect))
            {
                var (response, credentialNote) = await BuildConnectionAsync(asset, connection, direct: true, settings, hudu, logger, ct);
                connections.Add(response);
                notes.Add($"{connection.Label}: {credentialNote}");
            }

            await audit.WriteAsync(new AuditEntry("offline.redeem", ticket.UserEmail, CompanyName: asset.CompanyName, AssetId: asset.Asset.Id,
                AssetName: asset.Asset.Name, Details: string.Join("; ", notes)), http, ct);
        }

        return Results.Ok(new OfflineBundleResponse(settings.Portal.OfflineMinutes * 60, connections));
    }

    /// <summary>
    /// Verifica comune ai riscatti: biglietto valido e non usato, token 365 dello stesso utente (o admin
    /// locale dallo stesso IP, se ammesso), tipo di biglietto giusto per l'endpoint.
    /// </summary>
    private static async Task<(Ticket? Ticket, IResult? Error)> AuthorizeRedeemAsync(
        RedeemRequest request, HttpContext http, TicketService tickets, SettingsService settings, PortalPrincipalFactory principals,
        AuditService audit, bool offline, CancellationToken ct)
    {
        // Token 365 del connettore, se presente. Un token presente ma non valido è sempre un errore.
        string? tokenEmail = null;
        if (http.Request.Headers.Authorization.Count > 0)
        {
            var auth = await http.AuthenticateAsync(AuthSchemes.Connector);
            tokenEmail = auth.Succeeded ? auth.Principal.GetEmail() : null;
            if (tokenEmail is null)
            {
                return (null, Results.Unauthorized());
            }
        }

        var clientIp = http.Connection.RemoteIpAddress?.ToString();
        var redeemed = await tickets.RedeemAsync(request.Ticket ?? "", tokenEmail, clientIp, ct);
        var actor = tokenEmail ?? (redeemed?.Ticket.UserEmail is { Length: > 0 } owner ? owner : "anonimo");
        if (redeemed is null || redeemed.Error != RedeemError.None)
        {
            var reason = redeemed?.Error switch
            {
                RedeemError.Expired => "scaduto",
                RedeemError.AlreadyUsed => "già usato",
                RedeemError.WrongUser when tokenEmail is null => "senza token 365",
                RedeemError.WrongUser => $"emesso per {redeemed.Ticket.UserEmail}",
                RedeemError.WrongClient => $"admin locale da un altro indirizzo (emesso per {redeemed.Ticket.ClientIp})",
                _ => "inesistente",
            };
            await audit.WriteAsync(new AuditEntry("ticket.reject", actor, Success: false, AssetId: redeemed?.Ticket.AssetId is > 0 ? redeemed.Ticket.AssetId : null,
                Details: "Biglietto " + reason), http, ct);
            return (null, redeemed?.Error == RedeemError.WrongUser && tokenEmail is null
                ? Results.Unauthorized()
                : Results.Problem("Biglietto non valido o scaduto: rilanciare la connessione dal portale.", statusCode: StatusCodes.Status403Forbidden));
        }

        var ticket = redeemed.Ticket;
        if ((ticket.OfflineAssets is not null) != offline)
        {
            await audit.WriteAsync(new AuditEntry("ticket.reject", ticket.UserEmail, Success: false, Details: "Biglietto usato sull'endpoint sbagliato"), http, ct);
            return (null, Results.Problem("Biglietto non valido per questa richiesta.", statusCode: StatusCodes.Status400BadRequest));
        }

        if (ticket.AuthMethod == LauncherClaims.MethodLocal
            && (!settings.Portal.AllowLocalAdminConnections || !await principals.IsBreakGlassAsync(ticket.UserEmail, ct)))
        {
            await audit.WriteAsync(new AuditEntry("ticket.reject", ticket.UserEmail, Success: false, AssetId: ticket.AssetId > 0 ? ticket.AssetId : null,
                Details: "Connessioni senza 365 disattivate o utente non più admin"), http, ct);
            return (null, Results.Problem("Le connessioni senza Microsoft 365 non sono abilitate.", statusCode: StatusCodes.Status403Forbidden));
        }

        return (ticket, null);
    }

    /// <summary>Dati di una connessione per il Launcher, con la credenziale letta da Hudu in quel momento.</summary>
    private static async Task<(RedeemResponse Response, string CredentialNote)> BuildConnectionAsync(
        ResolvedAsset asset, ResolvedConnection connection, bool direct, SettingsService settings, HuduClient hudu, ILogger logger, CancellationToken ct)
    {
        RedeemCredential? credential = null;
        string? warning = null;
        // Le interfacce web si aprono nel browser: utente e password si prendono da Hudu, non dal Launcher.
        var isWeb = connection.Protocol is ConnectionProtocol.Http or ConnectionProtocol.Https;
        if (!isWeb && connection.PasswordId is { } credentialId)
        {
            try
            {
                var secret = await hudu.GetCredentialAsync(credentialId, ct);
                if (secret.CompanyId == 0 || secret.CompanyId == asset.Asset.CompanyId)
                {
                    credential = new RedeemCredential(secret.Username, secret.Password);
                }
            }
            catch (HuduException ex)
            {
                logger.LogWarning(ex, "Credenziale {CredentialId} non letta da Hudu", credentialId);
                warning = "Credenziale non disponibile (Hudu non risponde): inserirla a mano.";
            }
        }

        var response = new RedeemResponse(
            connection.Protocol,
            asset.Asset.Name,
            asset.CompanyName,
            connection.Label,
            // Connessione diretta: niente jump host e niente password di RustDesk.
            direct ? null : new RedeemGateway(connection.GatewayPeerId!, settings.RustDesk.ConnectionPassword, settings.RustDesk.ClientPath),
            new RedeemTarget(connection.Host!, connection.Port),
            credential,
            $"launcher-{asset.Asset.Id}-{connection.Key.Replace(':', '-')}",
            warning,
            connection.Protocol == ConnectionProtocol.Rdp
                ? RdpOptionCatalog.Resolve(settings.RdpDefaults, RdpOptionCatalog.Parse(asset.Setting?.RdpOptions))
                : null,
            connection.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Telnet ? settings.Terminal : null,
            asset.Asset.HuduUrl);
        return (response, credential is null ? "senza credenziale" : $"credenziale: {connection.PasswordName}");
    }

    private static string Describe(ResolvedConnection c, bool direct = false) =>
        $"{c.Protocol.ToString().ToLowerInvariant()}://{c.Host}:{c.Port} " + (direct ? "diretta" : $"via {c.GatewayPeerId}");
}
