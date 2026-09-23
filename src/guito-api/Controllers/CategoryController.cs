using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.Category;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CategoryController : ControllerBase
    {
        private readonly IListCategoryService _categoryService;

        public CategoryController(IListCategoryService categoryService)
        {
            _categoryService = categoryService;
        }

        [HttpGet]
        public async Task<CategoryList> List()
        {
            return await _categoryService.List();
        }
    }
}
