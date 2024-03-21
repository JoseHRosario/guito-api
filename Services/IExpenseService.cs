using GuitoApi.Model;

namespace GuitoApi.Services
{
    public interface IExpenseService
    {
        public Task Create(Expense value);
    }
}
