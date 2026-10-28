using System.Net;
using System.Text;
using Launcher.Connector.Core;

namespace Launcher.Connector.Tests;

public sealed class LaunchRequestTests
{
    private const string Ticket = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abcd";

    [Theory]
    [InlineData("hudulauncher://connect?ticket=" + Ticket)]
    [InlineData("hudulauncher://connect/?ticket=" + Ticket)]
    [InlineData("HUDULAUNCHER://CONNECT?ticket=" + Ticket)]
    public void Accepts_valid_links(string link) => Assert.Equal(Ticket, LaunchRequest.Parse(link)?.Ticket);

    [Theory]
    [InlineData("hudulauncher://connect?ticket=" + Ticket + "&host=10.0.0.1")]
    [InlineData("hudulauncher://connect?ticket=" + Ticket + "&ticket=" + Ticket)]
    [InlineData("hudulauncher://other?ticket=" + Ticket)]
    [InlineData("hudulauncher://connect/evil?ticket=" + Ticket)]
    [InlineData("https://connect?ticket=" + Ticket)]
    [InlineData("hudulauncher://connect?ticket=short")]
    [InlineData("hudulauncher://connect?ticket=%22%3Bcalc.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_tampered_links(string? link) => Assert.Null(LaunchRequest.Parse(link));

    [Fact]
    public void Finds_the_link_among_arguments() =>
        Assert.NotNull(LaunchRequest.FromArgs(["--something", "hudulauncher://connect?ticket=" + Ticket]));

    [Theory]
    [InlineData("https%3A%2F%2Flauncher.example.com", "entra", "https://launcher.example.com/", false)]
    [InlineData("http%3A%2F%2F10.0.0.50%3A8080", "local", "http://10.0.0.50:8080/", true)]
    [InlineData("http%3A%2F%2Flocalhost%3A5000", "local", "http://localhost:5000/", true)]
    public void Reads_portal_and_access_type(string portal, string auth, string expected, bool local)
    {
        var request = LaunchRequest.Parse($"hudulauncher://connect?ticket={Ticket}&portal={portal}&auth={auth}");
        Assert.NotNull(request);
        Assert.Equal(new Uri(expected), request.Portal);
        Assert.Equal(local, request.LocalAuth);
        Assert.Equal(request, LaunchRequest.Parse(request.ToLink()));
    }

    [Theory]
    [InlineData("&portal=http%3A%2F%2Fevil.example.com")]
    [InlineData("&portal=http%3A%2F%2F8.8.8.8")]
    [InlineData("&portal=https%3A%2F%2Fuser%3Apass%40portale.it")]
    [InlineData("&portal=file%3A%2F%2F%2Fc%3A%2Fwindows")]
    [InlineData("&portal=notaurl")]
    [InlineData("&auth=admin")]
    [InlineData("&auth=local&auth=entra")]
    public void Rejects_bad_portal_or_access_type(string extra) =>
        Assert.Null(LaunchRequest.Parse($"hudulauncher://connect?ticket={Ticket}{extra}"));

    [Fact]
    public void Old_links_mean_365_without_portal()
    {
        var request = LaunchRequest.Parse("hudulauncher://connect?ticket=" + Ticket)!;
        Assert.Null(request.Portal);
        Assert.False(request.LocalAuth);
    }
}

public sealed class PortalUrlTests
{
    [Theory]
    [InlineData("https://launcher.example.com/Admin?x=1", "https://launcher.example.com/")]
    [InlineData("http://192.168.1.5", "http://192.168.1.5/")]
    [InlineData("http://10.1.2.3:8080/", "http://10.1.2.3:8080/")]
    [InlineData("http://172.31.0.1", "http://172.31.0.1/")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/")]
    public void Accepts_https_and_internal_http(string value, string expected) =>
        Assert.Equal(new Uri(expected), PortalClient.TryNormalizePortalUrl(value));

    [Theory]
    [InlineData("http://launcher.example.com")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://1.2.3.4")]
    [InlineData("ftp://10.0.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_public_http_and_other_schemes(string? value) =>
        Assert.Null(PortalClient.TryNormalizePortalUrl(value));

    [Fact]
    public void Compares_origins() =>
        Assert.True(PortalClient.SameOrigin(new Uri("https://Portale.it/"), new Uri("https://portale.it:443/")));
}

public sealed class PeerConfigFileTests
{
    // Struttura reale di un file di RustDesk (accorciata).
    private const string RealFile = """
        password = []
        size = [
            0,
            0,
        ]
        view_style = 'original'
        custom_image_quality = [50]
        port_forwards = []
        direct_failures = 1

        [options]
        alias = 'ACME - Export VM'

        [transfer]
        write_jobs = []
        read_jobs = []
        """;

    [Fact]
    public void Replaces_empty_rules_in_place()
    {
        var result = PeerConfigFile.SetPortForwards(RealFile, [new TunnelRule(51234, "192.168.1.10", 3389)]);
        Assert.Contains("port_forwards = [[51234, \"192.168.1.10\", 3389]]\ndirect_failures = 1", result.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("alias = 'ACME - Export VM'", result, StringComparison.Ordinal);
        Assert.Contains("custom_image_quality = [50]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Replaces_multiline_rules_written_by_rustdesk()
    {
        var pretty = RealFile.Replace("port_forwards = []", "port_forwards = [\n    [\n    5000,\n    \"10.0.0.1\",\n    80,\n],\n]", StringComparison.Ordinal);
        var result = PeerConfigFile.SetPortForwards(pretty, [new TunnelRule(1, "a", 2), new TunnelRule(3, "localhost", 4)]);
        Assert.Contains("port_forwards = [[1, \"a\", 2], [3, \"localhost\", 4]]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inserts_before_first_table_when_missing()
    {
        var result = PeerConfigFile.SetPortForwards("password = []\n\n[options]\nalias = 'x'\n", [new TunnelRule(1, "h", 2)]);
        Assert.Equal("password = []\n\nport_forwards = [[1, \"h\", 2]]\n[options]\nalias = 'x'\n", result);
    }

    [Fact]
    public void Creates_content_for_empty_file() =>
        Assert.Equal("port_forwards = [[1, \"h\", 2]]\n", PeerConfigFile.SetPortForwards("", [new TunnelRule(1, "h", 2)]));

    [Theory]
    [InlineData("10.0.0.1\", 1]] evil = [[\"")]
    [InlineData("host name")]
    [InlineData("")]
    public void Rejects_hosts_that_could_inject_toml(string host) =>
        Assert.Throws<ArgumentException>(() => PeerConfigFile.SetPortForwards("", [new TunnelRule(1, host, 2)]));
}

public sealed class TelnetFilterTests
{
    [Fact]
    public void Separates_data_and_answers_negotiation()
    {
        var filter = new TelnetFilter();
        byte[] input = [(byte)'h', TelnetFilter.Iac, TelnetFilter.Will, TelnetFilter.OptionEcho, TelnetFilter.Iac, TelnetFilter.Do, 24, (byte)'i'];
        var (data, reply) = filter.Process(input);
        Assert.Equal("hi"u8.ToArray(), data);
        Assert.Equal([TelnetFilter.Iac, TelnetFilter.Do, TelnetFilter.OptionEcho, TelnetFilter.Iac, TelnetFilter.Wont, 24], reply);
    }

    [Fact]
    public void Handles_commands_split_across_reads()
    {
        var filter = new TelnetFilter();
        var first = filter.Process([(byte)'a', TelnetFilter.Iac]);
        var second = filter.Process([TelnetFilter.Will, TelnetFilter.OptionSuppressGoAhead, (byte)'b']);
        Assert.Equal("a"u8.ToArray(), first.Data);
        Assert.Equal("b"u8.ToArray(), second.Data);
        Assert.Equal([TelnetFilter.Iac, TelnetFilter.Do, TelnetFilter.OptionSuppressGoAhead], second.Reply);
    }

    [Fact]
    public void Skips_subnegotiation_and_unescapes_255()
    {
        var filter = new TelnetFilter();
        var (data, _) = filter.Process([TelnetFilter.Iac, TelnetFilter.Sb, 24, 1, TelnetFilter.Iac, TelnetFilter.Se, TelnetFilter.Iac, TelnetFilter.Iac, (byte)'x']);
        Assert.Equal([TelnetFilter.Iac, (byte)'x'], data);
    }

    [Fact]
    public void Escapes_outgoing_255() =>
        Assert.Equal([1, TelnetFilter.Iac, TelnetFilter.Iac, 2], TelnetFilter.Escape([1, TelnetFilter.Iac, 2]));
}

public sealed class KnownHostsTests
{
    [Fact]
    public void Trust_on_first_use_then_detect_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), "kh-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var hosts = new KnownHosts(path);
            Assert.Equal(HostKeyStatus.Unknown, hosts.Check("launcher-1-primary", "SHA256:aaa"));
            hosts.Trust("launcher-1-primary", "SHA256:aaa");
            Assert.Equal(HostKeyStatus.Trusted, new KnownHosts(path).Check("launcher-1-primary", "SHA256:aaa"));
            Assert.Equal(HostKeyStatus.Changed, new KnownHosts(path).Check("launcher-1-primary", "SHA256:bbb"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class PortalClientTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("https://launcher.example.com/qualcosa", "https://launcher.example.com/")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/")]
    public void Portal_url_is_normalized(string input, string expected) =>
        Assert.Equal(expected, PortalClient.ValidatePortalUrl(input).ToString());

    [Theory]
    [InlineData("http://launcher.example.com")]
    [InlineData("ftp://x")]
    [InlineData(null)]
    public void Insecure_portal_url_is_rejected(string? input) =>
        Assert.Throws<PortalException>(() => PortalClient.ValidatePortalUrl(input));

    [Fact]
    public async Task Redeem_parses_the_server_response()
    {
        var json = """
            {"protocol":"Rdp","name":"SRV-DC01","company":"Cliente","label":"RDP",
             "gateway":{"id":"100200300","password":"x"},"target":{"host":"10.0.0.10","port":3389},
             "credential":{"username":"DOM\\admin","password":"P@ss"},"hostKeyAlias":"launcher-10-primary","warning":null}
            """;
        var stub = new Stub(HttpStatusCode.OK, json);
        var client = new PortalClient(new HttpClient(stub), new Uri("https://portal.test/"));

        var info = await client.RedeemAsync("ticket", "token", TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionProtocol.Rdp, info.Protocol);
        Assert.Equal(("10.0.0.10", 3389), (info.Target.Host, info.Target.Port));
        Assert.Equal("DOM\\admin", info.Credential!.Username);
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("https://portal.test/api/connector/redeem", stub.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Errors_show_the_portal_message()
    {
        var client = new PortalClient(new HttpClient(new Stub(HttpStatusCode.Forbidden, """{"title":"Biglietto non valido o scaduto"}""")), new Uri("https://portal.test/"));
        var ex = await Assert.ThrowsAsync<PortalException>(() => client.RedeemAsync("t", "x", TestContext.Current.CancellationToken));
        Assert.Contains("scaduto", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class ConnectionInfoTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void Reads_rdp_and_terminal_options_from_the_portal()
    {
        const string body = """
            {"protocol":"Rdp","name":"SRV","company":"Cliente","label":"RDP","gateway":{"id":"1","password":"x"},
             "target":{"host":"10.0.0.1","port":3389},"credential":null,"hostKeyAlias":"a","warning":null,
             "rdp":{"adminSession":true,"clipboard":true,"drives":false,"printers":false,"smartCards":false,"ports":false,
                    "devices":false,"webAuthn":true,"microphone":false,"audio":"local","keyboard":"fullscreen",
                    "colorDepth":24,"display":"scale","nla":false},
             "terminal":{"highlight":true,"rules":[{"pattern":"error","color":"red"}]},
             "huduUrl":"https://hudu.example.com/a/1"}
            """;
        var info = System.Text.Json.JsonSerializer.Deserialize<ConnectionInfo>(body, Json)!;
        Assert.True(info.Rdp!.AdminSession);
        Assert.True(info.Rdp.WebAuthn);
        Assert.Equal(0, info.Rdp.AudioMode);
        Assert.Equal(2, info.Rdp.KeyboardMode);
        Assert.Equal(24, info.Rdp.SafeColorDepth);
        Assert.False(info.Rdp.Nla);
        Assert.Equal("red", info.Terminal!.Rules![0].Color);
        Assert.Equal("https://hudu.example.com/a/1", info.HuduUrl);
    }

    [Fact]
    public void Older_portals_get_safe_rdp_defaults()
    {
        const string body = """
            {"protocol":"Rdp","name":"SRV","company":"C","label":"RDP","gateway":{"id":"1"},"target":{"host":"h","port":3389},"hostKeyAlias":"a"}
            """;
        var info = System.Text.Json.JsonSerializer.Deserialize<ConnectionInfo>(body, Json)!;
        var options = info.Rdp ?? RdpOptions.Default;
        Assert.True(options.Clipboard);
        Assert.False(options.Drives || options.Printers || options.AdminSession);
        Assert.Equal(2, options.AudioMode);
        Assert.Equal(1, options.KeyboardMode);
    }
}

public sealed class DirectConnectionTests
{
    [Fact]
    public void Direct_connection_has_no_gateway_and_targets_the_device()
    {
        const string body = """
            {"protocol":"Ssh","name":"SW-CORE","company":"C","label":"SSH","gateway":null,"target":{"host":"192.168.1.2","port":22},"hostKeyAlias":"a"}
            """;
        var info = System.Text.Json.JsonSerializer.Deserialize<ConnectionInfo>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        })!;
        Assert.True(info.IsDirect);
        Assert.Equal("connessione diretta", info.Route);
        var endpoint = SessionEndpoint.ForTarget(info.Target);
        Assert.Equal(("192.168.1.2", 22, true), (endpoint.Host, endpoint.Port, endpoint.Direct));
        Assert.Equal(("127.0.0.1", 51000, false), (SessionEndpoint.ForTunnel(51000).Host, SessionEndpoint.ForTunnel(51000).Port, SessionEndpoint.ForTunnel(51000).Direct));
        Assert.Equal("[fe80::1]", new SessionEndpoint("fe80::1", 443, true).UrlHost);
    }
}

public sealed class OfflineCacheTests
{
    private const string Ticket = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abcd";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ConnectionInfo Info(string name, bool direct = true) => new(
        ConnectionProtocol.Ssh, name, "Cliente", "SSH", direct ? null : new GatewayInfo("123", "x"), new TargetInfo("192.168.1.2", 22), null, "alias", null);

    [Fact]
    public void Offline_links_are_recognised()
    {
        var request = LaunchRequest.Parse($"hudulauncher://offline?ticket={Ticket}&portal=https%3A%2F%2Flauncher.example.com&auth=entra");
        Assert.NotNull(request);
        Assert.True(request.Offline);
        Assert.Equal(request, LaunchRequest.Parse(request.ToLink()));
        Assert.False(LaunchRequest.Parse($"hudulauncher://connect?ticket={Ticket}")!.Offline);
        Assert.Null(LaunchRequest.Parse($"hudulauncher://export?ticket={Ticket}"));
    }

    [Fact]
    public void New_bundle_replaces_the_old_one_and_keeps_only_direct_connections()
    {
        var clock = new ManualClock();
        var cache = new OfflineCache(clock);
        cache.Replace([Info("A"), Info("B")], TimeSpan.FromMinutes(60));
        cache.Replace([Info("C"), Info("D", direct: false)], TimeSpan.FromMinutes(60));
        Assert.Equal(["C"], cache.Items.Select(i => i.Name));
    }

    [Fact]
    public void Cache_empties_itself_when_it_expires()
    {
        var clock = new ManualClock();
        var cache = new OfflineCache(clock);
        var changes = 0;
        cache.Changed += () => changes++;
        cache.Replace([Info("A")], TimeSpan.FromMinutes(60));
        clock.Now = clock.Now.AddMinutes(59);
        Assert.Single(cache.Items);
        Assert.Equal(TimeSpan.FromMinutes(1), cache.Remaining);

        clock.Now = clock.Now.AddMinutes(2);
        Assert.Empty(cache.Items);
        Assert.Null(cache.ExpiresAt);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        var cache = new OfflineCache(new ManualClock());
        cache.Replace([Info("A")], TimeSpan.FromMinutes(60));
        cache.Clear();
        Assert.Empty(cache.Items);
    }
}

public sealed class RustDeskClientPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Programmi-di-prova");

    [Fact]
    public void Accepts_only_executables_inside_program_files()
    {
        Assert.True(RustDeskClientPath.IsAllowed(Path.Combine(Root, "Azienda-RustDesk-Client", "client.exe"), [Root]));
        Assert.True(RustDeskClientPath.IsAllowed(Path.Combine(Root, "RustDesk", "RUSTDESK.EXE"), [Root]));

        Assert.False(RustDeskClientPath.IsAllowed(null, [Root]));
        Assert.False(RustDeskClientPath.IsAllowed("rustdesk.exe", [Root]));
        Assert.False(RustDeskClientPath.IsAllowed(Path.Combine(Root, "RustDesk", "rustdesk.bat"), [Root]));
        Assert.False(RustDeskClientPath.IsAllowed(Path.Combine(Path.GetTempPath(), "Scaricati", "rustdesk.exe"), [Root]));
        // ".." non permette di uscire dalla cartella ammessa.
        Assert.False(RustDeskClientPath.IsAllowed(Path.Combine(Root, "..", "Scaricati", "rustdesk.exe"), [Root]));
        // Una cartella che inizia con lo stesso nome non è la stessa cartella.
        Assert.False(RustDeskClientPath.IsAllowed(Root + "-falsa" + Path.DirectorySeparatorChar + "rustdesk.exe", [Root]));
    }

    [Fact]
    public void Gateway_carries_the_client_path_from_the_portal()
    {
        var info = System.Text.Json.JsonSerializer.Deserialize<GatewayInfo>(
            """{"id":"123456789","password":"x","clientPath":"C:\\Program Files\\RustDesk\\rustdesk.exe"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(@"C:\Program Files\RustDesk\rustdesk.exe", info.ClientPath);
    }
}
