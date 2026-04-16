using Microsoft.EntityFrameworkCore;
using Yastık6.Api.Domain.Entities;

namespace Yastık6.Api.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<PriceRecord> Prices { get; set; } = null!;
    public DbSet<TrackedSymbol> TrackedSymbols { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // EF Core'un temel ayarlarını ezmemek için bunu hep koy

        modelBuilder.Entity<PriceRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => new { e.ProductCode, e.TimestampUtc });

            entity.Property(e => e.TimestampUtc)
                .HasConversion(
                    v => v.UtcDateTime, 
                    v => new DateTimeOffset(v, TimeSpan.Zero)  
                );
        });

        modelBuilder.Entity<TrackedSymbol>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Code).IsUnique();
        });
    }
}