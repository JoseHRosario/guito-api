using GuitoApi.Infrastructure.Import;
using GuitoApi.Model;
using GuitoApi.Services.Import;
using Xunit;

namespace GuitoApi.Tests;

public class HistoricalExpenseImportTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 4)]
    [InlineData(5, 6)]
    public async Task ImportAsync_ShouldRejectIncompleteBounds_WhenApplicationSnapshotDoesNotCoverItsRange(int firstRow, int lastRow)
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = "[]" };
        var database = new ImportDatabaseTransport();
        var snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() with { FirstRow = firstRow, LastRow = lastRow };

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() => ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot }));

        // Assert
        Assert.Empty(database.Heads);
        Assert.Equal(0, database.ExpenseCount);
    }

    [Fact]
    public async Task ImportAsync_ShouldKeepSourceClassificationUnchanged_WhenReportRowsAreUpdated()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal("Imported", Assert.Single(report.Rows).Status);
        Assert.Equal("Pending", Assert.Single(snapshot.Rows).Status);
        Assert.Null(snapshot.Rows[0].ExpenseId);
    }

    [Fact]
    public async Task ImportAsync_ShouldAccountForCompleteHistory_WhenBlankRowsAndDuplicatesExist()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","original@example.com"],[],["2026-07-10","","","1.23","Coffee","Restaurants","original@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var importer = ImportHarness.Create(handler, database);

        // Act
        var report = await importer.ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 7)).ToDomain() });

        // Assert
        AssertCompleteHistory(report, handler);
    }
    [Fact]
    public async Task ImportAsync_ShouldBlockReadiness_WhenPreviouslyMalformedRowDisappearsWithoutResolution()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["bad","","","1.23","Coffee","Restaurants","original@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var importer = ImportHarness.Create(handler, database);
        var initial = await importer.ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });
        handler.LatestValuesJson = "[]";
        var changed = await ImportHarness.CaptureAsync(handler, 5, 5);

        // Act
        var report = await importer.ImportAsync(new HistoricalImportRequest { Snapshot = changed.ToDomain(),
            Reconciliation = new ImportReconciliation { PreviousSnapshotId = initial.SnapshotId, ApprovedBy = "José", Reason = "Review" } });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.NotEmpty(report.Exceptions);
        Assert.Equal(0, database.ExpenseCount);
    }
    [Fact]
    public async Task ImportAsync_ShouldReportDurableExclusionApproval_WhenMalformedRowIsApprovedAndRetried()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["bad","","","1.23","Coffee","Restaurants","original@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var snapshot = await ImportHarness.CaptureAsync(handler, 5, 5);
        var request = new HistoricalImportRequest { Snapshot = snapshot.ToDomain(), Exclusions = new() { [5] = new("José", "Not an expense; reviewed original row") } };

        // Act
        await ImportHarness.Create(handler, database).ImportAsync(request);
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal("José", report.Rows[0].Exclusion!.ApprovedBy);
        Assert.Equal("Not an expense; reviewed original row", report.Rows[0].Exclusion!.Reason);
        Assert.Equal(0, database.ExpenseCount);
    }
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-12.3400", "-12.34")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("€ 123456789.1234567890123456789", "123456789.1234567890123456789")]
    [InlineData("0.0000000000000000000000000001 EUR", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public async Task ImportAsync_ShouldPreserveExactFinancialValues_WhenAmountsAreRepresentable(string source, string expected)
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = System.Text.Json.JsonSerializer.Serialize(new[] { new[] { "2026-01-10", "", "", source, "  ORIGINAL merchant  ", "Restaurants", "Original@Example.com" } }) };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        AssertExactFinancialValues(report, expected);
    }

    [Theory]
    [InlineData("bad-date", "1", "Restaurants", "creator@example.com")]
    [InlineData("2026-02-30", "1", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "0.12345678901234567890123456789", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "1e-29", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "79228162514264337593543950336", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "1 USD", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "1,234.56", "Restaurants", "creator@example.com")]
    [InlineData("2026-07-10", "1", "", "creator@example.com")]
    [InlineData("2026-07-10", "1", "Restaurants", "")]
    public async Task ImportAsync_ShouldBlockCutoverWithRawExceptions_WhenRelevantRowIsMalformed(string date, string amount, string category, string creator)
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = System.Text.Json.JsonSerializer.Serialize(new[] { new[] { date, "", "", amount, "Original", category, creator } }) };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.Equal("Malformed", Assert.Single(report.Rows).Status);
        Assert.NotEmpty(report.Rows[0].Exception!);
        Assert.Contains(date, report.Rows[0].RawJson);
        Assert.Equal(0, database.ExpenseCount);
    }

    [Fact]
    public async Task ImportAsync_ShouldPreserveBlankPositionsAndNonExpenses_WhenHistoryExtendsBeyondLastReturnedRow()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[[],["","2026","July"],["2026-07-10","","","0","","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 10)).ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, report.Rows.Select(r => r.SourceRow));
        Assert.Equal(new[] { "Blank", "NonExpense", "Imported", "Blank", "Blank", "Blank" }, report.Rows.Select(r => r.Status));
        Assert.Equal(1, database.ExpenseCount);
    }

    [Fact]
    public async Task ImportAsync_ShouldResumeWithoutDuplicates_WhenOneRowWriteFailsAndEntryPointIsRecreated()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","First","Restaurants","creator@example.com"],["2026-07-10","","","4.56","Fail","Restaurants","creator@example.com"],["2026-07-10","","","1.23","First","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport { FailDescription = "Fail" };
        var snapshot = await ImportHarness.CaptureAsync(handler, 5, 7);
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });
        database.FailDescription = null;

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });
        var repeated = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });

        // Assert
        AssertResumedHistory(first, report, repeated, database);
    }

    [Fact]
    public async Task ImportAsync_ShouldRecoverCommittedIdentity_WhenCommitResponseIsLost()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport { LoseCommitAcknowledgement = true };
        var snapshot = await ImportHarness.CaptureAsync(handler, 5, 5);
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });

        // Act
        var repeated = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = snapshot.ToDomain() });

        // Assert
        Assert.False(first.ReadyForCutover);
        Assert.True(repeated.ReadyForCutover);
        Assert.Equal(1, database.ExpenseCount);
        Assert.Equal(1, repeated.Rows[0].ExpenseId);
    }

    [Fact]
    public async Task ImportAsync_ShouldBlockChangedSnapshotBeforeInserts_WhenNoReconciliationApprovalExists()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });
        handler.LatestValuesJson = handler.LatestValuesJson.Replace("1.23", "2.34");

        // Act
        var changed = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        Assert.False(changed.ReadyForCutover);
        Assert.Contains(first.SnapshotId, Assert.Single(changed.Exceptions));
        Assert.NotEqual(first.SnapshotId, changed.SnapshotId);
        Assert.Equal(1, database.ExpenseCount);
    }

    [Fact]
    public async Task ImportAsync_ShouldReuseMovedRowsAndPreserveNewDuplicate_WhenExplicitOneToOneReconciliationIsApproved()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var initial = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });
        handler.LatestValuesJson = """[[],["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"],["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""";
        var request = new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 7)).ToDomain(), Reconciliation = new ImportReconciliation
            { PreviousSnapshotId = initial.SnapshotId, ApprovedBy = "José", Reason = "Row moved, second is a new purchase", ReuseRows = new() { [6] = 5 }, NewRows = [7] } };

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(request);
        var repeated = await ImportHarness.Create(handler, database).ImportAsync(request);

        // Assert
        AssertReconciledDuplicate(initial, report, repeated, database);
    }

    [Fact]
    public async Task ImportAsync_ShouldBlockReadiness_WhenPersistedParityDiffersDespiteSuccessfulWrites()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport { CorruptAmount = true };

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.False(report.Rows[0].Parity);
        Assert.Equal("999", report.Rows[0].Amount);
        Assert.Equal("0", report.ImportedTotal);
        Assert.NotEmpty(report.Rows[0].Exception!);
    }
    [Fact]
    public async Task ImportAsync_ShouldBlockResidualRecords_WhenAnImportedPredecessorIsExplicitlyRetainedAfterRemoval()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });
        handler.LatestValuesJson = "[]";

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain(),
            Reconciliation = new ImportReconciliation { PreviousSnapshotId = first.SnapshotId, ApprovedBy = "José", Reason = "Investigate removed row", RetainPreviousRows = [5] } });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.NotEmpty(report.Exceptions);
        Assert.Equal(1, database.ExpenseCount);
    }
    [Fact]
    public async Task ImportAsync_ShouldImportAndReconcileMoreThanOnePage_WhenFullArchivedHistoryExceedsLatestLimits()
    {
        // Arrange
        var source = Enumerable.Range(0, 125).Select(i => new[] { "2026-07-10", "", "", "1.23", $"Purchase {i}", "Restaurants", "creator@example.com" }).ToArray();
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = System.Text.Json.JsonSerializer.Serialize(source) };
        var database = new ImportDatabaseTransport();
        var original = await ImportHarness.CaptureAsync(handler, 5, 129);

        // Act / Assert: the archive fixture owns cleanup even when parity fails.
        await ImportHarness.WithArchiveAsync(original, async (path, loaded) =>
            await AssertPagedArchiveAsync(source, handler, database, original, path, loaded));
    }

    [Fact]
    public async Task ImportAsync_ShouldReportUnmirroredCategory_WhenSourceAttributionCannotBePersisted()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Not seeded","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.Equal("Failed", report.Rows[0].Status);
        Assert.Contains("mirror", report.Rows[0].Exception!);
        Assert.Equal("Not seeded", report.Rows[0].Expected!.Category);
        Assert.Equal(0, database.ExpenseCount);
    }

    [Fact]
    public async Task ImportAsync_ShouldHonorExplicitCommaConvention_WhenFormattedSourceUsesPortugueseDecimals()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","12,34 €","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5, ",")).ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal("12.34", report.Rows[0].Amount);
        Assert.Contains("12,34", report.Rows[0].RawJson);
    }
    [Fact]
    public async Task ImportAsync_ShouldPreserveUnderlyingAmount_WhenSheetsDisplayFormatRoundsTheVisibleValue()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23 €","Coffee","Restaurants","creator@example.com"]]""",
            UnformattedValuesJson = """[["2026-07-10","","",1.234567890123456789,"Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal("1.234567890123456789", report.Rows[0].Amount);
        Assert.Contains("1.23", report.Rows[0].RawJson);
        Assert.Contains("1.234567890123456789", report.Rows[0].RawJson);
    }
    [Fact]
    public async Task ImportAsync_ShouldPreserveOriginalDay_WhenExplicitPortugueseDateFormatMatchesUnderlyingSerial()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["10/07/2026","","","1.23 €","Coffee","Restaurants","creator@example.com"]]""",
            UnformattedValuesJson = """[[46213,"","",1.23,"Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var snapshot = await ImportHarness.CaptureAsync(handler, 5, 5);

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (snapshot with { DateFormat = "dd/MM/yyyy" }).ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal("2026-07-10T00:00:00+01:00", report.Rows[0].BookingDate);
    }
    [Fact]
    public async Task ImportAsync_ShouldReportExactAggregate_WhenTotalExceedsDecimalMaximum()
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","79228162514264337593543950335","Coffee","Restaurants","creator@example.com"],["2026-07-10","","","79228162514264337593543950335","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 6)).ToDomain() });

        // Assert
        Assert.True(report.ReadyForCutover);
        Assert.Equal("158456325028528675187087900670", report.SourceTotal);
        Assert.Equal(report.SourceTotal, report.PersistedTotal);
        Assert.Equal(report.SourceTotal, report.ImportedTotal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_ShouldRejectInvalidReconciliation_WhenDuplicateMappingOrChangedValueWouldHideHistory(bool changedValue)
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain() });
        handler.LatestValuesJson = changedValue
            ? """[["2026-07-10","","","2.34","Coffee","Restaurants","creator@example.com"],[]]"""
            : """[["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"],["2026-07-10","","","1.23","Coffee","Restaurants","creator@example.com"]]""";
        var plan = new ImportReconciliation { PreviousSnapshotId = first.SnapshotId, ApprovedBy = "José", Reason = "Review", ReuseRows = changedValue ? new() { [5] = 5 } : new() { [5] = 5, [6] = 5 } };

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 6)).ToDomain(), Reconciliation = plan });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.NotEmpty(report.Exceptions);
        Assert.Equal(1, database.ExpenseCount);
    }

    [Theory]
    [InlineData("", "Reviewed")]
    [InlineData("José", "")]
    public async Task ImportAsync_ShouldRejectUnapprovedExclusion_WhenApproverOrReasonIsAbsent(string approver, string reason)
    {
        // Arrange
        var handler = new FakeSheetsHttpHandler { LatestValuesJson = """[["bad","","","1.23","Coffee","Restaurants","creator@example.com"]]""" };
        var database = new ImportDatabaseTransport();

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = (await ImportHarness.CaptureAsync(handler, 5, 5)).ToDomain(), Exclusions = new() { [5] = new(approver, reason) } });

        // Assert
        Assert.False(report.ReadyForCutover);
        Assert.Equal(0, report.ExcludedCount);
        Assert.NotEmpty(report.Exceptions);
        Assert.Equal(0, database.ExpenseCount);
    }

    private static void AssertCompleteHistory(HistoricalImportReport report, FakeSheetsHttpHandler handler)
    {
        Assert.True(report.ReadyForCutover);
        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(2, report.ImportedCount);
        Assert.Equal("2.46", report.ImportedTotal);
        Assert.Equal(2, report.Rows.Where(r => r.ExpenseId != null).Select(r => r.ExpenseId).Distinct().Count());
        Assert.Equal("Blank", report.Rows[1].Status);
        Assert.All(report.Rows.Where(r => r.ExpenseId != null), AssertOriginalAudit);
        Assert.Equal(Enumerable.Repeat("Expenses!B5:H7", 4), handler.ReadRanges);
        Assert.Empty(handler.AppendBodies);
    }

    private static void AssertExactFinancialValues(HistoricalImportReport report, string expected)
    {
        Assert.True(report.ReadyForCutover);
        var row = Assert.Single(report.Rows);
        Assert.Equal(expected, row.Amount);
        Assert.Equal(expected, report.SourceTotal);
        Assert.Equal(expected, report.ImportedTotal);
        Assert.Equal("  ORIGINAL merchant  ", row.Description);
        Assert.Equal("Original@Example.com", row.CreatorEmail);
        Assert.Equal("Restaurants", row.Category);
        Assert.Equal("2026-01-10T00:00:00+00:00", row.BookingDate);
        Assert.StartsWith("2026-10-11", row.CreationDate);
    }

    private static void AssertResumedHistory(HistoricalImportReport first, HistoricalImportReport report, HistoricalImportReport repeated, ImportDatabaseTransport database)
    {
        Assert.False(first.ReadyForCutover);
        Assert.Equal(2, first.ImportedCount);
        Assert.Equal("Failed", first.Rows[1].Status);
        Assert.True(report.ReadyForCutover);
        Assert.True(repeated.ReadyForCutover);
        Assert.Equal(3, database.ExpenseCount);
        Assert.Equal("7.02", report.ImportedTotal);
        Assert.Equal(first.Rows[0].ExpenseId, report.Rows[0].ExpenseId);
        Assert.Equal(first.Rows[2].ExpenseId, report.Rows[2].ExpenseId);
        Assert.Equal(report.Rows.Select(r => r.ExpenseId), repeated.Rows.Select(r => r.ExpenseId));
        Assert.True(database.Rollbacks > 0);
    }

    private static void AssertReconciledDuplicate(HistoricalImportReport initial, HistoricalImportReport report, HistoricalImportReport repeated, ImportDatabaseTransport database)
    {
        Assert.True(report.ReadyForCutover);
        Assert.True(repeated.ReadyForCutover);
        Assert.Equal(initial.Rows[0].ExpenseId, report.Rows[1].ExpenseId);
        Assert.NotEqual(report.Rows[1].ExpenseId, report.Rows[2].ExpenseId);
        Assert.Equal(2, database.ExpenseCount);
    }

    private static void AssertOriginalAudit(HistoricalImportRow row)
    {
        Assert.Equal("2026-07-10T00:00:00+01:00", row.BookingDate);
        Assert.Equal("original@example.com", row.CreatorEmail);
        Assert.Null(row.UpdateDate);
        Assert.NotNull(row.CreationDate);
    }

    private static async Task AssertPagedArchiveAsync(string[][] source, FakeSheetsHttpHandler handler, ImportDatabaseTransport database,
        FrozenSheetsSnapshot original, string path, FrozenSheetsSnapshot loaded)
    {
        // Arrange
        var first = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = loaded.ToDomain() });
        handler.LatestValuesJson = System.Text.Json.JsonSerializer.Serialize(new[] { Array.Empty<string>() }.Concat(source));
        var changed = await ImportHarness.CaptureAsync(handler, 5, 130);

        // Act
        var report = await ImportHarness.Create(handler, database).ImportAsync(new HistoricalImportRequest { Snapshot = changed.ToDomain(),
            Reconciliation = new ImportReconciliation { PreviousSnapshotId = first.SnapshotId, ApprovedBy = "José", Reason = "One blank row inserted",
                ReuseRows = Enumerable.Range(6, 125).ToDictionary(i => i, i => i - 1) } });

        // Assert
        AssertPagedParity(first, report, database);
        Assert.Equal(SnapshotArchive.Identity(original), SnapshotArchive.Identity(loaded));
        Assert.Equal(path, await SnapshotArchive.SaveAsync(Path.GetDirectoryName(path)!, original with { CapturedAt = original.CapturedAt.AddDays(1) }));
    }

    private static void AssertPagedParity(HistoricalImportReport first, HistoricalImportReport report, ImportDatabaseTransport database)
    {
        Assert.True(first.ReadyForCutover);
        Assert.True(report.ReadyForCutover);
        Assert.Equal(125, report.ImportedCount);
        Assert.Equal("153.75", report.ImportedTotal);
        Assert.Equal(first.Rows.Select(r => r.ExpenseId), report.Rows.Skip(1).Select(r => r.ExpenseId));
        Assert.Equal(125, database.ExpenseCount);
    }
}
