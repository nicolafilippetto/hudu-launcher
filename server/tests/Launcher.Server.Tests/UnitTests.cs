using System.Net;
using System.Security.Cryptography;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Endpoints;
using Launcher.Server.Integrations;
using Launcher.Server.Inventory;
using Launcher.Server.Pages;
using Launcher.Server.Security;
using Launcher.Server.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.Server.Tests;

public sealed class HostParserTests
{
    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10", null, null)]
    [InlineData("192.168.120.11/24", "192.168.120.11", null, null)]
    [InlineData("https://192.168.120.21", "192.168.120.21", null, "https")]
    [InlineData("https://10.0.0.1:8443/login", "10.0.0.1", 8443, "https")]
    [InlineData("srv01.dominio.local", "srv01.dominio.local", null, null)]
    [InlineData("10.0.0.5:2222", "10.0.0.5", 2222, null)]
    [InlineData(" 10.0.0.7, 10.0.0.8 ", "10.0.0.7", null, null)]
    public void Parses_hosts(string input, string host, int? port, string? scheme)
    {
        var parsed = HostParser.Parse(input);
        Assert.NotNull(parsed);
        Assert.Equal(host, parsed.Value.Host);
        Assert.Equal(port, parsed.Value.Port);
        Assert.Equal(scheme, parsed.Value.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("valido!!")]
    [InlineData("<script>")]
    public void Rejects_invalid_hosts(string? input) => Assert.Null(HostParser.Parse(input));
}

public sealed class ConnectionResolverTests
{
    private static ResolutionContext Context(
        IEnumerable<CachedPeer>? peers = null,
        IEnumerable<CachedPassword>? passwords = null,
        AssetSetting? setting = null,
        CompanySetting? company = null,
        Dictionary<int, string>? huduGateways = null,
        IEnumerable<ExtraConnection>? extras = null)
    {
        var peerList = (peers ?? []).ToList();
        var passwordList = (passwords ?? []).ToList();
        return new ResolutionContext
        {
            Companies = new Dictionary<int, CachedCompany> { [1] = new() { Id = 1, Name = "Cliente" } },
            Layouts = new Dictionary<int, LayoutConfig>
            {
                [28] = new() { LayoutId = 28, LayoutName = "Virtual Machines", Enabled = true },
                [9] = new() { LayoutId = 9, LayoutName = "Firewalls", Enabled = true, DefaultProtocol = ConnectionProtocol.Https },
            },
            AssetSettings = setting is null ? new Dictionary<int, AssetSetting>() : new Dictionary<int, AssetSetting> { [setting.AssetId] = setting },
            Extras = (extras ?? []).ToLookup(e => e.AssetId),
            CompanySettings = company is null ? new Dictionary<int, CompanySetting>() : new Dictionary<int, CompanySetting> { [company.CompanyId] = company },
            Passwords = passwordList.ToDictionary(p => p.Id),
            PasswordsByAsset = ConnectionResolver.IndexPasswordsByAsset(passwordList),
            Peers = peerList.ToDictionary(p => p.Id),
            PeersByName = ConnectionResolver.IndexPeersByName(peerList),
            HuduCompanyGateways = huduGateways ?? [],
            Favorites = new HashSet<int>(),
        };
    }

    private static CachedAsset Asset(string name = "SRV01", string? ip = "10.0.0.10", string? os = "Windows Server 2022", int layout = 28) => new()
    {
        Id = 100,
        CompanyId = 1,
        LayoutId = layout,
        LayoutName = "Virtual Machines",
        Name = name,
        PrimaryIp = ip,
        IpamAddresses = ip,
        OperatingSystem = os,
    };

    [Theory]
    [InlineData("Windows Server 2019", ConnectionProtocol.Rdp)]
    [InlineData("Debian 12", ConnectionProtocol.Ssh)]
    [InlineData("VMware ESXi 8", ConnectionProtocol.Ssh)]
    [InlineData(null, ConnectionProtocol.Rdp)]
    public void Guesses_protocol_from_os(string? os, ConnectionProtocol expected) =>
        Assert.Equal(expected, ConnectionResolver.GuessProtocol(os, null, null));

