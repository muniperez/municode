using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Models;

namespace Workspaces.Common.Data;

public partial class WorkspaceDbContext
{
    public DbSet<ProviderCharge> ProviderCharges => Set<ProviderCharge>();

    private static void ConfigureProviderCharges(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderCharge>(entity =>
        {
            entity.ToTable("provider_charges");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.ProviderAccountId, x.Period }).IsUnique();
            entity.Property(x => x.Period).HasMaxLength(7);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Source).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(256);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
