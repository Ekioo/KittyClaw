using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Data;

public class TodoDbContext : DbContext
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<ColumnProcessor> ColumnProcessors => Set<ColumnProcessor>();
    public DbSet<ColumnExecution> ColumnExecutions => Set<ColumnExecution>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<ChatMessageRow> ChatMessages => Set<ChatMessageRow>();

    private readonly string _dbPath;

    /// <summary>Absolute path of the SQLite file — used as the MigrationGate memoization key.</summary>
    public string DbPath => _dbPath;

    public TodoDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(SqliteConcurrencyInterceptor.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasMany(t => t.Comments).WithOne(c => c.Ticket).HasForeignKey(c => c.TicketId);
            e.HasMany(t => t.Activities).WithOne(a => a.Ticket).HasForeignKey(a => a.TicketId);
            e.HasMany(t => t.Labels).WithMany(l => l.Tickets).UsingEntity("TicketLabels");
        });

        modelBuilder.Entity<Comment>(e =>
        {
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<ActivityEntry>(e =>
        {
            e.HasKey(a => a.Id);
        });

        modelBuilder.Entity<Label>(e =>
        {
            e.HasKey(l => l.Id);
        });

        modelBuilder.Entity<BoardColumn>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.PipelineId, c.Name }).IsUnique();
        });

        modelBuilder.Entity<Pipeline>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Slug).IsUnique();
        });

        modelBuilder.Entity<ColumnProcessor>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.ColumnId).IsUnique();
            e.Ignore(p => p.AvailableSkills);
            e.Ignore(p => p.RecommendedSkills);
            e.Ignore(p => p.RequiredSkills);
            e.Ignore(p => p.Routes);
        });

        modelBuilder.Entity<ColumnExecution>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<Member>(e =>
        {
            e.HasKey(m => m.Id);
        });

        modelBuilder.Entity<ChatMessageRow>(e =>
        {
            e.HasKey(m => m.Id);
        });
    }
}
