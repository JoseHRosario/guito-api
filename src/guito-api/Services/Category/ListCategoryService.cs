using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Category;

public class ListCategoryService(ICategoriesRepository categoriesRepository) : IListCategoryService
{
    public async Task<CategoryList> ListAsync(CancellationToken cancellationToken = default)
    {
        var categories = await categoriesRepository.ListAsync(cancellationToken);
        return new CategoryList { Categories = categories.Select(category => new CategoryListDetail { Name = category.Name }).ToList() };
    }
}
