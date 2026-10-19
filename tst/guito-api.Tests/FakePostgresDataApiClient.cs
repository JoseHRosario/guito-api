using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of IPostgresDataApiClient for direct repository tests (issue #88).
/// Scripts result sets/exceptions via NextResults/ExecuteException and records every
/// executed statement (SQL, parameters, transaction id, cancellation token).
/// </summary>
public class FakePostgresDataApiClient : IPostgresDataApiClient
{
    public Queue<PostgresResult> NextResults { get; } = new();
    public Func<string, IReadOnlyList<PostgresParameter>?, PostgresResult>? ExecuteBank { get; set; }
    public bool PersistExpenses { get; set; }
    public bool PersistBankImports { get; set; }
    public Func<string, CancellationToken, Task>? BeforeBankImportAsync { get; set; }
    private readonly Dictionary<string, Dictionary<string, PostgresValue>> _bankImports = [];
    private readonly Dictionary<string, Dictionary<string, PostgresValue>> _bankAccounts = [];
    private readonly Dictionary<long, long?> _bankCategories = [];
    public List<(long Id, string Name, string? Description)> CategoryRows { get; } =
        [(2, "Restaurants", null), (3, "Groceries", null), (2147483648L, "Test", null), (4, "Original category", null)];
    private readonly List<IReadOnlyList<PostgresValue>> _expenses = [];
    private readonly Dictionary<int, long> _revisions = [];
    private readonly SemaphoreSlim _transactionGate = new(1);
    private List<IReadOnlyList<PostgresValue>>? _snapshot;
    private Dictionary<int, long>? _revisionSnapshot;
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }
    public bool HasBankReviewSchema { get; set; }
    public Dictionary<string, string>? LinkSchemaColumns { get; set; }
    public Dictionary<long, (int? ExpenseId, string Status)> LinkedTransactions { get; } = [];
    public Dictionary<long, (string Day, string Amount, string Currency, string? Description)> BankDetails { get; } = [];
    public Dictionary<long, (string Status, string Direction)> BankMovementStates { get; } = [];
    private Dictionary<long, (int? ExpenseId, string Status)>? _linksSnapshot;


    public void SeedBankAccount(string uid, string? identificationHash)
    {
        var values = new Dictionary<string, PostgresValue>
        {
            ["uid"] = PostgresValue.FromString(uid), ["fetch_uid"] = PostgresValue.FromString(uid),
            ["identification_hash"] = identificationHash is null ? PostgresValue.Null() : PostgresValue.FromString(identificationHash),
            ["session_id"] = PostgresValue.FromString("session"), ["iban"] = PostgresValue.Null(),
            ["name"] = PostgresValue.FromString("Main"), ["currency"] = PostgresValue.FromString("EUR"),
            ["aspsp_country"] = PostgresValue.FromString("PT"), ["aspsp_name"] = PostgresValue.FromString("Bank"),
            ["consent_status"] = PostgresValue.FromString("VALID"), ["consent_expires_at"] = PostgresValue.Null()
        };
        _bankAccounts.Add(uid, values);
    }

    public void SeedExpense(int id, string bookingDate, string amount, string category, string creatorEmail = "human@example.com", string description = "Original purchase") =>
        _expenses.Add([PostgresValue.FromString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            PostgresValue.FromString(bookingDate), PostgresValue.FromString(amount), PostgresValue.FromString(description),
            PostgresValue.FromString(category), PostgresValue.FromString(creatorEmail),
            PostgresValue.FromString("2027-02-01T10:00:00Z"), PostgresValue.Null()]);

    public void SeedExpense(IReadOnlyList<PostgresValue> row) => _expenses.Add(row);

    private PostgresResult ExecuteExpense(string sql, IReadOnlyList<PostgresParameter>? parameters)
    {
        var values = parameters!.ToDictionary(p => p.Name, p => p.Value);
        if (sql == "SELECT id FROM categories WHERE name = :name")
            return new PostgresResult([], CategoryRows.Where(category => category.Name == values["name"].StringValue)
                .Select(category => (IReadOnlyList<PostgresValue>)new[] { PostgresValue.FromLong(category.Id) }).ToList(), 0);
        if (sql.StartsWith("DELETE FROM expenses", StringComparison.Ordinal))
        {
            var count = _expenses.RemoveAll(r => (!sql.Contains("id = :id", StringComparison.Ordinal) || int.Parse(r[0].StringValue!) == values["id"].LongValue)
                && (!sql.Contains("revision = :revision", StringComparison.Ordinal) || _revisions.GetValueOrDefault(int.Parse(r[0].StringValue!), 1) == values["revision"].LongValue));
            return new PostgresResult([], [], count);
        }
        if (sql.StartsWith("UPDATE bank_transactions", StringComparison.Ordinal))
        {
            var parameter = values["expense_id"];
            if (HasBankReviewSchema && (parameter.LongValue is null || !sql.Contains("review_status = 'needsReview'", StringComparison.Ordinal)))
                throw new InvalidOperationException("Integer links require typed binding and atomic review-state reset.");
            var id = parameter.LongValue ?? long.Parse(parameter.StringValue!);
            var targets = LinkedTransactions.Where(t => !sql.Contains("WHERE expense_id = :expense_id", StringComparison.Ordinal) || t.Value.ExpenseId == id).ToArray();
            foreach (var target in targets)
                LinkedTransactions[target.Key] = (null, HasBankReviewSchema ? "needsReview" : target.Value.Status);
            return new PostgresResult([], [], targets.Length);
        }
        if (sql.StartsWith("UPDATE expenses", StringComparison.Ordinal))
            return UpdateExpense(sql, values);
        if (sql.StartsWith("SELECT id FROM expenses", StringComparison.Ordinal))
        {
            var rows = _expenses.Where(r => !sql.Contains("WHERE id = :id", StringComparison.Ordinal)
                || int.Parse(r[0].StringValue!) == values["id"].LongValue).Select(r => (IReadOnlyList<PostgresValue>)new[] { r[0] }).ToList();
            return new PostgresResult([], rows, 0);
        }
        if (sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal))
            return InsertExpense(values);
        if (sql.Contains("LEFT JOIN LATERAL", StringComparison.Ordinal))
            return ReadExpenseHistory(sql, values);
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
        {
            return ReadExpenses(sql, values);
        }
        throw new InvalidOperationException("Unsupported expense statement in fake.");
    }
    private PostgresResult ReadExpenses(string sql, Dictionary<string, PostgresValue> values)
    {
        IEnumerable<IReadOnlyList<PostgresValue>> rows = _expenses;
        if (sql.Contains("e.category_id = :category_id", StringComparison.Ordinal))
            rows = rows.Where(r => CategoryRows.Single(category => category.Name == r[4].StringValue).Id == values["category_id"].LongValue);
        if (sql.Contains("e.booking_date >= CAST(:start AS timestamptz)", StringComparison.Ordinal))
            rows = rows.Where(r => DateTimeOffset.Parse(r[1].StringValue!) >= DateTimeOffset.Parse(values["start"].StringValue!));
        if (sql.Contains("e.booking_date < CAST(:end AS timestamptz)", StringComparison.Ordinal))
            rows = rows.Where(r => values["end"].StringValue == "10000-01-01T00:00:00+00:00" || DateTimeOffset.Parse(r[1].StringValue!) < DateTimeOffset.Parse(values["end"].StringValue!));
        if (sql.Contains("ORDER BY e.booking_date DESC, e.id DESC", StringComparison.Ordinal))
            rows = rows.OrderByDescending(r => DateTimeOffset.Parse(r[1].StringValue!))
                .ThenByDescending(r => int.Parse(r[0].StringValue!, System.Globalization.CultureInfo.InvariantCulture));
        if (sql.Contains("LIMIT :limit", StringComparison.Ordinal)) rows = rows.Take((int)values["limit"].LongValue!);
        if (sql.Contains("e.id = :id", StringComparison.Ordinal))
            rows = rows.Where(r => int.Parse(r[0].StringValue!) == values["id"].LongValue);
        if (sql.Contains("CAST(e.revision AS text)", StringComparison.Ordinal))
            rows = rows.Select(r => (IReadOnlyList<PostgresValue>)r.Concat([PostgresValue.FromString(_revisions.GetValueOrDefault(int.Parse(r[0].StringValue!), 1).ToString())]).ToArray());
        return new PostgresResult([], rows.ToList(), 0);
    }

    private PostgresResult UpdateExpense(string sql, Dictionary<string, PostgresValue> values)
    {
        var targets = _expenses.Where(r => (!sql.Contains("id = :id", StringComparison.Ordinal) || int.Parse(r[0].StringValue!) == values["id"].LongValue)
            && (!sql.Contains("revision = :revision", StringComparison.Ordinal) || _revisions.GetValueOrDefault(int.Parse(r[0].StringValue!), 1) == values["revision"].LongValue)).ToArray();
        foreach (var row in targets)
        {
            var updated = row.ToArray();
            var id = int.Parse(row[0].StringValue!);
            updated[1] = values["booking_date"];
            if (!sql.Contains("THEN amount ELSE", StringComparison.Ordinal) || decimal.Parse(row[2].StringValue!) != decimal.Parse(values["amount"].StringValue!)) updated[2] = values["amount"];
            updated[3] = values["description"];
            updated[4] = PostgresValue.FromString(CategoryRows.Single(category => category.Id == values["category_id"].LongValue).Name);
            if (sql.Contains("update_date = clock_timestamp()", StringComparison.Ordinal)) updated[7] = PostgresValue.FromString("2027-02-02T10:00:00.654321Z");
            if (sql.Contains("revision = revision + 1", StringComparison.Ordinal)) _revisions[id] = _revisions.GetValueOrDefault(id, 1) + 1;
            _expenses[_expenses.IndexOf(row)] = updated;
        }
        return new PostgresResult([], [], targets.Length);
    }

    private PostgresResult InsertExpense(Dictionary<string, PostgresValue> values)
    {
        var id = _expenses.Count + 1;
        var category = CategoryRows.Single(category => category.Id == values["category_id"].LongValue).Name;
        var now = PostgresValue.FromString(DateTimeOffset.UtcNow.ToString("O"));
        _expenses.Add([PostgresValue.FromString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            values["booking_date"], values["amount"], values["description"],
            PostgresValue.FromString(category), values["creator_email"], now, PostgresValue.Null()]);
        return new PostgresResult([], [[PostgresValue.FromLong(id)]], 1);
    }

    private PostgresResult ReadExpenseHistory(string sql, Dictionary<string, PostgresValue> values)
    {
        if (!sql.Contains("JOIN categories c ON c.id = e.category_id", StringComparison.Ordinal))
            throw new InvalidOperationException("History category lookup requires the mirror identity join.");
        IEnumerable<IReadOnlyList<PostgresValue>> rows = _expenses.Where(row => CategoryRows.Any(category => category.Name == row[4].StringValue));
        if (sql.Contains("ORDER BY e.booking_date DESC", StringComparison.Ordinal))
            rows = rows.OrderByDescending(row => DateTimeOffset.Parse(row[1].StringValue!))
                .ThenByDescending(row => sql.Contains("e.id DESC", StringComparison.Ordinal)
                    ? int.Parse(row[0].StringValue!) : 0);
        if (sql.Contains("OFFSET :offset", StringComparison.Ordinal))
            rows = rows.Skip((int)Math.Min(values["offset"].LongValue!.Value, int.MaxValue));
        if (sql.Contains("LIMIT :limit", StringComparison.Ordinal))
            rows = rows.Take((int)values["limit"].LongValue!.Value);
        var total = sql.Contains("SELECT COUNT(*) AS total_count FROM expenses", StringComparison.Ordinal) ? _expenses.Count : 0;
        var count = PostgresValue.FromString(total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = rows.Select(row => (IReadOnlyList<PostgresValue>)new[] { count }.Concat(row).ToArray()).ToList();
        if (result.Count == 0) result.Add(new[] { count }.Concat(Enumerable.Repeat(PostgresValue.Null(), 8)).ToArray());
        return new PostgresResult([], result, 0);
    }

    public Exception? ExecuteException { get; set; }
    public int ExecuteExceptionAtCall { get; set; } = 1;
    public string? ExecuteExceptionSqlPrefix { get; set; }
    public string? NextTransactionId { get; set; }

    public List<string> ExecutedSql { get; } = [];
    public List<IReadOnlyList<PostgresParameter>?> ExecutedParameters { get; } = [];
    public List<string?> ExecutedTransactionIds { get; } = [];
    public List<CancellationToken> ExecutedCancellationTokens { get; } = [];

    public Task<PostgresResult> ExecuteAsync(
        string sql,
        IReadOnlyList<PostgresParameter>? parameters = null,
        string? transactionId = null,
        CancellationToken cancellationToken = default)
    {
        ExecutedSql.Add(sql);
        ExecutedParameters.Add(parameters);
        ExecutedTransactionIds.Add(transactionId);
        ExecutedCancellationTokens.Add(cancellationToken);
        if (ExecuteException is not null && (ExecuteExceptionSqlPrefix is { } prefix
            ? sql.StartsWith(prefix, StringComparison.Ordinal) : ExecutedSql.Count >= ExecuteExceptionAtCall))
            throw ExecuteException;

        if (PersistBankImports && (sql.Contains("bank_accounts", StringComparison.Ordinal)
            || sql.StartsWith("WITH identity_choice", StringComparison.Ordinal)
            || sql.StartsWith("UPDATE bank_transactions\nSET suggested_category_id", StringComparison.Ordinal)))
            return ExecuteBankImportAsync(sql, parameters, cancellationToken);
        if (NextResults.Count == 0 && sql.StartsWith("SELECT id, name, description FROM categories", StringComparison.Ordinal))
        {
            var categories = sql.Contains("ORDER BY name", StringComparison.Ordinal)
                ? CategoryRows.OrderBy(category => category.Name, StringComparer.Ordinal).ToList() : CategoryRows;
            return Task.FromResult(new PostgresResult([], categories.Select(category => (IReadOnlyList<PostgresValue>)new[]
                { PostgresValue.FromString(category.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                  PostgresValue.FromString(category.Name), category.Description is null ? PostgresValue.Null() : PostgresValue.FromString(category.Description) }).ToList(), 0));
        }
        if (PersistExpenses && sql.Contains("information_schema.columns", StringComparison.Ordinal))
            return Task.FromResult(ReadLinkSchema(sql));
        if (ExecuteBank is not null && sql.Contains("bank_transactions", StringComparison.Ordinal))
            return Task.FromResult(ExecuteBank(sql, parameters));
        if (HasBankReviewSchema && sql.StartsWith("SELECT id FROM bank_transactions", StringComparison.Ordinal))
        {
            var id = parameters!.Single(parameter => parameter.Name == "id").Value.LongValue!.Value;
            var rows = LinkedTransactions.Keys.Where(key => !sql.Contains("WHERE id = :id", StringComparison.Ordinal) || key == id)
                .Select(key => (IReadOnlyList<PostgresValue>)new[] { PostgresValue.FromLong(key) }).ToList();
            return Task.FromResult(new PostgresResult([], rows, 0));
        }
        if (HasBankReviewSchema && sql.StartsWith("UPDATE bank_transactions bt", StringComparison.Ordinal))
            return Task.FromResult(ChangeBankLink(sql, parameters!));
        if (HasBankReviewSchema && sql.StartsWith("SELECT candidate.id", StringComparison.Ordinal))
            return Task.FromResult(new PostgresResult([], EligibleExpenses(sql, parameters!).Select(row =>
                (IReadOnlyList<PostgresValue>)row.Take(5).ToArray()).ToList(), 0));
        if (HasBankReviewSchema && (sql.StartsWith("SELECT bt.id", StringComparison.Ordinal)
            || sql.StartsWith("WITH updated AS", StringComparison.Ordinal)))
            return Task.FromResult(ExecuteBankReview(sql, parameters!));
        if (PersistExpenses && NextResults.Count == 0 && (sql.Contains("expenses", StringComparison.Ordinal) || sql.StartsWith("UPDATE bank_transactions", StringComparison.Ordinal)
            || sql == "SELECT id FROM categories WHERE name = :name"))
            return Task.FromResult(ExecuteExpense(sql, parameters));
        return Task.FromResult(NextResults.Count > 0
            ? NextResults.Dequeue()
            : new PostgresResult([], [], 0));
    }

    private PostgresResult ExecuteBankReview(string sql, IReadOnlyList<PostgresParameter> parameters)
    {
        var values = parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value);
        var rows = LinkedTransactions.AsEnumerable();
        if (sql.Contains("WHERE bt.id = :id", StringComparison.Ordinal) || sql.Contains("WHERE id = :id", StringComparison.Ordinal))
            rows = rows.Where(row => row.Key == values["id"].LongValue);
        if (sql.StartsWith("WITH updated AS", StringComparison.Ordinal))
            rows = ChangeBankReview(rows, sql, values["review_status"].StringValue!);
        if (sql.Contains("WHERE bt.review_status = :review_status", StringComparison.Ordinal))
            rows = rows.Where(row => row.Value.Status == values["review_status"].StringValue);
        if (sql.Contains("ORDER BY bt.booking_date DESC, bt.id DESC", StringComparison.Ordinal))
            rows = rows.OrderByDescending(row => row.Key);
        return new PostgresResult([], rows.Select(row => BankReviewRow(row.Key, row.Value, sql)).ToList(), 0);
    }

    private PostgresResult ChangeBankLink(string sql, IReadOnlyList<PostgresParameter> parameters)
    {
        sql = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ");
        var values = parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value);
        var id = values["id"].LongValue!.Value;
        var expenseId = checked((int)values["expense_id"].LongValue!.Value);
        if (!LinkedTransactions.ContainsKey(id)) return new([], [], 0);
        if (sql.Contains("SET expense_id = NULL"))
        {
            var link = LinkedTransactions[id];
            if (sql.Contains("bt.expense_id = :expense_id") && link.ExpenseId != expenseId) return new([], [], 0);
            if (sql.Contains("bt.review_status = 'matched'") && link.Status != "matched") return new([], [], 0);
            LinkedTransactions[id] = (null, "needsReview");
            return new([], [[PostgresValue.FromLong(id)]], 1);
        }
        if (!EligibleExpenses(sql, parameters).Any(row => !sql.Contains("candidate.id = :expense_id") || int.Parse(row[0].StringValue!) == expenseId))
            return new([], [], 0);
        if (sql.Contains("linked.expense_id = :expense_id") && LinkedTransactions.Values.Any(link => link.ExpenseId == expenseId))
            return new([], [], 0);
        LinkedTransactions[id] = (expenseId, "matched");
        return new([], [[PostgresValue.FromLong(id)]], 1);
    }

    private IEnumerable<IReadOnlyList<PostgresValue>> EligibleExpenses(string sql, IReadOnlyList<PostgresParameter> parameters)
    {
        sql = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ");
        var id = parameters.Single(parameter => parameter.Name == "id").Value.LongValue!.Value;
        if (!LinkedTransactions.TryGetValue(id, out var link)) return [];
        var bank = BankDetails.GetValueOrDefault(id, ("2026-07-01", "12.34", "EUR", null));
        if (sql.Contains("bt.review_status = 'needsReview'") && link.Status != "needsReview") return [];
        if (sql.Contains("bt.expense_id IS NULL") && link.ExpenseId is not null) return [];
        if (sql.Contains("bt.currency = 'EUR'") && bank.Item3 != "EUR") return [];
        var movement = BankMovementStates.GetValueOrDefault(id, ("BOOK", "DBIT"));
        if (sql.Contains("bt.status = 'BOOK'") && movement.Item1 != "BOOK") return [];
        if (sql.Contains("bt.direction = 'DBIT'") && movement.Item2 != "DBIT") return [];
        if (sql.Contains("bt.amount::text NOT IN ('NaN', 'Infinity', '-Infinity')") && bank.Item2 is "NaN" or "Infinity" or "-Infinity") return [];
        if (sql.Contains("bt.amount > 0") && decimal.Parse(bank.Item2, System.Globalization.CultureInfo.InvariantCulture) <= 0) return [];
        if (sql.Contains("bt.booking_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'")
            && !DateOnly.TryParse(bank.Item1, System.Globalization.CultureInfo.InvariantCulture, out _)) return [];
        IEnumerable<IReadOnlyList<PostgresValue>> rows = _expenses;
        if (sql.Contains("candidate.amount = bt.amount"))
            rows = rows.Where(row => decimal.Parse(row[2].StringValue!, System.Globalization.CultureInfo.InvariantCulture) == decimal.Parse(bank.Item2, System.Globalization.CultureInfo.InvariantCulture));
        if (sql.Contains("BETWEEN bt.booking_date - 3 AND bt.booking_date + 3"))
            rows = rows.Where(row => Math.Abs(DateOnly.FromDateTime(sql.Contains("AT TIME ZONE 'Europe/Lisbon'")
                ? GuitoApi.Services.TimeService.InLisbon(DateTimeOffset.Parse(row[1].StringValue!)).DateTime
                : DateTimeOffset.Parse(row[1].StringValue!).UtcDateTime).DayNumber - DateOnly.Parse(bank.Item1).DayNumber) <= 3);
        if (sql.Contains("linked.expense_id = candidate.id"))
            rows = rows.Where(row => !LinkedTransactions.Values.Any(value => value.ExpenseId == int.Parse(row[0].StringValue!)));
        return sql.Contains("LIMIT") ? rows.Take(10) : rows;
    }

    private IEnumerable<KeyValuePair<long, (int? ExpenseId, string Status)>> ChangeBankReview(
        IEnumerable<KeyValuePair<long, (int? ExpenseId, string Status)>> rows, string sql, string target)
    {
        var eligible = rows.Where(row => (!sql.Contains("expense_id IS NULL", StringComparison.Ordinal) || row.Value.ExpenseId is null)
            && (!sql.Contains("review_status IN ('needsReview', 'ignored')", StringComparison.Ordinal)
                || row.Value.Status is "needsReview" or "ignored")).ToArray();
        foreach (var row in eligible) LinkedTransactions[row.Key] = (row.Value.ExpenseId, target);
        return eligible.Select(row => new KeyValuePair<long, (int?, string)>(row.Key, LinkedTransactions[row.Key]));
    }

    private IReadOnlyList<PostgresValue> BankReviewRow(long id, (int? ExpenseId, string Status) link, string sql)
    {
        var expense = _expenses.SingleOrDefault(row => int.Parse(row[0].StringValue!) == link.ExpenseId);
        var bank = BankDetails.GetValueOrDefault(id, ("2026-07-01", "12.34", "EUR", null));
        var count = sql.Contains("SELECT COUNT(*) FROM expenses candidate")
            ? EligibleExpenses(sql, [new("id", PostgresValue.FromLong(id))]).Count() : 0;
        var imported = _bankImports.Values.SingleOrDefault(row => row["id"].LongValue == id);
        var suggestion = _bankCategories.GetValueOrDefault(id);
        return [PostgresValue.FromLong(id), PostgresValue.FromString(imported?["account_uid"].StringValue ?? "eb-account"),
            PostgresValue.FromString(bank.Item1), PostgresValue.FromString(bank.Item2),
            PostgresValue.FromString(bank.Item3), bank.Item4 is null ? PostgresValue.Null() : PostgresValue.FromString(bank.Item4),
            suggestion is { } categoryId ? PostgresValue.FromLong(categoryId) : PostgresValue.Null(),
            suggestion is { } suggestedId ? PostgresValue.FromString(CategoryRows.Single(c => c.Id == suggestedId).Name) : PostgresValue.Null(),
            PostgresValue.FromString("BOOK"), PostgresValue.FromString(link.Status),
            link.ExpenseId is { } expenseId ? PostgresValue.FromLong(expenseId) : PostgresValue.Null(),
            expense?[1] ?? PostgresValue.Null(), expense?[2] ?? PostgresValue.Null(),
            expense?[3] ?? PostgresValue.Null(), expense?[4] ?? PostgresValue.Null(), PostgresValue.FromLong(count)];
    }

    private async Task<PostgresResult> ExecuteBankImportAsync(string sql, IReadOnlyList<PostgresParameter>? parameters, CancellationToken cancellationToken)
    {
        if (BeforeBankImportAsync is not null) await BeforeBankImportAsync(sql, cancellationToken);
        return ExecuteBankImport(sql, parameters);
    }

    private PostgresResult ExecuteBankImport(string sql, IReadOnlyList<PostgresParameter>? parameters)
    {
        var values = parameters?.ToDictionary(p => p.Name, p => p.Value) ?? [];
        if (sql.Contains("bank_accounts", StringComparison.Ordinal)) return ExecuteBankAccount(sql, values);
        if (sql.StartsWith("UPDATE", StringComparison.Ordinal))
        {
            var targets = _bankImports.Where(row => !sql.Contains("sync_key = :sync_key", StringComparison.Ordinal)
                || row.Key == values["sync_key"].StringValue).ToArray();
            foreach (var target in targets)
            {
                var id = target.Value["id"].LongValue!.Value;
                var state = LinkedTransactions[id];
                if (sql.Contains("expense_id IS NULL", StringComparison.Ordinal) && state.ExpenseId is not null) continue;
                if (sql.Contains("review_status IN ('needsReview', 'ignored')", StringComparison.Ordinal) && state.Status is not ("needsReview" or "ignored")) continue;
                _bankCategories[id] = values["category_id"].LongValue;
            }
            return new([], [], 0);
        }
        var reliable = values["identity_reliable"].BooleanValue == true;
        var evidence = _bankImports.Values.AsEnumerable();
        if (sql.Contains("evidence.account_uid = :account_uid", StringComparison.Ordinal))
            evidence = evidence.Where(row => row["account_uid"].StringValue == values["account_uid"].StringValue);
        if (sql.Contains("evidence.entry_reference = :entry_reference", StringComparison.Ordinal))
            evidence = evidence.Where(row => row["entry_reference"].StringValue == values["entry_reference"].StringValue && !values["entry_reference"].IsNull);
        if (sql.Contains("NOT EXISTS", StringComparison.Ordinal) && evidence.Any(row =>
            (sql.Contains("evidence.identity_reliable IS NOT TRUE", StringComparison.Ordinal) && row["identity_reliable"].BooleanValue != true)
            || (sql.Contains("evidence.amount <>", StringComparison.Ordinal) && row["amount"].StringValue != values["amount"].StringValue)
            || (sql.Contains("evidence.currency <>", StringComparison.Ordinal) && row["currency"].StringValue != values["currency"].StringValue)
            || (sql.Contains("evidence.direction <>", StringComparison.Ordinal) && row["direction"].StringValue != values["direction"].StringValue))) reliable = false;
        var key = values[reliable ? "sync_key" : "fallback_sync_key"].StringValue!;
        if (!_bankImports.TryGetValue(key, out var existing))
        {
            var id = LinkedTransactions.Keys.DefaultIfEmpty(0).Max() + 1;
            values["id"] = PostgresValue.FromLong(id);
            values["identity_reliable"] = PostgresValue.FromBoolean(reliable);
            _bankImports.Add(key, values);
            LinkedTransactions.Add(id, (null, "needsReview"));
            UpdateImportedBankDetails(values);
            return new([], [[values["id"], PostgresValue.FromString(key), PostgresValue.FromBoolean(true)]], 1);
        }
        var existingId = existing["id"].LongValue!.Value;
        var link = LinkedTransactions[existingId];
        if ((sql.Contains("bank_transactions.expense_id IS NULL", StringComparison.Ordinal) && link.ExpenseId is not null)
            || (sql.Contains("bank_transactions.review_status IN ('needsReview', 'ignored')", StringComparison.Ordinal) && link.Status is not ("needsReview" or "ignored"))
            || (sql.Contains("bank_transactions.identity_reliable AND EXCLUDED.identity_reliable", StringComparison.Ordinal) && (!reliable || existing["identity_reliable"].BooleanValue != true)))
            return new([], [], 0);
        foreach (var field in new[] { "account_uid", "entry_reference", "amount", "currency", "direction" })
            if (sql.Contains($"bank_transactions.{field} = EXCLUDED.{field}", StringComparison.Ordinal) && existing[field] != values[field]) return new([], [], 0);
        foreach (var field in new[] { "booking_date", "transaction_date", "value_date", "remittance_information" })
            if (sql.Contains($"{field} = EXCLUDED.{field}", StringComparison.Ordinal)) existing[field] = values[field];
        if (sql.Contains("review_status = EXCLUDED.review_status", StringComparison.Ordinal)) LinkedTransactions[existingId] = (link.ExpenseId, "needsReview");
        UpdateImportedBankDetails(existing);
        return new([], [[existing["id"], PostgresValue.FromString(key), PostgresValue.FromBoolean(false)]], 1);
    }

    private void UpdateImportedBankDetails(Dictionary<string, PostgresValue> row) =>
        BankDetails[row["id"].LongValue!.Value] = (row["booking_date"].StringValue!, row["amount"].StringValue!, row["currency"].StringValue!, row["remittance_information"].StringValue);

    private PostgresResult ExecuteBankAccount(string sql, Dictionary<string, PostgresValue> values)
    {
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
            return new([], _bankAccounts.Values.Select(row => (IReadOnlyList<PostgresValue>)new[]
            {
                row["uid"], row["session_id"], row["iban"], row["name"], row["currency"], row["aspsp_country"], row["aspsp_name"],
                row["consent_status"], row["consent_expires_at"], row["fetch_uid"], row["identification_hash"]
            }).ToList(), 0);
        var target = sql.Contains("ON CONFLICT (identification_hash)", StringComparison.Ordinal)
            ? _bankAccounts.Values.SingleOrDefault(row => row["identification_hash"] == values["identification_hash"])
            : _bankAccounts.GetValueOrDefault(values["uid"].StringValue!);
        if (target is null) _bankAccounts.Add(values["uid"].StringValue!, values);
        else
            foreach (var field in values.Keys.Where(field => field is not ("uid" or "identification_hash")))
                if (sql.Contains($"{field} = EXCLUDED.{field}", StringComparison.Ordinal)) target[field] = values[field];
        return new([], [], 1);
    }

    private PostgresResult ReadLinkSchema(string sql)
    {
        var columns = LinkSchemaColumns ?? (HasBankReviewSchema
            ? new Dictionary<string, string> { ["expense_id"] = "integer", ["review_status"] = "text", ["provider_payload"] = "jsonb" }
            : new Dictionary<string, string> { ["expense_id"] = "text", ["provider_payload"] = "jsonb" });
        IEnumerable<KeyValuePair<string, string>> selected = columns;
        if (sql.Contains("column_name IN ('expense_id', 'review_status')", StringComparison.Ordinal))
            selected = selected.Where(column => column.Key is "expense_id" or "review_status");
        var rows = selected.Select(column => (IReadOnlyList<PostgresValue>)new[]
            { PostgresValue.FromString(column.Key), PostgresValue.FromString(column.Value) }).ToList();
        // Other tables/schemas have the same column names: omitted predicates expose that ambiguity.
        if (!sql.Contains("table_schema = current_schema()", StringComparison.Ordinal)
            || !sql.Contains("table_name = 'bank_transactions'", StringComparison.Ordinal))
            rows.Add([PostgresValue.FromString("expense_id"), PostgresValue.FromString("uuid")]);
        return new PostgresResult([], rows, 0);
    }

    public async Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (BeginException is not null) throw BeginException;
        var id = NextTransactionId ?? throw new InvalidOperationException("No transaction id scripted.");
        await _transactionGate.WaitAsync(cancellationToken);
        _snapshot = _expenses.ToList();
        _revisionSnapshot = new(_revisions);
        _linksSnapshot = new(LinkedTransactions);
        return id;
    }

    public Task CommitAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (CommitException is not null) throw CommitException;
        Commits++;
        _snapshot = null;
        _transactionGate.Release();
        if (CommitAppliedThenThrow is not null) throw CommitAppliedThenThrow;
        return Task.CompletedTask;
    }

    public Exception? RollbackException { get; set; }
    public Exception? BeginException { get; set; }
    public Exception? CommitException { get; set; }
    public Exception? CommitAppliedThenThrow { get; set; }
    public Task RollbackCompletion { get; set; } = Task.CompletedTask;
    public TaskCompletionSource RollbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        RollbackStarted.TrySetResult();
        await RollbackCompletion.ConfigureAwait(false);
        if (RollbackException is not null) throw RollbackException;
        Rollbacks++;
        _expenses.Clear();
        _expenses.AddRange(_snapshot!);
        _revisions.Clear();
        foreach (var pair in _revisionSnapshot!) _revisions.Add(pair.Key, pair.Value);
        LinkedTransactions.Clear();
        foreach (var pair in _linksSnapshot!) LinkedTransactions.Add(pair.Key, pair.Value);
        _snapshot = null;
        _transactionGate.Release();
    }
}
