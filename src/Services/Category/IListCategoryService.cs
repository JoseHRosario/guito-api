using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Category
{
    public interface IListCategoryService
    {
        public Task<CategoryList> List();
    }
}
