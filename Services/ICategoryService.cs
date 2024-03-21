using GuitoApi.Model;

namespace GuitoApi.Services
{
    public interface ICategoryService
    {
        public Task<List<Category>> List();
    }
}
