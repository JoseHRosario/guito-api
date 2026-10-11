namespace Guito.HistoricalImport;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await ImportCommand.RunAsync(args); }
        catch (Exception exception)
        {
            // Provider messages can contain source values. Keep them out of public CI logs.
            Console.Error.WriteLine($"Historical import stopped ({exception.GetType().Name}). No authority switch was performed. Inspect protected evidence/configuration.");
            return 1;
        }
    }
}
