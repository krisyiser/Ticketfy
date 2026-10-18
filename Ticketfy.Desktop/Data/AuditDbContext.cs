using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using System;
using System.IO;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Data;

public class AuditDbContext : DbContext
{
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AuditLogEntity>().HasKey(a => a.Id);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            AppDataPaths.EnsureRequiredDirectories();
            optionsBuilder.UseSqlite($"Data Source={AppDataPaths.AuditDatabaseFile};");
        }
    }
}

public sealed class AuditDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "ticketfy.audit.design.db")};Mode=ReadWriteCreate;")
            .Options;
        return new AuditDbContext(options);
    }
}
