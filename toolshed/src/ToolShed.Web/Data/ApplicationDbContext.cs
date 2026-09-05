using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Models;

namespace ToolShed.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Tool> Tools => Set<Tool>();

    public DbSet<ToolPhoto> ToolPhotos => Set<ToolPhoto>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Tool>(entity =>
        {
            entity.HasOne(t => t.Owner)
                .WithMany(u => u.Tools)
                .HasForeignKey(t => t.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => t.Category);
            entity.HasIndex(t => t.IsListed);
        });

        builder.Entity<ToolPhoto>(entity =>
        {
            entity.HasOne(p => p.Tool)
                .WithMany(t => t.Photos)
                .HasForeignKey(p => p.ToolId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(p => p.ToolId);
        });

        builder.Entity<Booking>(entity =>
        {
            entity.HasOne(b => b.Tool)
                .WithMany(t => t.Bookings)
                .HasForeignKey(b => b.ToolId)
                .OnDelete(DeleteBehavior.Cascade);

            // Keep a member's loan history even if their account is removed by an admin;
            // the tool owner still needs to know where the drill went.
            entity.HasOne(b => b.Borrower)
                .WithMany(u => u.Bookings)
                .HasForeignKey(b => b.BorrowerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(b => new { b.ToolId, b.StartDate, b.EndDate });
            entity.HasIndex(b => b.BorrowerId);
            entity.Ignore(b => b.HoldsDates);
            entity.Ignore(b => b.Days);
        });

        builder.Entity<Invitation>(entity =>
        {
            entity.HasOne(i => i.CreatedBy)
                .WithMany()
                .HasForeignKey(i => i.CreatedById)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(i => i.TokenHash).IsUnique();
            entity.HasIndex(i => i.Email);
        });
    }
}
