using Microsoft.EntityFrameworkCore;
using Municode.Common.Models;
using System.Text;

namespace Municode.Common.Data;

public partial class WorkspaceDbContext : DbContext
{
    public WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options) : base(options) { }

    protected WorkspaceDbContext(DbContextOptions options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<BrowserSession> BrowserSessions => Set<BrowserSession>();
    public DbSet<BootstrapRecord> BootstrapRecords => Set<BootstrapRecord>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<CliLoginSession> CliLoginSessions => Set<CliLoginSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<ProviderPlan> ProviderPlans => Set<ProviderPlan>();
    public DbSet<ProviderPlanName> ProviderPlanNames => Set<ProviderPlanName>();
    public DbSet<ProviderCredentialCleanup> ProviderCredentialCleanups => Set<ProviderCredentialCleanup>();
    public DbSet<WorkspaceMaintenance> WorkspaceMaintenanceFlags => Set<WorkspaceMaintenance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureGitHubApps(modelBuilder);
        ConfigureDevelopmentWorkspaces(modelBuilder);
        ConfigureDevelopmentWorkspaceGateway(modelBuilder);
        ConfigureBudgets(modelBuilder);

        modelBuilder.Entity<WorkspaceMaintenance>(entity =>
        {
            entity.ToTable("workspace_maintenance");
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasData(new WorkspaceMaintenance { Id = WorkspaceMaintenance.SingletonId });
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(x => x.KratosIdentityId).IsUnique();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.KratosIdentityId).HasColumnName("kratos_identity_id");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<BrowserSession>(entity =>
        {
            entity.ToTable("browser_sessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.KratosSessionId).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt, x.IdleExpiresAt });
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BootstrapRecord>(entity =>
        {
            entity.ToTable("bootstrap_records");
            entity.HasKey(x => x.InstallationId);
            entity.HasIndex(x => x.OperationId).IsUnique();
            entity.Property(x => x.State).HasMaxLength(32);
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrganizationMember>(entity =>
        {
            entity.ToTable("organization_members");
            entity.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Role).HasConversion<string>().HasColumnName("role");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasIndex(x => x.KeyPrefix);
            entity.HasIndex(x => new { x.OrganizationId, x.RevokedAt });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.KeyPrefix).HasColumnName("key_prefix");
            entity.Property(x => x.KeyHash).HasColumnName("key_hash");
            entity.Property(x => x.ScopesJson).HasColumnName("scopes_json");
            entity.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<CliLoginSession>(entity =>
        {
            entity.ToTable("cli_login_sessions");
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            entity.Property(x => x.ScopesJson).HasColumnName("scopes_json");
            entity.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
            entity.Property(x => x.Status).IsConcurrencyToken();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            entity.Property(x => x.ExchangedAt).HasColumnName("exchanged_at");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        });

        modelBuilder.Entity<ProviderAccount>(entity =>
        {
            entity.ToTable("provider_accounts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.ProviderType).HasConversion<string>();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderPlan>(entity =>
        {
            entity.ToTable("provider_plans");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.NormalizedName }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.NormalizedName).HasMaxLength(256);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.ProviderAccount).WithMany(x => x.Plans)
                .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderPlanName>(entity =>
        {
            entity.ToTable("provider_plan_names");
            entity.HasKey(x => new { x.OrganizationId, x.NormalizedName });
            entity.Property(x => x.NormalizedName).HasMaxLength(256);
            // No plan FK: claims intentionally outlive deleted plans.
        });

        modelBuilder.Entity<ProviderCredentialCleanup>(entity =>
        {
            entity.ToTable("provider_credential_cleanups");
            entity.HasKey(x => x.Id);
        });

        UseSnakeCaseColumns(modelBuilder);
    }

    private static void UseSnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (char.IsUpper(character))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
