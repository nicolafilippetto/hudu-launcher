using System.Net;
using System.Security.Cryptography;
using Launcher.Server.Connections;
using Launcher.Server.Data;
using Launcher.Server.Endpoints;
using Launcher.Server.Integrations;
using Launcher.Server.Inventory;
using Launcher.Server.Pages;
using Launcher.Server.Security;

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
            PasswordsByAsset = passwordList.Where(p => p.AssetId is not null).ToLookup(p => p.AssetId!.Value),
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
    public void Manual_settings_override_everything()
    {
        var ctx = Context(
            peers: [new CachedPeer { Id = "987654321", Hostname = "SRV01" }],
            passwords: [new CachedPassword { Id = 7, CompanyId = 1, Name = "root" }],
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
        Assert.Contains(primary.Issues, i => i.Contains("non più presente", StringComparison.Ordinal));
    }

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
    public void Assertion_is_signed_with_the_certificate_as_entra_expects()
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
        var result = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ValidateTokenAsync(assertion, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = client,
            ValidAudience = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.X509SecurityKey(publicCertificate),
        }).GetAwaiter().GetResult();
        Assert.True(result.IsValid, result.Exception?.Message);
    }
}
