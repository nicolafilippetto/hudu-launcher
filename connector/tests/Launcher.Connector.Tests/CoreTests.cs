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
    public void Reads_back_the_tunnel_rule_for_diagnostics()
    {
        var rule = new TunnelRule(51234, "192.168.1.10", 3389);
        var written = PeerConfigFile.SetPortForwards(RealFile, [rule]);
        Assert.True(PeerConfigFile.Contains(written, rule));
        Assert.False(PeerConfigFile.Contains(written, rule with { LocalPort = 51235 }));
        Assert.False(PeerConfigFile.Contains(RealFile, rule));
        Assert.Equal("port_forwards = [[51234, \"192.168.1.10\", 3389]]", PeerConfigFile.ReadPortForwards(written));
        Assert.Null(PeerConfigFile.ReadPortForwards("password = []\n"));
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

    [Fact]
    public void Accepts_window_size_negotiation_and_sends_the_size()
    {
        var filter = new TelnetFilter { WindowSize = (132, 43) };
        var (_, reply) = filter.Process([TelnetFilter.Iac, TelnetFilter.Do, TelnetFilter.OptionWindowSize]);

        Assert.True(filter.WindowSizeEnabled);
        Assert.Equal([TelnetFilter.Iac, TelnetFilter.Will, 31, TelnetFilter.Iac, TelnetFilter.Sb, 31, 0, 132, 0, 43, TelnetFilter.Iac, TelnetFilter.Se], reply);
        // 255 colonne: il byte 255 va raddoppiato anche nella sottonegoziazione.
        Assert.Equal([TelnetFilter.Iac, TelnetFilter.Sb, 31, 0, TelnetFilter.Iac, TelnetFilter.Iac, 0, 24, TelnetFilter.Iac, TelnetFilter.Se],
            TelnetFilter.WindowSizeMessage(255, 24));
    }
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

public sealed class SessionToolsTests
{
    [Fact]
    public void Key_combinations_press_in_order_and_release_in_reverse()
    {
        var strokes = RemoteKeys.CtrlAltDel.Strokes;
        Assert.Equal([0x1D, 0x38, 0x53, 0x53, 0x38, 0x1D], strokes.Select(s => s.ScanCode));
        Assert.Equal([false, false, false, true, true, true], strokes.Select(s => s.KeyUp));
    }

    [Fact]
    public void Key_combinations_fit_a_single_send() =>
        Assert.All(RemoteKeys.Others.Append(RemoteKeys.CtrlAltDel), k => Assert.InRange(k.Strokes.Count, 2, KeyStrokes.MaxPerSend));

    [Fact]
    public void Keys_with_modifiers_wrap_the_key()
    {
        Assert.Equal(
            [new KeyStroke(0x1D, false), new KeyStroke(0x38, false), new KeyStroke(0x1A, false),
             new KeyStroke(0x1A, true), new KeyStroke(0x38, true), new KeyStroke(0x1D, true)],
            KeyStrokes.Key(0x1A, ctrl: true, alt: true));
        Assert.Equal([new KeyStroke(0x1E, false), new KeyStroke(0x1E, true)], KeyStrokes.Key(0x1E));
    }

    [Fact]
    public void Batches_never_split_a_character()
    {
        var characters = Enumerable.Range(0, 7).Select(i => KeyStrokes.Key(0x10 + i, shift: i % 2 == 0)).ToList();
        var batches = KeyStrokes.Batch(characters).ToList();
        Assert.All(batches, b => Assert.InRange(b.Count, 1, KeyStrokes.MaxPerSend));
        Assert.Equal(characters.SelectMany(c => c), batches.SelectMany(b => b));
        Assert.All(batches, b => Assert.Equal(b.Count(s => !s.KeyUp), b.Count(s => s.KeyUp)));
    }

    [Fact]
    public void Typing_turns_line_breaks_into_enter_and_drops_controls()
    {
        var keys = ClipboardTyping.Plan("a\r\nb\nc\rd\te\u0007è");
        var expected = new[]
        {
            new TypedKey('a', 0), new TypedKey('\0', TypedKey.Return), new TypedKey('b', 0), new TypedKey('\0', TypedKey.Return),
            new TypedKey('c', 0), new TypedKey('\0', TypedKey.Return), new TypedKey('d', 0), new TypedKey('\0', TypedKey.Tab),
            new TypedKey('e', 0), new TypedKey('è', 0),
        };
        Assert.Equal(expected, keys);
    }

    [Theory]
    [InlineData(100u, 1, 125u)]
    [InlineData(150u, -1, 125u)]
    [InlineData(140u, 1, 150u)]
    [InlineData(140u, -1, 125u)]
    [InlineData(300u, 1, null)]
    [InlineData(100u, -1, null)]
    public void Zoom_steps_between_levels(uint current, int direction, uint? expected) =>
        Assert.Equal(expected, DisplayScale.Step(current, direction));

    [Theory]
    [InlineData(100u, 100u)]
    [InlineData(150u, 140u)]
    [InlineData(200u, 180u)]
    public void Device_scale_follows_desktop_scale(uint desktop, uint device) =>
        Assert.Equal(device, DisplayScale.DeviceScale(desktop));

    [Fact]
    public void File_names_are_safe()
    {
        var now = new DateTime(2026, 10, 30, 8, 5, 9, DateTimeKind.Local);
        Assert.Equal("SRV_01 _ dc_ 2026-10-30 080509.png", FileNames.For("SRV/01 : dc?", "png", now));
        Assert.Equal("sessione 2026-10-30 080509.txt", FileNames.For(" .. ", "txt", now));
    }
}

public sealed class WebProxyTests
{
    [Fact]
    public void Routes_the_asset_through_the_tunnel_and_the_rest_directly()
    {
        var route = WebRoutes.For(new TargetInfo("vcenter.cliente.local", 443), SessionEndpoint.ForTunnel(50123));
        Assert.Equal(new ProxyRoute("127.0.0.1", 50123), route("VCENTER.cliente.local.", 443));
        Assert.Null(route("vcenter.cliente.local", 5480));
        Assert.Equal(new ProxyRoute("fonts.example.com", 443), route("fonts.example.com", 443));
    }

    [Fact]
    public void Direct_connections_reach_other_ports_of_the_same_host()
    {
        var route = WebRoutes.For(new TargetInfo("192.168.10.120", 443), SessionEndpoint.ForTarget(new TargetInfo("192.168.10.120", 443)));
        Assert.Equal(new ProxyRoute("192.168.10.120", 443), route("192.168.10.120", 443));
        Assert.Equal(new ProxyRoute("192.168.10.120", 80), route("192.168.10.120", 80));
    }

    [Theory]
    [InlineData("fe80::1", "[FE80:0:0:0:0:0:0:1]", true)]
    [InlineData("10.0.0.1", "10.0.0.2", false)]
    [InlineData("idrac.local", "IDRAC.LOCAL", true)]
    public void Compares_hosts(string a, string b, bool same) => Assert.Equal(same, WebRoutes.SameHost(a, b));

    [Fact]
    public async Task Forwards_bytes_to_the_routed_endpoint_and_blocks_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var echo = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            using var server = await echo.AcceptTcpClientAsync(ct);
            var s = server.GetStream();
            var buffer = new byte[5];
            await s.ReadExactlyAsync(buffer, ct);
            await s.WriteAsync(buffer, ct);
        }, ct);

        await using var proxy = new Socks5Proxy((host, port) => host == "idrac.local" && port == 443 ? new ProxyRoute("127.0.0.1", echoPort) : null);
        proxy.Start();

        using (var client = new System.Net.Sockets.TcpClient())
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, proxy.Port, ct);
            var s = client.GetStream();
            Assert.Equal(0, await Connect(s, "idrac.local", 443, ct));
            await s.WriteAsync("hello"u8.ToArray(), ct);
            var reply = new byte[5];
            await s.ReadExactlyAsync(reply, ct);
            Assert.Equal("hello"u8.ToArray(), reply);
        }

        await echoTask;
        echo.Stop();

        using var blocked = new System.Net.Sockets.TcpClient();
        await blocked.ConnectAsync(System.Net.IPAddress.Loopback, proxy.Port, ct);
        Assert.Equal(2, await Connect(blocked.GetStream(), "other.local", 22, ct));
    }

    /// <summary>Saluto e CONNECT SOCKS5 con nome host; restituisce il codice di risposta.</summary>
    private static async Task<byte> Connect(Stream s, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(new byte[] { 5, 1, 0 }, ct);
        var hello = new byte[2];
        await s.ReadExactlyAsync(hello, ct);
        Assert.Equal(new byte[] { 5, 0 }, hello);
        var name = System.Text.Encoding.ASCII.GetBytes(host);
        await s.WriteAsync((byte[])[5, 1, 0, 3, (byte)name.Length, .. name, (byte)(port >> 8), (byte)port], ct);
        var reply = new byte[10];
        await s.ReadExactlyAsync(reply, ct);
        return reply[1];
    }
}

