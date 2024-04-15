using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Account
{
    public interface IListTransactionsService
    {
        public Task<TransactionList> List(DateTime? dateFrom = null, DateTime? dateTo = null);
    }
}
