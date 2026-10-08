namespace GuitoApi.DataTransferObjects.Output;

/// <summary>Result of POST /BankTransaction/sync (issue #90).</summary>
public class BankSyncResult
{
    /// <summary>Rows fetched from Enable Banking in the sync window (BOOK + DBIT only).</summary>
    public int Fetched { get; set; }

    /// <summary>Rows actually stored — the rest were sync_key duplicates, skipped.</summary>
    public int New { get; set; }
}