public sealed class WebLoginScriptTests
{
    [Fact]
    public void Values_are_passed_as_json_strings_not_code()
    {
        const string password = "\"); fetch('https://evil'); (\"</script>";
        var script = WebLoginScript.Fill("iDRAC.local", "admin", password);
        Assert.DoesNotContain(password, script, StringComparison.Ordinal);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(password), script, StringComparison.Ordinal);
        Assert.EndsWith("(\"idrac.local\", \"admin\", " + System.Text.Json.JsonSerializer.Serialize(password) + ", true, \"\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Otp_code_is_passed_as_a_json_string() =>
        Assert.EndsWith("(\"vcsa.iess.local\", \"root\", \"x\", true, \"123456\")", WebLoginScript.Fill("vcsa.iess.local", "root", "x", "123456"), StringComparison.Ordinal);

    [Fact]
    public void Detection_carries_no_credentials() =>
        Assert.EndsWith("(\"fe80::1\", \"\", \"\", false, \"\")", WebLoginScript.Detect("[FE80::1]"), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"both\"", WebLoginFill.Filled)]
    [InlineData("\"user\"", WebLoginFill.UserOnly)]
    [InlineData("\"otp\"", WebLoginFill.Otp)]
    [InlineData("\"host\"", WebLoginFill.OtherHost)]
    [InlineData("\"none\"", WebLoginFill.NoForm)]
    [InlineData("null", WebLoginFill.NoForm)]
    public void Parses_results(string result, WebLoginFill expected) => Assert.Equal(expected, WebLoginScript.Parse(result));
}

