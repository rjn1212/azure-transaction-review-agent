using Microsoft.EntityFrameworkCore;

namespace TxnAgent.Data;

public class TxnDbContext(DbContextOptions<TxnDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.HomeCountry).HasMaxLength(2);
            e.Property(x => x.CardStatus).HasMaxLength(20);
            e.Property(x => x.AvgMonthlySpend).HasPrecision(12, 2);
        });
        b.Entity<Merchant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Mcc).HasMaxLength(4);
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.ChargebackRate).HasPrecision(6, 4);
        });
        b.Entity<Transaction>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Country).HasMaxLength(2);
            e.HasIndex(x => new { x.CustomerId, x.OccurredAt });
        });
        b.Entity<Alert>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ExpectedDecision).HasMaxLength(20);
        });
        b.Entity<PolicyRule>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(10);
            e.Property(x => x.Text).HasMaxLength(500);
        });
        b.Entity<AgentRun>(e =>
        {
            e.Property(x => x.Model).HasMaxLength(50);
            e.Property(x => x.Decision).HasMaxLength(20);
            e.Property(x => x.FinalDecision).HasMaxLength(20);
            e.Property(x => x.OverriddenBy).HasMaxLength(100);
            e.Property(x => x.CostUsd).HasPrecision(10, 6);
        });
        b.Entity<AgentStep>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
        });
    }
}
