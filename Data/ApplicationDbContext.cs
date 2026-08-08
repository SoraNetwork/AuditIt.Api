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
        public DbSet<QuickRemark> QuickRemarks { get; set; }

        public DbSet<Renter> Renters { get; set; }
        public DbSet<Rental> Rentals { get; set; }
        public DbSet<RentalItem> RentalItems { get; set; }
        public DbSet<RentalShipment> RentalShipments { get; set; }
        public DbSet<RentalShipmentItem> RentalShipmentItems { get; set; }
        public DbSet<ItemListing> ItemListings { get; set; }
        public DbSet<Reminder> Reminders { get; set; }
        public DbSet<ShipmentReminderSettings> ShipmentReminderSettings { get; set; }
        public DbSet<ShipmentReminderDispatch> ShipmentReminderDispatches { get; set; }
        public DbSet<SettlementSetting> SettlementSettings { get; set; }

        public DbSet<Role> Roles { get; set; }
        public DbSet<Permission> Permissions { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Item>()
                .HasQueryFilter(i => !i.IsDeleted);

            modelBuilder.Entity<Item>()
                .HasIndex(i => i.ShortId)
                .IsUnique(false);

            modelBuilder.Entity<Item>()
                .HasIndex(i => i.SerialNumber)
                .IsUnique()
                .HasFilter("\"SerialNumber\" IS NOT NULL");

            modelBuilder.Entity<Renter>()
                .HasIndex(r => r.Phone);

            modelBuilder.Entity<Rental>()
                .HasIndex(r => r.RentalNumber)
                .IsUnique();

            modelBuilder.Entity<Rental>()
                .HasIndex(r => r.Status);

            modelBuilder.Entity<RentalItem>()
                .HasIndex(ri => ri.RentalId);

            modelBuilder.Entity<RentalItem>()
                .HasIndex(ri => ri.ItemId);

            modelBuilder.Entity<RentalShipment>()
                .HasIndex(s => s.RentalId);

            modelBuilder.Entity<RentalShipment>()
                .HasIndex(s => s.TrackingNumber);

            modelBuilder.Entity<RentalShipmentItem>()
                .HasKey(link => new { link.RentalShipmentId, link.RentalItemId });

            modelBuilder.Entity<RentalShipmentItem>()
                .HasOne(link => link.RentalShipment)
                .WithMany(shipment => shipment.RentalItems)
                .HasForeignKey(link => link.RentalShipmentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RentalShipmentItem>()
                .HasOne(link => link.RentalItem)
                .WithMany(item => item.ShipmentLinks)
                .HasForeignKey(link => link.RentalItemId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ItemListing>()
                .HasIndex(l => l.ItemId);

            modelBuilder.Entity<Reminder>()
                .HasIndex(r => new { r.TargetUser, r.DismissedAt, r.DueAt });

            modelBuilder.Entity<Reminder>()
                .HasIndex(r => new { r.RelatedEntityType, r.RelatedEntityId, r.Type });

            modelBuilder.Entity<ShipmentReminderSettings>()
                .Property(settings => settings.VoiceSendHour)
                .HasDefaultValue(12);

            modelBuilder.Entity<ShipmentReminderSettings>()
                .Property(settings => settings.VoiceSendMinute)
                .HasDefaultValue(30);

            modelBuilder.Entity<ShipmentReminderDispatch>()
                .HasIndex(dispatch => new
                {
                    dispatch.RentalId,
                    dispatch.RecipientMobile,
                    dispatch.Channel,
                    dispatch.BusinessDate
                })
                .IsUnique();

            modelBuilder.Entity<ShipmentReminderDispatch>()
                .HasIndex(dispatch => new { dispatch.BusinessDate, dispatch.Status });

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Name)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasIndex(u => u.DingTalkUserId)
                .IsUnique()
                .HasFilter("\"DingTalkUserId\" IS NOT NULL");

            modelBuilder.Entity<Role>()
                .HasIndex(r => r.Name)
                .IsUnique();

            modelBuilder.Entity<Permission>()
                .HasIndex(p => p.Code)
                .IsUnique();

            modelBuilder.Entity<RolePermission>()
                .HasKey(rp => new { rp.RoleId, rp.PermissionId });

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Permission)
                .WithMany(p => p.RolePermissions)
                .HasForeignKey(rp => rp.PermissionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserRole>()
                .HasKey(ur => new { ur.UserId, ur.RoleId });

            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
