using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using TxnAgent.Data;

namespace TxnAgent.Agent;

/// <summary>
/// Read-only tools the model can call. Each returns a small DTO (keeps tokens and cost low).
/// Time windows are anchored to <paramref name="asOfUtc"/> (the flagged transaction's time), so
/// results are the same whether the review runs today or months after the data was seeded.
/// </summary>
public class ReviewTools(TxnDbContext db, DateTime asOfUtc)
{
    public const int MaxRecentTransactions = 10;

    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(GetTransactionAsync, "get_transaction",
            "Gets the flagged transaction for an alert: amount, merchant, channel, country, time, and the customer and merchant ids."),
        AIFunctionFactory.Create(GetCustomerProfileAsync, "get_customer_profile",
            "Gets the customer's profile: tenure, home country, card status, average monthly spend and typical single-transaction amount."),
        AIFunctionFactory.Create(GetRecentTransactionsAsync, "get_recent_transactions",
            $"Gets up to {MaxRecentTransactions} of the customer's transactions in the N days before the flagged transaction (newest first). Includes other transactions up to 24h after it, for velocity checks."),
        AIFunctionFactory.Create(GetMerchantRiskAsync, "get_merchant_risk",
            "Gets the merchant's category, country, risk score (0-100) and chargeback rate."),
        AIFunctionFactory.Create(GetReviewPolicyAsync, "get_review_policy",
            "Gets the active review policy rules (code + text) that decisions must follow."),
    ];

    public record TransactionInfo(int AlertId, long TransactionId, int CustomerId, int MerchantId, string MerchantName,
        decimal Amount, string Currency, string Channel, string Country, DateTime OccurredAtUtc, string AlertReason);

    public async Task<TransactionInfo?> GetTransactionAsync([Description("The alert id")] int alertId)
    {
        return await db.Alerts.AsNoTracking()
            .Where(a => a.Id == alertId)
            .Select(a => new TransactionInfo(a.Id, a.TransactionId, a.Transaction!.CustomerId, a.Transaction.MerchantId,
                a.Transaction.Merchant!.Name, a.Transaction.Amount, a.Transaction.Currency, a.Transaction.Channel,
                a.Transaction.Country, a.Transaction.OccurredAt, a.Reason))
            .FirstOrDefaultAsync();
    }

    public record CustomerProfile(int CustomerId, string HomeCountry, int TenureDays, string CardStatus,
        decimal AvgMonthlySpend, decimal TypicalTransactionAmount, int TransactionsLast60Days);

    public async Task<CustomerProfile?> GetCustomerProfileAsync([Description("The customer id")] int customerId)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId);
        if (c is null) return null;
        var since = asOfUtc.AddDays(-60);
        var amounts = await db.Transactions.AsNoTracking()
            .Where(t => t.CustomerId == customerId && t.OccurredAt >= since && t.OccurredAt < asOfUtc)
            .Select(t => t.Amount).ToListAsync();
        var typical = amounts.Count == 0 ? 0 : Math.Round(amounts.Average(), 2);
        var tenure = DateOnly.FromDateTime(asOfUtc).DayNumber - c.OpenedOn.DayNumber;
        return new CustomerProfile(c.Id, c.HomeCountry, tenure, c.CardStatus, c.AvgMonthlySpend, typical, amounts.Count);
    }

    public record RecentTxn(long Id, DateTime OccurredAtUtc, decimal Amount, string Merchant, string Channel, string Country);

    public async Task<List<RecentTxn>> GetRecentTransactionsAsync(
        [Description("The customer id")] int customerId,
        [Description("Look-back window in days (1-60)")] int days = 30,
        [Description("Transaction id to exclude, usually the flagged one")] long? excludeTransactionId = null)
    {
        days = Math.Clamp(days, 1, 60);
        var since = asOfUtc.AddDays(-days);
        var until = asOfUtc.AddHours(24);
        var exclude = excludeTransactionId ?? -1;
        return await db.Transactions.AsNoTracking()
            .Where(t => t.CustomerId == customerId && t.OccurredAt >= since && t.OccurredAt <= until && t.Id != exclude)
            .OrderByDescending(t => t.OccurredAt)
            .Take(MaxRecentTransactions)
            .Select(t => new RecentTxn(t.Id, t.OccurredAt, t.Amount, t.Merchant!.Name, t.Channel, t.Country))
            .ToListAsync();
    }

    public record MerchantRisk(int MerchantId, string Name, string Mcc, string Category, string Country, int RiskScore, decimal ChargebackRate);

    public async Task<MerchantRisk?> GetMerchantRiskAsync([Description("The merchant id")] int merchantId)
    {
        return await db.Merchants.AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new MerchantRisk(m.Id, m.Name, m.Mcc, m.Category, m.Country, m.RiskScore, m.ChargebackRate))
            .FirstOrDefaultAsync();
    }

    public record Rule(string Code, string Text);

    public async Task<List<Rule>> GetReviewPolicyAsync()
    {
        return await db.PolicyRules.AsNoTracking()
            .Where(r => r.IsActive).OrderBy(r => r.Code)
            .Select(r => new Rule(r.Code, r.Text)).ToListAsync();
    }
}
