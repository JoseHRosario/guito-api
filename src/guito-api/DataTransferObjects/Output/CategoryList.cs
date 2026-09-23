namespace GuitoApi.DataTransferObjects.Output
{
    public class CategoryList
    {
        public List<CategoryListDetail> Categories { get; set; } = new List<CategoryListDetail>();
    }

    public class CategoryListDetail
    {
        public string Name { get; set; } = "";
    }
}
