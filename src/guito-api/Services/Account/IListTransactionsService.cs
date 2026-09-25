using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Account
{
    public interface IListTransactionsService
    {
        public Task<TransactionList> ListAsync(DateTime? dateFrom = null, DateTime? dateTo = null);
    }
}
