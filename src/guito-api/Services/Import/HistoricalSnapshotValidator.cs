namespace GuitoApi.Services.Import;

public static class HistoricalSnapshotValidator
{
    public static bool HasCompleteBounds(int firstRow, int lastRow, int rowCount) =>
        firstRow >= 1 && lastRow >= firstRow && rowCount == lastRow - firstRow + 1;
}
