using Launcher.Server.Data;
using Launcher.Server.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.Server.Tests;

[Collection("Server")]
public sealed class SyncTests
{
    [Fact]
    public async Task Hudu_sync_completes_even_if_rustdesk_times_out()
    {
        // Istanza dedicata: la sincronizzazione sostituisce la cache e non deve toccare gli altri test.
        using var factory = new LauncherFactory();
        await factory.SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Peers.Add(new CachedPeer { Id = "999", Hostname = "VECCHIO" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await scope.ServiceProvider.GetRequiredService<SyncService>().RunAsync("test", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("RustDesk non raggiungibile", result.Message, StringComparison.Ordinal);
        var state = await db.SyncStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(state.LastSuccessAt);
        Assert.Contains("RustDesk", state.LastError, StringComparison.Ordinal);
        // La rubrica precedente resta valida.
        Assert.True(await db.Peers.AnyAsync(p => p.Id == "999", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Address_book_is_read_with_get_and_the_console_token()
    {
        using var factory = new ConsoleTokenFactory();
        await factory.SeedAsync();
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<SyncService>().RunAsync("test", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var peers = await db.Peers.AsNoTracking().OrderBy(p => p.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["100200300", "987654321"], peers.Select(p => p.Id));
        Assert.Equal("Clienti", peers[0].AddressBook);
        Assert.Equal("JUMP01", peers[0].Hostname);
    }

    /// <summary>Come RustDesk Server Pro con un token della console: GET sì, POST no.</summary>
    private sealed class ConsoleTokenFactory : LauncherFactory
    {
        protected override HttpMessageHandler CreateRustDeskHandler() => new ConsoleApi();

        private sealed class ConsoleApi : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Get || request.Headers.Authorization?.Parameter != "token")
                {
                    return Task.FromResult(Json(System.Net.HttpStatusCode.Unauthorized, "{\"error\":\"Invalid token\"}"));
                }

                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                return Task.FromResult(request.RequestUri.AbsolutePath switch
                {
                    "/api/ab/shared/profiles" => Json(System.Net.HttpStatusCode.OK, "{\"total\":1,\"data\":[{\"guid\":\"ab-1\",\"name\":\"Clienti\"}]}"),
                    "/api/ab/peers" when query["ab"] == "ab-1" => Json(System.Net.HttpStatusCode.OK,
                        "{\"total\":2,\"data\":[{\"id\":\"100200300\",\"hostname\":\"JUMP01\",\"alias\":\"JUMP 01\",\"tags\":[\"OTS\"]},{\"id\":\"987654321\",\"hostname\":\"SRV\"}]}"),
                    _ => Json(System.Net.HttpStatusCode.NotFound, "{}"),
                });
            }

            private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body) =>
                new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
