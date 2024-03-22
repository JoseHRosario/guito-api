using GuitoApi.Model;
using GuitoApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CategoryController : ControllerBase
    {
        private readonly ICategoryService _categoryService;

        public CategoryController(ICategoryService categoryService)
        {
            _categoryService = categoryService;
        }

        [HttpGet]
        public async Task<List<Category>> List()
        {
            return await _categoryService.List();
        }
    }
}
