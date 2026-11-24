using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Models;

namespace Workspaces.Common.Data;

public partial class WorkspaceDbContext
{
    public DbSet<Budget> Budgets => Set<Budget>();

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
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
