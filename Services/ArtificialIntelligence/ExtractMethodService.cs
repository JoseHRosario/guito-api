using Azure.AI.OpenAI.Assistants;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using Microsoft.Extensions.Options;
using System.Text.Json;
using GuitoApi.DataTransferObjects.Input;
using System.Globalization;

namespace GuitoApi.Services.ArtificialIntelligence
{
    public class ExtractMethodService : IExtractMethodService
    {
        private readonly AppConfigurationOptions _options;
        private readonly ILogger<ExtractMethodService> _logger;

        public ExtractMethodService(
            IOptions<AppConfigurationOptions> options,
            ILogger<ExtractMethodService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ExpenseExtracted> ExtractMethod(ExpenseExtract input)
        {
            ValidateConfiguration();
            var output = new ExpenseExtracted();
            var prompt = AddDateToPrompt(input);
            var client = new AssistantsClient(_options.OpenAi.ApiKey);
            var threadResponse = await client.CreateThreadAsync();
            var thread = threadResponse.Value;
            await client.CreateMessageAsync(thread.Id,MessageRole.User, prompt);
            var runResponse = await client.CreateRunAsync(thread.Id, new CreateRunOptions(_options.OpenAi.AssistantId));
            var run = runResponse.Value;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                runResponse = await client.GetRunAsync(thread.Id, runResponse.Value.Id);
            }
            while (runResponse.Value.Status == RunStatus.Queued || runResponse.Value.Status == RunStatus.InProgress);
            var runs = await client.GetRunsAsync(thread.Id);
            var action = runs.Value.FirstOrDefault()?.RequiredAction as SubmitToolOutputsAction;
            if (action == null)
            {
                _logger.LogError("No action found in Open AI");
                throw new Exception("No action found in Open AI");
            }
            var toolCall = action?.ToolCalls.FirstOrDefault() as RequiredFunctionToolCall;
            if (toolCall == null)
            {
                _logger.LogError("No tool call found in Open AI");
                throw new Exception("No tool call found in Open AI");
            }
            
            output = JsonSerializer.Deserialize<ExpenseExtracted>(toolCall.Arguments, 
                new JsonSerializerOptions() { PropertyNameCaseInsensitive  = true});
            await client.DeleteThreadAsync(thread.Id);
            return output;
        }

        private string AddDateToPrompt(ExpenseExtract input)
        {
            var date = DateTime.Now.ToString("dddd dd MMMM yyyy", CultureInfo.CreateSpecificCulture(input.Language));
            var datePrompt = input.Language switch
            {
                "en-US" => $"Take note that today is {date}",
                "pt-PT" => $"Toma nota que hoje é {date}",
                _ => $"Take note that today is {date}"
            };
            return $"{input.Prompt} {datePrompt}";
        }

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_options.OpenAi.ApiKey))
                throw new ArgumentNullException("OpenAI API Key is not set");

            if (string.IsNullOrWhiteSpace(_options.OpenAi.AssistantId))
                throw new ArgumentNullException("OpenAI Assistant ID is not set");
        }
    }
}
