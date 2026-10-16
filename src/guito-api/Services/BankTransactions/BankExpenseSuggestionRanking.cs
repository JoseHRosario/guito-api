using System.Text.RegularExpressions;
using GuitoApi.Model;

namespace GuitoApi.Services.BankTransactions;

public static class BankExpenseSuggestionRanking
{
    public static IReadOnlyList<BankLinkedExpense> Rank(BankTransactionDetail transaction, IEnumerable<BankLinkedExpense> candidates)
    {
        var bankTokens = Tokens(transaction.RemittanceInformation ?? "");
        return candidates.OrderByDescending(expense => Similarity(bankTokens, Tokens(expense.Description)))
            .ThenBy(expense => Math.Abs(DateOnly.FromDateTime(expense.BookingDate.DateTime).DayNumber - transaction.BookingDate.DayNumber))
            .ThenByDescending(expense => expense.BookingDate)
            .ThenByDescending(expense => expense.Id).ToList();
    }

    private static HashSet<string> Tokens(string description) => Regex.Matches(description.ToUpperInvariant(), @"[\p{L}\p{N}]+")
        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static decimal Similarity(HashSet<string> bank, HashSet<string> expense)
    {
        var union = bank.Union(expense).Count();
        return union == 0 ? 0 : (decimal)bank.Intersect(expense).Count() / union;
    }
}
