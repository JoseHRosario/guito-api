namespace GuitoApi.DataTransferObjects.Output;

/// <summary>The Enable Banking auth redirect URL for the SPA (issue #89).</summary>
public class BankAuthUrl
{
    public string Url { get; set; } = string.Empty;
}