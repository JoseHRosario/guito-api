using GuitoApi.Infrastructure.AI;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services.BankTransactions;

namespace GuitoApi.Tests;

/// <summary>
/// Direct sync-service tests for the Jev suggestion flow (issue #112, ADR-0014) at
/// seams the HTTP boundary tests cannot reach: the empty-category-list fail-open,
/// the blank-remittance skip, and the one-decision-request-per-row shape.
/// </summary>
public class BankTransactionSyncSuggestionTests
{
    private static readonly CategorySummary[] SeededCategories =
    [
        new(1, "Restaurants", null),
        new(2, "Groceries", null),
    ];

    private static BankAccountSummary LinkedAccount() => new()
    {
        Uid = "uid-1",
        SessionId = "sess-1",
        Name = "Main",
        Currency = "EUR",
        AspspCountry = "PT",
        AspspName = "CGD",
        ConsentStatus = "VALID",
    };

    private static BankTransactionSourcePage OneBookedDebitPage() => new(
        [
            new BankTransactionSource(
                TransactionId: "tx-1", EntryReference: "ref-1", BookingDate: new DateOnly(2026, 10, 5),
                Amount: -9.20m, Currency: "EUR", CreditDebitIndicator: "DBIT", Status: "BOOK",
                RemittanceInformation: "COMPRA 9166 MEO", Note: null, CounterpartyName: null),
        ],
        ContinuationKey: null);

    private static FakeCategoriesRepository SeededMirror()
    {
        var repository = new FakeCategoriesRepository();
        repository.Categories.AddRange(SeededCategories);
        return repository;
    }

    private static SyncBankTransactionsService BuildService(
        FakeBankTransactionProvider provider, FakeBankTransactionRepository transactions,
        FakeCategoriesRepository categories, FakeOpenRouterHttpHandler openRouter)
    {
        return new SyncBankTransactionsService(
            provider,
            new FakeBankAccountRepository { Accounts = { LinkedAccount() } },
            transactions,
            categories,
            new OpenRouterExpenseExtractionRepository(
                Microsoft.Extensions.Options.Options.Create(TestSheetsConfiguration.Build()),
                new FakeSecretsProvider(),
                new FakeOpenRouterClientProvider(openRouter)));
    }

    [Fact]
    public async Task Sync_ShouldFailOpenWithoutJevCall_WhenTheCategoryListIsEmpty()
    {
        // Arrange — an unseeded (empty) categories table: there is nothing to choose
        // from, so no Jev call and no suggestion — never a default.
        var provider = new FakeBankTransactionProvider();
        provider.Pages.Enqueue(OneBookedDebitPage());
        var transactions = new FakeBankTransactionRepository();
        var openRouter = new FakeOpenRouterHttpHandler();
        var service = BuildService(provider, transactions, new FakeCategoriesRepository(), openRouter);

        // Act
        var result = await service.SyncAsync();

        // Assert
        Assert.Equal(1, result.New);
        Assert.Empty(openRouter.DecisionRequestBodies);
        Assert.Empty(transactions.SuggestionUpdates);
    }

    [Fact]
    public async Task Sync_ShouldSkipJevForBlankRemittance_AndSuggestTheRest()
    {
        // Arrange — a BOOK/DBIT row with no remittance has nothing to decide on: it is
        // stored without a suggestion (NULL) while the other new row gets its call.
        var provider = new FakeBankTransactionProvider();
        provider.Pages.Enqueue(new BankTransactionSourcePage(
            [
                new BankTransactionSource("tx-1", "ref-1", new DateOnly(2026, 10, 5), -9.20m, "EUR", "DBIT", "BOOK",
                    RemittanceInformation: "COMPRA 9166 MEO", Note: null, CounterpartyName: null),
                new BankTransactionSource("tx-2", "ref-2", new DateOnly(2026, 10, 5), -3.10m, "EUR", "DBIT", "BOOK",
                    RemittanceInformation: null, Note: null, CounterpartyName: null),
            ],
            ContinuationKey: null));
        var transactions = new FakeBankTransactionRepository();
        var openRouter = new FakeOpenRouterHttpHandler();
        var service = BuildService(provider, transactions, SeededMirror(), openRouter);

        // Act
        var result = await service.SyncAsync();

        // Assert
        Assert.Equal(2, result.New);
        Assert.Single(openRouter.DecisionRequestBodies);
        // The blank-remittance row gets no suggestion write at all — it stays NULL.
        var update = Assert.Single(transactions.SuggestionUpdates);
        Assert.NotNull(update.CategoryId);
    }

    [Fact]
    public async Task Sync_ShouldSendOneDecisionRequestPerNewRow_WithRemittanceAsState()
    {
        // Arrange — the Decisions API takes one state per request (ADR-0014); two new
        // rows mean two requests, each carrying its own remittance information and the
        // seeded candidates.
        var provider = new FakeBankTransactionProvider();
        provider.Pages.Enqueue(new BankTransactionSourcePage(
            [
                new BankTransactionSource("tx-1", "ref-1", new DateOnly(2026, 10, 5), -9.20m, "EUR", "DBIT", "BOOK",
                    RemittanceInformation: "COMPRA 9166 MEO", Note: null, CounterpartyName: null),
                new BankTransactionSource("tx-2", "ref-2", new DateOnly(2026, 10, 5), -3.10m, "EUR", "DBIT", "BOOK",
                    RemittanceInformation: "PINGO DOCE", Note: null, CounterpartyName: null),
            ],
            ContinuationKey: null));
        var openRouter = new FakeOpenRouterHttpHandler();
        var service = BuildService(
            provider, new FakeBankTransactionRepository(), SeededMirror(), openRouter);

        // Act
        var result = await service.SyncAsync();

        // Assert
        Assert.Equal(2, result.New);
        Assert.Equal(2, openRouter.DecisionRequestBodies.Count);
        Assert.Contains("COMPRA 9166 MEO", openRouter.DecisionRequestBodies[0]);
        Assert.Contains("PINGO DOCE", openRouter.DecisionRequestBodies[1]);
        Assert.Contains("Restaurants", openRouter.DecisionRequestBodies[0]);
        Assert.Contains("Groceries", openRouter.DecisionRequestBodies[0]);
    }
}
