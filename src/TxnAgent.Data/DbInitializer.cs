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
            CardStatus = i >= 48 ? "Suspended" : "Active",
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
            new() { Name = "PharmaDirect", Mcc = "5912", Category = "Pharmacy", RiskScore = 15, ChargebackRate = 0.003m },
            new() { Name = "AutoParts Express", Mcc = "5533", Category = "Auto parts", RiskScore = 20, ChargebackRate = 0.005m },
            new() { Name = "NightOwl Casino", Mcc = "7995", Category = "Gambling", Country = "GB", RiskScore = 92, ChargebackRate = 0.035m },
            new() { Name = "TechHub Store", Mcc = "5045", Category = "Computers", RiskScore = 35, ChargebackRate = 0.009m },
            new() { Name = "GlobalPay Transfer", Mcc = "6012", Category = "Money transfer", Country = "NG", RiskScore = 95, ChargebackRate = 0.042m },
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
            // === R1: APPROVE — domestic, within 2x typical, merchant risk < 40 ===
            (0,  3, 1.5m,  "",   "CardPresent", "Amount above 30-day average",         "APPROVE"),
            (1,  2, 1.2m,  "",   "CardPresent", "New device fingerprint",              "APPROVE"),
            (7,  0, 1.8m,  "",   "CardPresent", "Slightly elevated grocery bill",      "APPROVE"),
            (8,  1, 1.0m,  "",   "CardPresent", "Routine fuel purchase",               "APPROVE"),
            (9,  2, 1.5m,  "",   "Online",      "Large coffee order for office",       "APPROVE"),
            (11, 9, 0.8m,  "",   "Online",      "Subscription renewal",                "APPROVE"),
            (12, 3, 1.3m,  "",   "CardPresent", "Retail shopping",                     "APPROVE"),
            (13, 4, 1.2m,  "",   "CardPresent", "Domestic airline ticket",             "APPROVE"),
            (14, 5, 1.6m,  "",   "CardPresent", "Weekend hotel stay",                  "APPROVE"),
            (15, 0, 1.1m,  "",   "Online",      "Online grocery delivery",             "APPROVE"),
            (20, 1, 1.4m,  "",   "CardPresent", "Fuel stop on road trip",              "APPROVE"),
            (30, 3, 1.7m,  "",   "CardPresent", "Department store purchase",           "APPROVE"),
            (44, 10, 1.2m, "",   "CardPresent", "Pharmacy pickup",                     "APPROVE"),
            (45, 11, 1.0m, "",   "CardPresent", "Auto parts purchase",                 "APPROVE"),

            // === R2: HOLD — foreign country AND amount > 5x typical ===
            (2,  7, 12m,   "MT", "Online",      "High-risk MCC, foreign country",      "HOLD"),
            (3,  8, 9m,    "FR", "CardPresent", "Large foreign jewelry purchase",       "HOLD"),
            (16, 4, 6m,    "JP", "CardPresent", "Overseas airline ticket",              "HOLD"),
            (17, 5, 7m,    "TH", "Online",      "Expensive resort booking abroad",      "HOLD"),
            (18, 3, 8m,    "DE", "CardPresent", "Luxury retail purchase in Germany",    "HOLD"),
            (19, 0, 6.5m,  "MX", "CardPresent", "Large purchase in Mexico",             "HOLD"),
            (21, 4, 10m,   "AE", "Online",      "Huge airline booking from Dubai",      "HOLD"),
            (22, 1, 5.5m,  "IT", "CardPresent", "High-value fuel purchase in Italy",    "HOLD"),
            (47, 11, 7m,   "BR", "CardPresent", "Expensive auto parts in Brazil",       "HOLD"),

            // === R3: HOLD — merchant risk >= 80 or chargeback rate >= 2% ===
            (23, 7, 2m,    "MT", "Online",      "Crypto exchange purchase",             "HOLD"),
            (24, 7, 0.5m,  "MT", "Online",      "Small crypto transaction",             "HOLD"),
            (25, 12, 3m,   "GB", "Online",      "Online casino deposit",                "HOLD"),
            (26, 14, 1m,   "NG", "Online",      "Money transfer to Nigeria",            "HOLD"),
            (27, 12, 1.5m, "GB", "CardPresent", "Casino visit in London",               "HOLD"),
            (28, 14, 2m,   "NG", "Online",      "International money transfer",         "HOLD"),

            // === R5: ESCALATE — no rule fully applies ===
            (4,  4, 4m,    "GB", "Online",      "Travel purchase, foreign country",     "ESCALATE"),
            (5,  6, 4m,    "",   "Online",      "Electronics spike",                    "ESCALATE"),
            (10, 5, 3m,    "US", "CardPresent", "Cross-border for CA customer",         "ESCALATE"),
            (35, 4, 3m,    "FR", "Online",      "Mid-range airline ticket in France",   "ESCALATE"),
            (36, 5, 4.5m,  "DE", "CardPresent", "Hotel in Germany near threshold",      "ESCALATE"),
            (37, 6, 3m,    "",   "Online",      "Electronics purchase above average",   "ESCALATE"),
            (38, 0, 3.5m,  "",   "CardPresent", "Elevated grocery spend",               "ESCALATE"),
            (39, 3, 2.5m,  "",   "Online",      "Above-average retail purchase",        "ESCALATE"),
            (40, 8, 2m,    "FR", "CardPresent", "Moderate jewelry purchase abroad",     "ESCALATE"),
            (41, 1, 4m,    "JP", "Online",      "Fuel purchase in Japan",               "ESCALATE"),
            (42, 6, 2.5m,  "",   "CardPresent", "Electronics above threshold",          "ESCALATE"),
            (43, 8, 1.5m,  "",   "CardPresent", "Domestic jewelry purchase",            "ESCALATE"),
            (46, 13, 3m,   "AU", "Online",      "Computer purchase from Australia",     "ESCALATE"),

            // === R6: HOLD — card suspended ===
            (49, 0, 1m,    "",   "CardPresent", "Transaction on suspended card",        "HOLD"),
            (48, 2, 1m,    "",   "CardPresent", "Coffee purchase on suspended card",    "HOLD"),
            (48, 3, 2m,    "",   "Online",      "Online retail on suspended card",      "HOLD"),
        };
        foreach (var (ci, mi, mult, country, channel, reason, expected) in scenarios)
        {
            var c = customers[ci];
            var t = new Transaction
            {
                Customer = c, Merchant = merchants[mi],
                Amount = Math.Round(c.AvgMonthlySpend / 30 * mult, 2),
                Channel = channel,
                Country = country == "" ? c.HomeCountry : country,
                OccurredAt = now.AddMinutes(-rng.Next(5, 600)),
            };
            db.Transactions.Add(t);
            db.Alerts.Add(new Alert { Transaction = t, Reason = reason, CreatedAt = t.OccurredAt.AddMinutes(1), ExpectedDecision = expected });
        }

        // R4: HOLD — velocity patterns (3+ countries in 24h).
        var velocityPatterns = new (int cust, (string country, int hoursAgo)[] legs, string reason)[]
        {
            (6,  [("US", 20), ("MX", 9),  ("BR", 1)],                         "Velocity: multiple countries"),
            (31, [("US", 22), ("MX", 10), ("JP", 2)],                         "Velocity: three countries in 24h"),
            (32, [("US", 20), ("GB", 8),  ("DE", 3)],                         "Velocity: rapid cross-border spend"),
            (33, [("US", 15), ("FR", 7),  ("ES", 1)],                         "Velocity: Europe hopping"),
            (34, [("US", 20), ("MX", 12), ("BR", 4), ("CO", 1)],              "Velocity: four countries in 24h"),
        };
        foreach (var (ci, legs, reason) in velocityPatterns)
        {
            var c = customers[ci];
            foreach (var (ctry, hrs) in legs)
            {
                var t = new Transaction { Customer = c, Merchant = merchants[3], Amount = 180, Channel = "CardPresent", Country = ctry, OccurredAt = now.AddHours(-hrs) };
                db.Transactions.Add(t);
                if (ctry == legs[^1].country)
                    db.Alerts.Add(new Alert { Transaction = t, Reason = reason, CreatedAt = t.OccurredAt.AddMinutes(1), ExpectedDecision = "HOLD" });
            }
        }
    }
}
