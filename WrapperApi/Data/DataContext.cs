using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using WrapperApi.Models;

namespace WrapperApi.Data;

public class DataContext : IdentityDbContext<User, Role, string>
{
    public DataContext(DbContextOptions<DataContext> options) : base(options) { }

    public DbSet<Job> Jobs { get; set; }
    public DbSet<Client> Clients { get; set; } = null!;
    public DbSet<ClientSecret> ClientSecrets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>().ToTable("Users");
        builder.Entity<Role>().ToTable("Roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims");
        builder.Entity<IdentityUserToken<string>>().ToTable("UserTokens");
        builder.Entity<Client>().HasIndex(c => c.ClientId).IsUnique();
        builder.Entity<ClientSecret>().HasIndex(s => s.ClientId);
        builder.Entity<Job>(b =>
        {
            b.Property(j => j.InitiatorType).HasDefaultValue(InitiatorType.Unknown);

            b.HasOne(j => j.Client)
            .WithMany(c => c.JobsSubmitted)
            .HasForeignKey(j => j.ClientId)
            .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(j => j.User)
            .WithMany(u => u.JobsSubmitted)
            .HasForeignKey(j => j.UserId)
            .OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(j => j.ClientId);
            b.HasIndex(j => j.UserId);
            b.HasIndex(j => j.InitiatorType);
        });
    }

}