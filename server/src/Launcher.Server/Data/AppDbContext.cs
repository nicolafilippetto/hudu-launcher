using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Launcher.Server.Data;

/// <summary>
/// Database SQLite del portale. Gli utenti Identity sono solo gli admin con accesso locale
/// (password + MFA); i tecnici entrano esclusivamente con 365.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<AdminEntry> Admins => Set<AdminEntry>();
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<BrandingAsset> Branding => Set<BrandingAsset>();
    public DbSet<LayoutConfig> LayoutConfigs => Set<LayoutConfig>();

    public DbSet<CachedCompany> Companies => Set<CachedCompany>();
    public DbSet<CachedLayout> Layouts => Set<CachedLayout>();
    public DbSet<CachedAsset> Assets => Set<CachedAsset>();
    public DbSet<CachedPassword> Passwords => Set<CachedPassword>();
    public DbSet<CachedPeer> Peers => Set<CachedPeer>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    public DbSet<AssetSetting> AssetSettings => Set<AssetSetting>();
    public DbSet<ExtraConnection> ExtraConnections => Set<ExtraConnection>();
    public DbSet<CompanySetting> CompanySettings => Set<CompanySetting>();
    public DbSet<Favorite> Favorites => Set<Favorite>();

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AdminEntry>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
        });
        builder.Entity<PortalUser>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.ObjectId }).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.ObjectId).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(256);
        });
        builder.Entity<Setting>().HasKey(x => x.Key);
        builder.Entity<BrandingAsset>().HasKey(x => x.Kind);
        builder.Entity<LayoutConfig>(e =>
        {
            e.HasKey(x => x.LayoutId);
            e.Property(x => x.LayoutId).ValueGeneratedNever();
        });

        builder.Entity<CachedCompany>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.Name);
        });
        builder.Entity<CachedLayout>().Property(x => x.Id).ValueGeneratedNever();
        builder.Entity<CachedAsset>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.CompanyId);
        });
        builder.Entity<CachedPassword>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.CompanyId);
            e.HasIndex(x => x.AssetId);
        });
        builder.Entity<CachedPeer>().HasKey(x => x.Id);
        builder.Entity<SyncState>().Property(x => x.Id).ValueGeneratedNever();

        builder.Entity<AssetSetting>(e =>
        {
            e.HasKey(x => x.AssetId);
            e.Property(x => x.AssetId).ValueGeneratedNever();
        });
        builder.Entity<ExtraConnection>().HasIndex(x => x.AssetId);
        builder.Entity<CompanySetting>(e =>
        {
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyId).ValueGeneratedNever();
        });
        builder.Entity<Favorite>().HasKey(x => new { x.UserEmail, x.AssetId });

        builder.Entity<Ticket>().HasIndex(x => x.ExpiresAt);
        builder.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserEmail, x.At });
        });

        // SQLite non ordina né confronta DateTimeOffset: si salvano come ticks UTC.
        var converter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                }
            }
        }
    }
}
