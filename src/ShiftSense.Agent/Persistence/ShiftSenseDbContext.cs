using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ShiftSense.Domain;

namespace ShiftSense.Agent.Persistence;

/// <summary>
/// EF Core mapping for the domain. The domain classes carry no EF attributes; everything is
/// configured here so the Domain project stays persistence-ignorant. SQLite by default,
/// PostgreSQL is a provider swap in <c>Program.cs</c>.
/// </summary>
public sealed class ShiftSenseDbContext(DbContextOptions<ShiftSenseDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<SwapRequest> SwapRequests => Set<SwapRequest>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // HashSet<string> <-> "a,b,c". Sorted on write so the stored form is canonical.
        var setConverter = new ValueConverter<HashSet<string>, string>(
            v => string.Join(',', v.OrderBy(x => x, StringComparer.Ordinal)),
            v => v.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(v.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal));
        var setComparer = new ValueComparer<HashSet<string>>(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SetEquals(b)),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(s))),
            v => new HashSet<string>(v, StringComparer.Ordinal));

        modelBuilder.Entity<Employee>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SkillIds).HasConversion(setConverter, setComparer);
            e.Property(x => x.HourlyRate).HasPrecision(12, 2);
            e.HasIndex(x => x.TeamId);
            e.HasIndex(x => x.ManagerId);
        });

        modelBuilder.Entity<Team>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<Shift>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RequiredSkillIds).HasConversion(setConverter, setComparer);
            e.HasIndex(x => x.EmployeeId);
            e.HasIndex(x => new { x.TeamId, x.Start });
        });

        modelBuilder.Entity<LeaveBalance>(e =>
        {
            e.HasKey(x => new { x.EmployeeId, x.Type });
            e.Property(x => x.Days).HasPrecision(8, 2);
            e.Property(x => x.UsedThisYear).HasPrecision(8, 2);
        });

        modelBuilder.Entity<LeaveRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Days).HasPrecision(8, 2);
            e.HasIndex(x => new { x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<SwapRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Timestamp);
        });
    }
}
