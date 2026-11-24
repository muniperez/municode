using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Models;

namespace Workspaces.Common.Data;

public partial class WorkspaceDbContext
{
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetNotification> BudgetNotifications => Set<BudgetNotification>();

    private static void ConfigureBudgets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Budget>(entity =>
        {
            entity.ToTable("budgets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.AlertPeriod).HasMaxLength(7);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.WebhookUrl).HasMaxLength(2048);
            entity.Property(x => x.WebhookSecretProvider).HasMaxLength(32);
            entity.Property(x => x.WebhookSecretPath).HasMaxLength(512);
            entity.Property(x => x.WebhookSecretVersion).HasMaxLength(64);
            entity.Property(x => x.EmailAudience).HasMaxLength(16);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BudgetNotification>(entity =>
        {
            entity.ToTable("budget_notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BudgetId, x.BudgetRevision, x.Period, x.Threshold, x.Channel, x.Recipient }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.Channel });
            entity.Property(x => x.Channel).HasMaxLength(16);
            entity.Property(x => x.Period).HasMaxLength(7);
            entity.Property(x => x.Recipient).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.LastError).HasMaxLength(256);
            entity.HasOne<Budget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureProviderCharges(modelBuilder);
    }
}