    [Fact]
    public void Layout_default_wins_when_os_unknown() =>
        Assert.Equal(ConnectionProtocol.Https, ConnectionResolver.GuessProtocol(null, new LayoutConfig { LayoutId = 9, LayoutName = "Firewalls", DefaultProtocol = ConnectionProtocol.Https }, null));

    [Fact]
    public void Uses_company_gateway_and_single_linked_password()
    {
        var ctx = Context(
            peers: [new CachedPeer { Id = "111222333", Hostname = "JUMP" }],
            passwords: [new CachedPassword { Id = 5, CompanyId = 1, Name = "admin", Username = "DOM\\admin", AssetId = 100 }],
            company: new CompanySetting { CompanyId = 1, GatewayPeerId = "111222333" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.True(primary.CanConnect);
        Assert.Equal(ConnectionProtocol.Rdp, primary.Protocol);
        Assert.Equal("10.0.0.10", primary.Host);
        Assert.Equal(3389, primary.Port);
        Assert.Equal("111222333", primary.GatewayPeerId);
        Assert.Equal(5, primary.PasswordId);
        Assert.Empty(primary.Issues);
    }

    [Fact]
    public void RustDesk_on_the_asset_itself_targets_localhost()
    {
        var ctx = Context(
            peers: [new CachedPeer { Id = "987654321", Hostname = "SRV01.dominio.local" }],
            company: new CompanySetting { CompanyId = 1, GatewayPeerId = "111" });

        var primary = ConnectionResolver.Resolve(Asset(ip: null), ctx).Primary!;

        Assert.Equal("987654321", primary.GatewayPeerId);
        Assert.Equal("localhost", primary.Host);
        Assert.Equal("RustDesk sull'asset", primary.GatewaySource);
    }

    [Fact]
    public void Fqdn_url_of_the_ipam_sets_protocol_host_and_port()
    {
        var ctx = Context(
            company: new CompanySetting { CompanyId = 1, GatewayPeerId = "111" },
            setting: new AssetSetting { AssetId = 100, Protocol = ConnectionProtocol.Https },
            extras: [new ExtraConnection { Id = 3, AssetId = 100, Label = "VAMI", Protocol = ConnectionProtocol.Https, Url = "https://vcenter.cliente.local:5480/" }]);
        var asset = Asset(os: "VMware Photon OS");
        asset.IpamFqdns = "10.0.0.10 https://vcenter.cliente.local:9443/ui";

        var resolved = ConnectionResolver.Resolve(asset, ctx);
        var primary = resolved.Primary!;
        Assert.Equal(ConnectionProtocol.Https, primary.Protocol);
        Assert.Equal("vcenter.cliente.local", primary.Host);
        Assert.Equal(9443, primary.Port);
        Assert.Equal("https://vcenter.cliente.local:9443/ui", primary.WebUrl);
        Assert.Empty(primary.Issues);

        var vami = resolved.Connections.Single(c => c.Key == "extra:3");
        Assert.Equal(("vcenter.cliente.local", 5480), (vami.Host!, vami.Port));
        Assert.Equal("https://vcenter.cliente.local:5480/", vami.WebUrl);
    }

    [Fact]
    public void Old_handwritten_url_is_ignored_and_default_url_uses_target()
    {
        var ctx = Context(setting: new AssetSetting { AssetId = 100, WebUrl = "https://x.local/" });
        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;
        Assert.Equal(ConnectionProtocol.Rdp, primary.Protocol);
        Assert.Null(primary.WebUrl);

        var https = ConnectionResolver.Resolve(Asset(), Context(setting: new AssetSetting { AssetId = 100, Protocol = ConnectionProtocol.Https, WebUrl = "https://x.local/" })).Primary!;
        Assert.Equal("https://10.0.0.10/", https.WebUrl);

        var web = ConnectionResolver.Resolve(Asset(), Context(setting: new AssetSetting { AssetId = 100, Protocol = ConnectionProtocol.Https, Port = 8443 })).Primary!;
        Assert.Equal("https://10.0.0.10:8443/", web.WebUrl);
    }

    [Fact]
    public void Manual_settings_override_everything()
    {
        var ctx = Context(
            peers: [new CachedPeer { Id = "987654321", Hostname = "SRV01" }],
            passwords: [new CachedPassword { Id = 7, CompanyId = 1, Name = "root", RelatedAssets = "100" }],
            setting: new AssetSetting { AssetId = 100, Host = "10.9.9.9", Port = 2222, Protocol = ConnectionProtocol.Ssh, GatewayPeerId = "555", PasswordId = 7 });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Equal(("10.9.9.9", 2222, ConnectionProtocol.Ssh, "555", 7), (primary.Host, primary.Port, primary.Protocol, primary.GatewayPeerId, primary.PasswordId));
    }

    [Fact]
    public void Several_linked_passwords_need_a_choice()
    {
        var ctx = Context(
            passwords:
            [
                new CachedPassword { Id = 1, CompanyId = 1, Name = "a", AssetId = 100 },
                new CachedPassword { Id = 2, CompanyId = 1, Name = "b", AssetId = 100 },
            ],
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Null(primary.PasswordId);
        Assert.Contains(primary.Issues, i => i.Contains("sceglierne una", StringComparison.Ordinal));
        Assert.True(primary.CanConnect);
    }

    [Fact]
    public void Password_of_another_company_is_rejected()
    {
        var ctx = Context(
            passwords: [new CachedPassword { Id = 9, CompanyId = 2, Name = "altrui" }],
            setting: new AssetSetting { AssetId = 100, PasswordId = 9 },
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Null(primary.PasswordId);
        Assert.Contains(primary.Issues, i => i.Contains("non collegata", StringComparison.Ordinal));
    }

    [Fact]
    public void Related_password_is_used_when_it_is_the_only_link()
    {
        var ctx = Context(
            passwords:
            [
                new CachedPassword { Id = 1, CompanyId = 1, Name = "condivisa", RelatedAssets = "55,100" },
                new CachedPassword { Id = 2, CompanyId = 1, Name = "di un altro asset", AssetId = 55 },
            ],
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Equal(1, primary.PasswordId);
        Assert.Empty(primary.Issues);
    }

    [Fact]
    public void Assigned_password_wins_over_related_ones()
    {
        var ctx = Context(
            passwords:
            [
                new CachedPassword { Id = 1, CompanyId = 1, Name = "related", RelatedAssets = "100" },
                new CachedPassword { Id = 2, CompanyId = 1, Name = "assegnata", AssetId = 100 },
            ],
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        Assert.Equal(2, ConnectionResolver.Resolve(Asset(), ctx).Primary!.PasswordId);
    }

    [Fact]
    public void Chosen_password_must_be_linked_in_hudu()
    {
        var ctx = Context(
            passwords: [new CachedPassword { Id = 3, CompanyId = 1, Name = "del cliente, non collegata" }],
            setting: new AssetSetting { AssetId = 100, PasswordId = 3 },
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Null(primary.PasswordId);
        Assert.Contains(primary.Issues, i => i.Contains("va collegata in Hudu", StringComparison.Ordinal));
    }

    [Fact]
    public void Relation_to_a_password_of_another_company_is_ignored()
    {
        var ctx = Context(
            passwords: [new CachedPassword { Id = 4, CompanyId = 2, Name = "altrui", RelatedAssets = "100" }],
            huduGateways: new Dictionary<int, string> { [1] = "444" });

        var primary = ConnectionResolver.Resolve(Asset(), ctx).Primary!;

        Assert.Null(primary.PasswordId);
        Assert.Contains("Nessuna credenziale associata", primary.Issues);
    }

    [Theory]
    [InlineData("vcenter.cliente.local", "https://vcenter.cliente.local/", "vcenter.cliente.local")]
    [InlineData("https://vcenter.cliente.local/ui", "https://vcenter.cliente.local/ui", "vcenter.cliente.local")]
    [InlineData("http://nas.cliente.local:5000", "http://nas.cliente.local:5000/", "nas.cliente.local")]
    public void Web_page_uses_the_fqdn_of_the_ipam(string fqdn, string url, string host)
    {
        var asset = Asset(os: null, layout: 9);
        asset.IpamFqdns = "10.0.0.10 " + SyncService.CleanFqdn(fqdn);

        var primary = ConnectionResolver.Resolve(asset, Context(huduGateways: new Dictionary<int, string> { [1] = "444" })).Primary!;

        Assert.Equal((url, host), (primary.WebUrl, primary.Host));
    }

    [Fact]
    public void Fqdn_is_not_used_for_rdp_or_when_ip_is_chosen()
    {
        var asset = Asset();
        asset.IpamFqdns = "10.0.0.10 srv01.cliente.local";
        Assert.Equal("10.0.0.10", ConnectionResolver.Resolve(asset, Context()).Primary!.Host);

        var web = Asset(os: null, layout: 9);
        web.IpamFqdns = "10.0.0.10 https://fw.cliente.local/ui";
        var ctx = Context(
            setting: new AssetSetting { AssetId = 100, Port = 4444, WebAddress = ConnectionResolver.WebAddressIp },
            extras: [new ExtraConnection { Id = 5, AssetId = 100, Label = "Admin", Protocol = ConnectionProtocol.Https, Port = 8443 }]);
        var resolved = ConnectionResolver.Resolve(web, ctx);
        Assert.Equal("https://10.0.0.10:4444/", resolved.Primary!.WebUrl);
        Assert.Equal("https://10.0.0.10:8443/", resolved.Connections.Single(c => c.Key == "extra:5").WebUrl);
    }

    [Fact]
    public void Web_login_proposes_only_passwords_linked_in_hudu()
    {
        List<CachedPassword> passwords =
        [
            new() { Id = 1, CompanyId = 1, Name = "stesso URL, non collegata", Url = "https://10.0.0.10/" },
            new() { Id = 2, CompanyId = 1, Name = "related", RelatedAssets = "100" },
            new() { Id = 3, CompanyId = 1, Name = "assegnata", AssetId = 100 },
            new() { Id = 4, CompanyId = 2, Name = "altro cliente", RelatedAssets = "100" },
        ];
        var resolved = ConnectionResolver.Resolve(Asset(os: null, layout: 9), Context(passwords: passwords, setting: new AssetSetting { AssetId = 100, PasswordId = 2 }));

        var candidates = Launcher.Server.Connections.WebLoginService.Candidates(resolved, resolved.Primary!, passwords);

        Assert.Equal([2, 3], candidates.Select(c => c.Id));
    }

    [Theory]
    [InlineData("https://admin:segreta@fw.cliente.local/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://nas.cliente.local")]
    [InlineData("<script>")]
    [InlineData("")]
    public void Unsafe_fqdn_values_are_dropped(string value) => Assert.Null(SyncService.CleanFqdn(value));

    [Fact]
    public void Missing_ip_and_gateway_block_the_connection()
    {
        var primary = ConnectionResolver.Resolve(Asset(ip: null), Context()).Primary!;
        Assert.False(primary.CanConnect);
        Assert.Contains(primary.Issues, i => i.StartsWith("Indirizzo IP mancante", StringComparison.Ordinal));
        Assert.Contains("Nessun gateway RustDesk", primary.Issues);
    }

    [Fact]
    public void Console_and_extra_connections_are_added()
    {
        var asset = Asset();
        asset.ConsoleAddress = "https://10.0.0.200:8443";
        var ctx = Context(
            company: new CompanySetting { CompanyId = 1, GatewayPeerId = "111" },
            extras: [new ExtraConnection { Id = 3, AssetId = 100, Label = "Web", Protocol = ConnectionProtocol.Https, Port = 5001 }]);

        var resolved = ConnectionResolver.Resolve(asset, ctx);

        var console = Assert.Single(resolved.Connections, c => c.Key == ConnectionResolver.ConsoleKey);
        Assert.Equal(("10.0.0.200", 8443), (console.Host, console.Port));
        var extra = Assert.Single(resolved.Connections, c => c.Key == "extra:3");
        Assert.Equal(("10.0.0.10", 5001, "111"), (extra.Host, extra.Port, extra.GatewayPeerId));
    }

    [Fact]
    public void Ambiguous_peer_names_are_not_matched()
    {
        var index = ConnectionResolver.IndexPeersByName(
        [
            new CachedPeer { Id = "1", Hostname = "SERVER" },
            new CachedPeer { Id = "2", Hostname = "server" },
            new CachedPeer { Id = "3", Hostname = "UNICO" },
        ]);
        Assert.False(index.ContainsKey("SERVER"));
        Assert.Equal("3", index["unico"].Id);
    }
}

public sealed class SecurityTests
{
    [Fact]
    public void Local_login_is_refused_through_an_unrecognized_proxy()
    {
        var direct = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        direct.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.10.2");
        Assert.True(NetworkAllowList.IsAllowed("10.0.10.0/24", direct));

        // Il WAF (172.18.0.5) non è tra i proxy fidati: l'intestazione resta e l'IP è quello del WAF.
        var viaUntrusted = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        viaUntrusted.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.18.0.5");
        viaUntrusted.Request.Headers["X-Forwarded-For"] = "203.0.113.50";
        Assert.False(NetworkAllowList.IsAllowed("10.0.10.0/24,172.16.0.0/12", viaUntrusted));

        // Proxy fidato: il middleware ha già sostituito l'IP e conservato l'originale.
        var viaTrusted = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        viaTrusted.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.10.2");
        viaTrusted.Request.Headers["X-Original-For"] = "172.18.0.5:51234";
        Assert.True(NetworkAllowList.IsAllowed("10.0.10.0/24", viaTrusted));
    }

    [Theory]
    [InlineData("172.18.0.5", null)]
    [InlineData(null, "172.18.0.0/16")]
    public void Local_login_is_refused_when_the_trusted_proxy_does_not_send_the_client(string? proxy, string? network)
    {
        var services = new ServiceCollection();
        services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
        {
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            if (proxy is not null)
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            if (network is not null)
            {
                o.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });
        using var provider = services.BuildServiceProvider();

        // Il WAF fidato non invia X-Forwarded-For: l'indirizzo visto è il suo, privato, anche per chi arriva da internet.
        var withoutClient = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider };
        withoutClient.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:172.18.0.5");
        Assert.True(NetworkAllowList.IsTrustedProxyWithoutClient(withoutClient));
        Assert.False(NetworkAllowList.IsAllowed("10.0.0.0/8,172.16.0.0/12,192.168.0.0/16", withoutClient));

        // Con X-Forwarded-For elaborato l'indirizzo è quello del client: ammesso se interno.
        var withClient = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider };
        withClient.Connection.RemoteIpAddress = IPAddress.Parse("10.0.10.2");
        withClient.Request.Headers["X-Original-For"] = "172.18.0.5:51234";
        Assert.False(NetworkAllowList.IsTrustedProxyWithoutClient(withClient));
        Assert.True(NetworkAllowList.IsAllowed("10.0.0.0/8,172.16.0.0/12,192.168.0.0/16", withClient));
    }

    [Fact]
    public void Master_key_round_trip_and_tamper_detection()
    {
        var key = new MasterKey(RandomNumberGenerator.GetBytes(32));
        var payload = key.Encrypt("segreto"u8.ToArray(), "ad"u8.ToArray());
        Assert.Equal("segreto"u8.ToArray(), key.Decrypt(payload, "ad"u8.ToArray()));

        payload[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => key.Decrypt(payload, "ad"u8.ToArray()));

        var other = new MasterKey(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => other.Decrypt(key.Encrypt([1, 2, 3], []), []));
    }

    [Fact]
    public void Master_key_must_be_32_bytes() =>
        Assert.Throws<InvalidOperationException>(() => new MasterKey(new byte[16]));

    [Theory]
    [InlineData("10.0.0.0/8, 192.168.1.0/24", "10.20.30.40", true)]
    [InlineData("10.0.0.0/8, 192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("127.0.0.1/32", "::ffff:127.0.0.1", true)]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("", "10.0.0.1", false)]
    public void Network_allow_list(string networks, string ip, bool expected) =>
        Assert.Equal(expected, NetworkAllowList.Contains(networks, IPAddress.Parse(ip)));

    [Theory]
    [InlineData("primary", true)]
    [InlineData("console", true)]
    [InlineData("extra:12", true)]
    [InlineData("extra:abc", false)]
    [InlineData("../etc", false)]
    public void Connection_keys(string key, bool valid) => Assert.Equal(valid, TicketService.IsValidConnectionKey(key));

    [Fact]
    public void Ticket_hash_is_deterministic_and_hides_the_token()
    {
        var hash = TicketService.Hash("abc");
        Assert.Equal(hash, TicketService.Hash("abc"));
        Assert.DoesNotContain("abc", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Branding_accepts_only_raster_images()
    {
        Assert.Equal("image/png", BrandingEndpoints.DetectImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/jpeg", BrandingEndpoints.DetectImageType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(BrandingEndpoints.DetectImageType("<svg onload=alert(1)>"u8));
        Assert.Null(BrandingEndpoints.DetectImageType("<html>"u8));
    }

    [Theory]
    [InlineData("100200300", "100200300")]
    [InlineData("100200300 — JUMP 01", "100200300")]
    [InlineData("  987654321  ", "987654321")]
    public void Gateway_input_is_normalized(string input, string expected)
    {
        var errors = new List<string>();
        Assert.Equal(expected, IndexModel.ParseGateway(input, errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("<script>alert(1)</script>")]
    public void Invalid_gateways_are_rejected(string input)
    {
        var errors = new List<string>();
        Assert.Null(IndexModel.ParseGateway(input, errors));
        Assert.NotEmpty(errors);
    }
}

public sealed class ConnectionOptionTests
{
    [Fact]
    public void Default_rdp_options_redirect_only_the_clipboard()
    {
        var options = RdpOptionCatalog.Resolve(new Dictionary<string, string>(), null);
        Assert.True(options.Clipboard);
        Assert.False(options.Drives || options.Printers || options.SmartCards || options.Ports || options.Devices || options.WebAuthn || options.Microphone);
        Assert.False(options.AdminSession);
        Assert.Equal("none", options.Audio);
        Assert.Equal("remote", options.Keyboard);
        Assert.Equal("resize", options.Display);
        Assert.True(options.Nla);
    }

    [Fact]
    public void Unknown_keys_and_values_are_dropped() =>
        Assert.Equal(["clipboard"], RdpOptionCatalog.Clean(new Dictionary<string, string?>
        {
            ["clipboard"] = "false",
            ["audio"] = "loud",
            ["shell"] = "cmd.exe",
        }).Keys);

    [Fact]
    public void Default_web_options_open_in_the_launcher_and_ask_for_certificates()
    {
        var options = WebOptionCatalog.Resolve("https://10.0.0.5/", new Dictionary<string, string>(), null);
        Assert.Equal("https://10.0.0.5/", options.Url);
        Assert.False(options.External);
        Assert.Equal("ask", options.Certificate);
        Assert.True(options.Popups && options.Downloads);
        Assert.False(options.KeepCookies);
        Assert.Equal(100, options.Zoom);
    }

    [Fact]
    public void Asset_web_options_override_admin_defaults()
    {
        var defaults = new Dictionary<string, string> { ["open"] = "browser", ["certificate"] = "ignore" };
        var options = WebOptionCatalog.Resolve("https://x/", defaults, new Dictionary<string, string> { ["open"] = "launcher" });
        Assert.False(options.External);
        Assert.Equal("ignore", options.Certificate);
    }

    [Theory]
    [InlineData("https://vcenter.cliente.local/ui", "https://vcenter.cliente.local/ui")]
    [InlineData("http://192.168.10.254:8080/", "http://192.168.10.254:8080/")]
    [InlineData(" https://[fe80::1]/ ", "https://[fe80::1]/")]
    public void Accepts_web_urls(string input, string expected)
    {
        var (url, error) = WebUrls.Parse(input);
        Assert.Null(error);
        Assert.Equal(expected, url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("vcenter.cliente.local")]
    [InlineData("ftp://10.0.0.1/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://admin:segreto@10.0.0.1/")]
    public void Rejects_bad_web_urls(string input)
    {
        var (url, error) = WebUrls.Parse(input);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(true, "10.0.0.1", 443, "https://10.0.0.1/")]
    [InlineData(true, "10.0.0.1", 8443, "https://10.0.0.1:8443/")]
    [InlineData(false, "fe80::1", 80, "http://[fe80::1]/")]
    public void Builds_default_web_urls(bool https, string host, int port, string expected) =>
        Assert.Equal(expected, WebUrls.Default(https, host, port));

    [Fact]
    public void Default_highlight_rules_are_valid()
    {
        var (rules, errors) = HighlightRules.Parse(HighlightRules.Default);
        Assert.Empty(errors);
        Assert.True(rules.Count >= 5);
        Assert.Contains(rules, r => r.Color == "red" && System.Text.RegularExpressions.Regex.IsMatch("Interface Gi1/0/1 is DOWN", r.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Highlight_rules_report_bad_lines()
    {
        var (rules, errors) = HighlightRules.Parse("""
            # commento
            #ff8800 \bvlan\d+\b
            pink    foo
            red     (unclosed
            green
            """);
        Assert.Single(rules);
        Assert.Equal("#ff8800", rules[0].Color);
        Assert.Equal(3, errors.Count);
    }
}

public sealed class IpamTests
{
    private static CachedAsset Asset(string? layoutIp, params string[] ipam) => new()
    {
        Id = 7, CompanyId = 1, LayoutId = 5, LayoutName = "VM", Name = "SRV",
        PrimaryIp = layoutIp,
        IpamAddresses = ipam.Length == 0 ? null : string.Join('\n', ipam),
    };

    [Fact]
    public void Ipam_addresses_are_cached_preferred_first()
    {
        var grouped = SyncService.GroupAddresses(
        [
            new HuduIpAddress("fe80::1", "assigned", null, 7, 1),
            new HuduIpAddress("192.168.1.50", "reserved", null, 7, 1),
            new HuduIpAddress("192.168.1.41", "assigned", "ap-01.local", 7, 1),
            new HuduIpAddress("192.168.1.41", "assigned", null, 7, 1),
            new HuduIpAddress("192.168.1.0/24", null, null, 0, 1),
        ]);
        Assert.Equal(["192.168.1.41", "192.168.1.50", "fe80::1"], grouped[7].Select(a => a.Address));

        var config = new LayoutConfig { LayoutId = 5, LayoutName = "Access Point", Enabled = true };
        var asset = new HuduAsset(7, 1, 5, "AP-01", null, false, new Dictionary<string, string?>());
        var cached = SyncService.ToCached(asset, config, "Cliente di prova", grouped[7]);
        Assert.Null(cached.PrimaryIp);
        Assert.Equal(["192.168.1.41", "192.168.1.50", "fe80::1"], cached.IpamList());
        Assert.Equal("ap-01.local", cached.Hostname);
        Assert.Contains("192.168.1.50", cached.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_ipam_address_is_used_even_if_the_layout_field_differs() =>
        Assert.Equal(("10.9.9.9", null), ConnectionResolver.ResolveHost(null, Asset("10.0.0.5", "10.9.9.9")));

    [Fact]
    public void Several_ipam_addresses_need_a_choice_unless_the_layout_field_picks_one()
    {
        var (host, issue) = ConnectionResolver.ResolveHost(null, Asset(null, "10.0.0.1", "10.0.0.2"));
        Assert.Equal("10.0.0.1", host);
        Assert.Contains("sceglierne uno", issue, StringComparison.Ordinal);
        Assert.Equal(("10.0.0.2", null), ConnectionResolver.ResolveHost(null, Asset("10.0.0.2", "10.0.0.1", "10.0.0.2")));
        Assert.Equal(("10.0.0.2", null), ConnectionResolver.ResolveHost("10.0.0.2", Asset(null, "10.0.0.1", "10.0.0.2")));
    }

    [Fact]
    public void Missing_ipam_address_is_reported()
    {
        var (host, issue) = ConnectionResolver.ResolveHost(null, Asset("10.0.0.5"));
        Assert.Equal("10.0.0.5", host);
        Assert.Contains("va caricato", issue, StringComparison.Ordinal);
        Assert.Contains("non più presente", ConnectionResolver.ResolveHost("10.0.0.9", Asset(null, "10.0.0.1")).Issue, StringComparison.Ordinal);
        Assert.Equal((null, null), ConnectionResolver.ResolveHost(null, Asset(null)));
    }
}

public sealed class EntraCredentialTests
{
    [Fact]
    public async Task Assertion_is_signed_with_the_certificate_as_entra_expects()
    {
        var (certificate, key) = EntraCredential.Generate("Hudu Launcher (test)", TimeProvider.System);
        var info = EntraCredential.Describe(certificate)!;
        Assert.True(info.NotAfter > DateTimeOffset.UtcNow.AddYears(1));
        Assert.False(info.ExpiresSoon(TimeProvider.System));

        var tenant = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var assertion = EntraCredential.CreateAssertion(tenant, client, certificate, key, TimeProvider.System);
        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(assertion);
        Assert.Equal("PS256", jwt.Alg);
        Assert.Equal(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.SHA256.HashData(info.RawData)),
            jwt.GetHeaderValue<string>("x5t#S256"));
        Assert.Equal(client, jwt.Issuer);
        Assert.Equal(client, jwt.Subject);
        Assert.Equal($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", Assert.Single(jwt.Audiences));
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromMinutes(5));

        // La firma si verifica con la sola parte pubblica, quella caricata in Entra.
        using var publicCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(info.RawData);
        var result = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ValidateTokenAsync(assertion, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = client,
            ValidAudience = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.X509SecurityKey(publicCertificate),
        });
        Assert.True(result.IsValid, result.Exception?.Message);
    }
}

public sealed class RustDeskOptionalTests
{
    [Theory]
    [InlineData(@"C:\Program Files\RustDesk\rustdesk.exe", true)]
    [InlineData(@"D:\Programmi\Azienda-RustDesk-Client\client.EXE", true)]
    [InlineData(@"rustdesk.exe", false)]
    [InlineData(@"C:\Program Files\RustDesk\rustdesk.bat", false)]
    [InlineData(@"C:\Program Files\..\Users\x\rustdesk.exe", false)]
    [InlineData(@"\\server\share\rustdesk.exe", false)]
    [InlineData(@"C:\Program Files\Rust|Desk\rustdesk.exe", false)]
    public void Client_path_must_be_a_full_windows_exe_path(string path, bool valid) =>
        Assert.Equal(valid, RustDeskSettings.IsValidClientPath(path));

    [Fact]
    public void Without_rustdesk_there_are_no_gateways_and_no_gateway_issues()
    {
        var asset = new CachedAsset { Id = 1, CompanyId = 1, LayoutId = 28, LayoutName = "VM", Name = "SRV01", IpamAddresses = "10.0.0.10", OperatingSystem = "Windows Server 2022" };
        var ctx = new ResolutionContext
        {
            Companies = new Dictionary<int, CachedCompany> { [1] = new() { Id = 1, Name = "Cliente" } },
            Layouts = new Dictionary<int, LayoutConfig>(),
            AssetSettings = new Dictionary<int, AssetSetting>(),
            Extras = Array.Empty<ExtraConnection>().ToLookup(e => e.AssetId),
            CompanySettings = new Dictionary<int, CompanySetting> { [1] = new() { CompanyId = 1, GatewayPeerId = "123456789" } },
            Passwords = new Dictionary<int, CachedPassword>(),
            PasswordsByAsset = ConnectionResolver.IndexPasswordsByAsset([]),
            Peers = new Dictionary<string, CachedPeer> { ["999"] = new() { Id = "999", Hostname = "SRV01" } },
            PeersByName = ConnectionResolver.IndexPeersByName([new CachedPeer { Id = "999", Hostname = "SRV01" }]),
            HuduCompanyGateways = new Dictionary<int, string>(),
            Favorites = new HashSet<int>(),
            RustDeskEnabled = false,
        };
        var primary = ConnectionResolver.Resolve(asset, ctx).Primary!;
        Assert.Null(primary.GatewayPeerId);
        Assert.False(primary.CanConnect);
        Assert.True(primary.CanConnectDirect);
        // Nemmeno RustDesk sull'asset stesso (che porterebbe a "localhost").
        Assert.Equal("10.0.0.10", primary.Host);
        Assert.DoesNotContain(primary.Issues, i => i.Contains("gateway", StringComparison.OrdinalIgnoreCase));
    }
}