public sealed class UpdateTests
{
    [Theory]
    [InlineData("HuduLauncher-1.4.10-full.nupkg", "1.4.10")]
    [InlineData("HuduLauncher-1.4.10-ci.3-delta.nupkg", "1.4.10-ci.3")]
    [InlineData("HuduLauncher-1.4.10.zip", null)]
    [InlineData("HuduLauncher-full.nupkg", null)]
    [InlineData("HuduLauncher-1.4.10-other.nupkg", null)]
    public void Package_version_comes_from_the_file_name(string fileName, string? version) =>
        Assert.Equal(version, PackageFiles.VersionOf(fileName));

    [Theory]
    [InlineData("HuduLauncher-1.4.9-full.nupkg", true)]
    [InlineData("HuduLauncher-1.4.10-ci.3-full.nupkg", true)]
    [InlineData("HuduLauncher-1.4.10-full.nupkg", false)]
    [InlineData("HuduLauncher-1.4.11-full.nupkg", false)]
    [InlineData("altro.nupkg", false)]
    public void Only_older_packages_are_removed(string fileName, bool older) =>
        Assert.Equal(older, PackageFiles.IsOlder(fileName, "1.4.10"));

    [Theory]
    [InlineData("1.3.1", "1.3.2", -1)]
    [InlineData("1.3.2-ci.40", "1.3.2", -1)]
    [InlineData("1.3.2-ci.9", "1.3.2-ci.40", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.3.1+abc", "1.3.1", 0)]
    [InlineData("?", "1.0.0", -1)]
    [InlineData(null, null, 0)]
    public void Compares_versions(string? a, string? b, int expected) => Assert.Equal(expected, Math.Sign(AppVersion.Compare(a, b)));

    [Fact]
    public void Accepts_only_feeds_signed_with_the_embedded_key()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var feed = "{\"Assets\":[]}"u8.ToArray();
        var signature = key.SignData(feed, System.Security.Cryptography.HashAlgorithmName.SHA256);

        Assert.True(UpdateSignature.Verify(feed, signature, publicKey));
        Assert.False(UpdateSignature.Verify("{\"Assets\":[{}]}"u8.ToArray(), signature, publicKey));
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.Verify(feed, other.SignData(feed, System.Security.Cryptography.HashAlgorithmName.SHA256), publicKey));
        Assert.False(UpdateSignature.Verify(feed, signature, null));
        Assert.False(UpdateSignature.Verify(feed, signature, "non-base64!"));
    }
}

