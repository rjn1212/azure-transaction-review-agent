using Microsoft.EntityFrameworkCore;

namespace TxnAgent.Data;

/// <summary>
/// Creates/migrates the schema and loads synthetic seed data on first run.
/// All data is fake. Deterministic (fixed random seed) so runs are repeatable.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(TxnDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.GetMigrations().Any())
            await db.Database.MigrateAsync(ct);
        else
            await db.Database.EnsureCreatedAsync(ct);

        if (await db.Customers.AnyAsync(ct)) return;
        Seed(db, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    public static void Seed(TxnDbContext db, DateTime now)
    {
        var rng = new Random(42);

        db.PolicyRules.AddRange(
            new PolicyRule { Code = "R1", Text = "Approve if amount is within 2x the customer's typical single-transaction size, in the home country, at a merchant with RiskScore < 40." },
            new PolicyRule { Code = "R2", Text = "Hold if amount exceeds 5x the customer's typical single-transaction size AND the transaction country differs from the customer's home country." },
            new PolicyRule { Code = "R3", Text = "Hold if the merchant RiskScore >= 80 or ChargebackRate >= 2%." },
            new PolicyRule { Code = "R4", Text = "Hold if there are 3 or more transactions in different countries within 24 hours (velocity)." },
            new PolicyRule { Code = "R5", Text = "Escalate to a human when signals conflict or evidence is insufficient to apply R1-R4 with confidence." },
            new PolicyRule { Code = "R6", Text = "Hold if the card status is not Active (never approve)." });

        string[] first = ["Ava", "Ben", "Chloe", "Dev", "Elena", "Frank", "Grace", "Hiro", "Isla", "Jon", "Kara", "Liam", "Maya", "Noah", "Omar"];
        string[] last = ["Adams", "Brooks", "Chen", "Diaz", "Evans", "Fischer", "Garcia", "Hughes", "Ito", "Jones"];
        var customers = Enumerable.Range(0, 50).Select(i => new Customer
        {
            Name = $"{first[i % first.Length]} {last[(i * 7) % last.Length]}",
            HomeCountry = i % 10 == 0 ? "CA" : "US",
            OpenedOn = DateOnly.FromDateTime(now.AddDays(-rng.Next(60, 3650))),
            AvgMonthlySpend = rng.Next(800, 6000),
            CardStatus = i == 49 ? "Suspended" : "Active",
        }).ToList();
        db.Customers.AddRange(customers);

        var merchants = new List<Merchant>
        {
            new() { Name = "Corner Grocery", Mcc = "5411", Category = "Grocery", RiskScore = 5, ChargebackRate = 0.001m },
            new() { Name = "City Fuel", Mcc = "5541", Category = "Fuel", RiskScore = 10, ChargebackRate = 0.002m },
            new() { Name = "Metro Coffee", Mcc = "5814", Category = "Restaurants", RiskScore = 5, ChargebackRate = 0.001m },
            new() { Name = "BigBox Retail", Mcc = "5311", Category = "Department store", RiskScore = 15, ChargebackRate = 0.004m },
            new() { Name = "SkyHigh Airlines", Mcc = "4511", Category = "Airlines", RiskScore = 30, ChargebackRate = 0.008m },
            new() { Name = "Harbor Hotel", Mcc = "7011", Category = "Lodging", RiskScore = 25, ChargebackRate = 0.006m },
            new() { Name = "GadgetZone Online", Mcc = "5732", Category = "Electronics", RiskScore = 45, ChargebackRate = 0.012m },
            new() { Name = "QuickCash Crypto", Mcc = "6051", Category = "Crypto / quasi-cash", Country = "MT", RiskScore = 88, ChargebackRate = 0.031m },
            new() { Name = "LuxWatch Boutique", Mcc = "5944", Category = "Jewelry", Country = "FR", RiskScore = 60, ChargebackRate = 0.015m },
            new() { Name = "StreamFlix", Mcc = "4899", Category = "Subscriptions", RiskScore = 10, ChargebackRate = 0.003m },
        };
        db.Merchants.AddRange(merchants);
        var everyday = merchants.Take(4).Concat([merchants[9]]).ToList();

        // ~40 transactions of normal history per customer over the last 60 days.
        foreach (var c in customers)
        {
            for (int i = 0; i < 40; i++)
            {
                var m = everyday[rng.Next(everyday.Count)];
                db.Transactions.Add(new Transaction
                {
                    Customer = c, Merchant = m,
                    Amount = Math.Round((decimal)(rng.NextDouble() * (double)c.AvgMonthlySpend / 15) + 5, 2),
                    Channel = m.Name == "StreamFlix" ? "Online" : "CardPresent",
                    Country = c.HomeCountry,
                    OccurredAt = now.AddDays(-rng.Next(1, 60)).AddMinutes(-rng.Next(0, 1440)),
                });
            }
        }

        // Flagged transactions with deliberate patterns.
        // Labelled with the decision the policy implies, so agent accuracy can be measured.
        var scenarios = new (int cust, int merch, decimal mult, string country, string channel, string reason, string expected)[]
        {
            (0, 3, 1.5m, "", "CardPresent", "Amount above 30-day average", "APPROVE"),          // R1
            (1, 2, 1.2m, "", "CardPresent", "New device fingerprint", "APPROVE"),               // R1
            (2, 7, 12m, "MT", "Online", "High-risk MCC, foreign country", "HOLD"),              // R2, R3
            (3, 8, 9m, "FR", "CardPresent", "Large foreign jewelry purchase", "HOLD"),          // R2
            (4, 4, 4m, "GB", "Online", "Travel purchase, foreign country", "ESCALATE"),         // foreign but < 5x: no rule fits -> R5
            (5, 6, 4m, "", "Online", "Electronics spike", "ESCALATE"),                          // above R1, below R2, mid-risk merchant
            (49, 0, 1m, "", "CardPresent", "Transaction on suspended card", "HOLD"),            // R6
            (10, 5, 3m, "US", "CardPresent", "Cross-border for CA customer", "ESCALATE"),       // foreign but below R2 threshold
        };
        foreach (var (ci, mi, mult, country, channel, reason, expected) in scenarios)
        {
            var c = customers[ci];
            var t = new Transaction
            {
                Customer = c, Merchant = merchants[mi],
                // Normal history averages ~AvgMonthlySpend/30 per transaction, so mult ~= "x typical".
                Amount = Math.Round(c.AvgMonthlySpend / 30 * mult, 2),
                Channel = channel,
                Country = country == "" ? c.HomeCountry : country,
                OccurredAt = now.AddMinutes(-rng.Next(5, 600)),
            };
            db.Transactions.Add(t);
            db.Alerts.Add(new Alert { Transaction = t, Reason = reason, CreatedAt = t.OccurredAt.AddMinutes(1), ExpectedDecision = expected });
        }

        // Velocity pattern for customer 6: three countries in 24h (R4).
        var c6 = customers[6];
        foreach (var (ctry, hrs) in new[] { ("US", 20), ("MX", 9), ("BR", 1) })
        {
            var t = new Transaction { Customer = c6, Merchant = merchants[3], Amount = 180, Channel = "CardPresent", Country = ctry, OccurredAt = now.AddHours(-hrs) };
            db.Transactions.Add(t);
            if (ctry == "BR") db.Alerts.Add(new Alert { Transaction = t, Reason = "Velocity: multiple countries", CreatedAt = t.OccurredAt.AddMinutes(1), ExpectedDecision = "HOLD" }); // R4
        }
    }
}
