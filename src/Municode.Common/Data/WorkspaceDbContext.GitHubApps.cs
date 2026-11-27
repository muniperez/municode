using Microsoft.EntityFrameworkCore;
using Municode.Common.Models;

namespace Municode.Common.Data;

public partial class WorkspaceDbContext
{
    public DbSet<GitHubAppConnection> GitHubAppConnections => Set<GitHubAppConnection>();
    public DbSet<GitHubWebhookReceipt> GitHubWebhookReceipts => Set<GitHubWebhookReceipt>();

    private static void ConfigureGitHubApps(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GitHubAppConnection>(entity =>
        {
            entity.ToTable("github_app_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GitHubWebhookReceipt>(entity =>
        {
            entity.ToTable("github_webhook_receipts");
            entity.HasKey(x => new { x.ConnectionId, x.DeliveryId });
            entity.Property(x => x.DeliveryId).HasMaxLength(100);
            entity.HasOne<GitHubAppConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceGitHubAppRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AdvanceGitHubAppRevisions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AdvanceGitHubAppRevisions()
    {
        foreach (var entry in ChangeTracker.Entries<GitHubAppConnection>().Where(x => x.State == EntityState.Modified))
            entry.Entity.Revision++;
    }
}
