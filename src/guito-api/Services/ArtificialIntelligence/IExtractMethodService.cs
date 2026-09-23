using GuitoApi.DataTransferObjects.Output;
using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Services.ArtificialIntelligence
{
    public interface IExtractMethodService
    {
        public Task<ExpenseExtracted> ExtractMethod(ExpenseExtract input);
    }
}
