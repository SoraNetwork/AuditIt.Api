using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Models;

namespace AuditIt.Api.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Warehouse> Warehouses { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<ItemDefinition> ItemDefinitions { get; set; }
        public DbSet<Item> Items { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Item>()
                .HasIndex(i => i.ShortId) // Index the external barcode for fast lookups
                .IsUnique(false); 
        }
    }
}