public sealed class TnsNamesTests
{
    private static readonly TnsEntry First = new(TnsNames.Alias(1), "127.0.0.1", 50123, "ORCL", IsSid: false);
    private static readonly TnsEntry Second = new(TnsNames.Alias(2), "10.0.0.5", 1521, "LEGACY", IsSid: true);

    private const string Existing = "PROD =\r\n  (DESCRIPTION =\r\n    (ADDRESS = (PROTOCOL = TCP)(HOST = db01)(PORT = 1521))\r\n    (CONNECT_DATA = (SERVICE_NAME = prod))\r\n  )\r\n";

    [Fact]
    public void Appends_the_launcher_block_at_the_end_keeping_the_file()
    {
        var updated = TnsNames.Apply(Existing, [First, Second]);

        Assert.StartsWith(Existing, updated, StringComparison.Ordinal);
        Assert.Contains("HUDULAUNCHER_1 =\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("(HOST = 127.0.0.1)(PORT = 50123)", updated, StringComparison.Ordinal);
        Assert.Contains("(SERVICE_NAME = ORCL)", updated, StringComparison.Ordinal);
        Assert.Contains("(SID = LEGACY)", updated, StringComparison.Ordinal);
        Assert.EndsWith(TnsNames.EndMarker + "\r\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", updated.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(["HUDULAUNCHER_1", "HUDULAUNCHER_2"], TnsNames.ManagedAliases(updated));
    }

    [Fact]
    public void Rewrites_the_block_and_removes_it_without_touching_the_rest()
    {
        var withTwo = TnsNames.Apply(Existing, [First, Second]);
        var withOne = TnsNames.Apply(withTwo, [Second]);
        Assert.Equal(["HUDULAUNCHER_2"], TnsNames.ManagedAliases(withOne));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(withOne, ">>> HuduLauncher"));

        Assert.Equal(Existing, TnsNames.Apply(withOne, []));
    }

    [Fact]
    public void Keeps_unix_line_endings_and_text_after_the_block()
    {
        var unix = "A =\n  (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = a)(PORT = 1)))\n";
        var withBlock = TnsNames.Apply(unix, [First]);
        Assert.DoesNotContain("\r\n", withBlock, StringComparison.Ordinal);

        // Una voce aggiunta a mano dopo il blocco resta al suo posto.
        var edited = withBlock + "\nB =\n  (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = b)(PORT = 2)))\n";
        var cleaned = TnsNames.Apply(edited, []);
        Assert.StartsWith(unix, cleaned, StringComparison.Ordinal);
        Assert.Contains("B =", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("HUDULAUNCHER", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_the_block_restores_the_file_byte_for_byte()
    {
        foreach (var original in new[] { Existing, Existing + "\r\n", "A = (x)\n\n\n", "" })
        {
            Assert.Equal(original, TnsNames.Apply(TnsNames.Apply(original, [First, Second]), []));
        }

        // Senza a capo finale: resta solo l'a capo aggiunto prima del blocco.
        Assert.Equal("A = (x)\r\n", TnsNames.Apply(TnsNames.Apply("A = (x)", [First]), []));
    }

    [Fact]
    public void Removes_blocks_written_by_previous_versions_with_their_blank_line()
    {
        var legacy = Existing + "\r\n# >>> HuduLauncher: voci gestite dal Launcher, si aggiornano da sole (non modificare)\r\n"
                     + TnsNames.Render(First) + TnsNames.EndMarker + "\r\n";
        Assert.Equal(["HUDULAUNCHER_1"], TnsNames.ManagedAliases(legacy));
        Assert.Equal(Existing, TnsNames.Apply(legacy, []));
    }

    [Fact]
    public void Works_on_an_empty_file() =>
        Assert.StartsWith(TnsNames.BeginMarker + "\r\n", TnsNames.Apply("", [First]), StringComparison.Ordinal);

    [Theory]
    [InlineData("ORCL)(HOST=evil")]
    [InlineData("ORCL\r\nEVIL =")]
    [InlineData("")]
    public void Refuses_names_that_would_inject_tns_syntax(string name) =>
        Assert.Throws<ArgumentException>(() => TnsNames.Render(First with { DatabaseName = name }));
}

public sealed class PageHostRoutingTests
{
    [Fact]
    public void Page_name_goes_into_the_tunnel_towards_the_ip()
    {
        var target = new TargetInfo("172.16.27.20", 443);
        var route = WebRoutes.For(target, SessionEndpoint.ForTunnel(52702), "vcsa.iess.local");

        // Il nome della pagina e l'IP vanno entrambi nel tunnel (porta locale), senza DNS.
        Assert.Equal(new ProxyRoute("127.0.0.1", 52702), route("vcsa.iess.local", 443));
        Assert.Equal(new ProxyRoute("127.0.0.1", 52702), route("172.16.27.20", 443));
        // Altri host restano fuori dal tunnel; altre porte dell'asset non passano dal tunnel.
        Assert.Equal(new ProxyRoute("cdn.example.com", 443), route("cdn.example.com", 443));
        Assert.Null(route("vcsa.iess.local", 8080));
    }

    [Fact]
    public void Connection_info_exposes_the_page_host()
    {
        var info = new ConnectionInfo(ConnectionProtocol.Https, "vcsa", "Cliente", "Web", new GatewayInfo("123", "x"), new TargetInfo("172.16.27.20", 443),
            null, "alias", null, Web: new WebOptions("https://vcsa.iess.local/ui"));
        Assert.Equal("vcsa.iess.local", info.PageHost);
        Assert.Equal("172.16.27.20", (info with { Web = null }).PageHost);
    }
}

public sealed class WinboxCommandTests
{
    [Fact]
    public void Tunnel_uses_local_port_user_and_password() =>
        Assert.Equal(["127.0.0.1:50211", "admin", "P@ss word"],
            WinboxCommand.Arguments(SessionEndpoint.ForTunnel(50211), new CredentialInfo("admin", "P@ss word")));

    [Fact]
    public void Default_port_is_omitted_and_missing_credential_opens_login() =>
        Assert.Equal(["192.168.88.1"], WinboxCommand.Arguments(SessionEndpoint.ForTarget(new TargetInfo("192.168.88.1", 8291)), null));
}

public sealed class NewProtocolParsingTests
{
    [Fact]
    public async Task Oracle_response_carries_the_database_and_no_password()
    {
        var json = """
            {"protocol":"Oracle","name":"DB01","company":"Cliente","label":"Oracle",
             "gateway":{"id":"100200300","password":"x"},"target":{"host":"10.0.0.20","port":1521},
             "credential":null,"hostKeyAlias":"launcher-20-primary","warning":null,
             "database":{"name":"ORCL","kind":"sid","username":"system","credentialName":"Oracle DB01"}}
            """;
        var client = new PortalClient(new HttpClient(new StubHandler(json)), new Uri("https://portal.test/"));

        var info = await client.RedeemAsync("ticket", "token", TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionProtocol.Oracle, info.Protocol);
        Assert.Null(info.Credential);
        Assert.Equal(("ORCL", true, "system"), (info.Database!.Name, info.Database.IsSid, info.Database.Username));
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class WindowBoundsTests
{
    private static readonly ScreenArea Laptop = new(0, 0, 1536, 824);
    private static readonly ScreenArea External = new(1536, -200, 2560, 1400);

    [Fact]
    public void Round_trips_and_rejects_garbage()
    {
        var bounds = new WindowBounds(1600, 40, 1400, 900, Maximized: true);
        Assert.Equal(bounds, WindowBounds.Parse(bounds.Serialize()));
        Assert.Null(WindowBounds.Parse("1;2;3"));
        Assert.Null(WindowBounds.Parse("a;b;c;d;max"));
        Assert.Null(WindowBounds.Parse("0;0;50;50;normal"));
        Assert.Null(WindowBounds.Parse(null));
    }

    [Fact]
    public void Keeps_the_position_when_its_screen_is_still_there() =>
        Assert.Equal(new WindowBounds(1700, 0, 1400, 900, false), new WindowBounds(1700, 0, 1400, 900, false).FitTo([Laptop, External]));

    [Fact]
    public void Ignores_a_position_on_a_monitor_that_is_gone() =>
        Assert.Null(new WindowBounds(1700, 0, 1400, 900, false).FitTo([Laptop]));

    [Fact]
    public void Shrinks_to_a_smaller_screen_and_refuses_title_bar_off_screen()
    {
        Assert.Equal(new WindowBounds(10, 10, 1536, 824, true), new WindowBounds(10, 10, 2000, 1200, true).FitTo([Laptop]));
        Assert.Null(new WindowBounds(100, -400, 800, 600, false).FitTo([Laptop]));
    }
}
