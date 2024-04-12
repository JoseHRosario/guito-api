namespace GuitoApi.Configuration
{
    public class Googlesheets
    {
        public string SpreadsheetId { get; set; } = string.Empty;
        public string ExpensesRange { get; set; } = string.Empty;
        public string ExpensesDateRange { get; set; } = string.Empty;
        public string ExpensesLatestRange { get; set; } = string.Empty;
        public string CategoriesRange { get; set; } = string.Empty;
        public string CredentialLocation { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string BlobName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RequisitionRange { get; set; } = string.Empty;
    }
}
