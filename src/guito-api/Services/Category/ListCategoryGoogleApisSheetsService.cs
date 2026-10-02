using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Category
{
    /// <summary>
    /// Adapter over <see cref="ICategoryRepository"/> (repository-pattern
    /// refactor, ADR 0011): keeps the IListCategoryService contract so controllers
    /// and the wire shape are unchanged, while every Sheets behavior moved into
    /// the repository layer.
    /// </summary>
    public class ListCategoryGoogleApisSheetsService : IListCategoryService
    {
        private readonly ICategoryRepository _categoryRepository;

        public ListCategoryGoogleApisSheetsService(ICategoryRepository categoryRepository) =>
            _categoryRepository = categoryRepository;

        public async Task<CategoryList> ListAsync(CancellationToken cancellationToken = default)
        {
            var categories = await _categoryRepository.ListAsync(cancellationToken);
            return new CategoryList { Categories = [.. categories] };
        }
    }
}
