using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;

namespace GuitoApi.Services.ArtificialIntelligence
{
    /// <summary>
    /// Intentional stub: the Azure OpenAI Assistants beta SDK was deprecated and removed.
    /// A new implementation over OpenRouter is planned for revival phase 3.
    /// </summary>
    public class ExtractMethodService : IExtractMethodService
    {
        public Task<ExpenseExtracted> ExtractMethod(ExpenseExtract input)
        {
            throw new ProblemException(
                (int)System.Net.HttpStatusCode.NotImplemented,
                "Expense extraction is not implemented yet — the OpenRouter-based implementation is planned for revival phase 3.");
        }
    }
}