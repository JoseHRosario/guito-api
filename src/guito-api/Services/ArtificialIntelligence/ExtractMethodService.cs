using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.AI;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.ArtificialIntelligence
{
    /// <summary>
    /// Extracts an expense from a natural-language pt-PT note via OpenRouter (issue #69):
    /// a chat model returns date/amount/description, and the Jev decision model picks the
    /// Category as a choice over the sheet's category list. Amount is normalized positive
    /// (ADR 0010). Never logs the API key.
    /// </summary>
    public class ExtractMethodService(
        IOptions<AppConfigurationOptions> options,
        ISecretsProvider secretsProvider,
        IOpenRouterClientProvider openRouterClients,
        ICategoryRepository categoryRepository) : IExtractMethodService
    {
        private const string ChatCompletionsEndpoint = "https://openrouter.ai/api/v1/chat/completions";
        private const string DecisionsEndpoint = "https://openrouter.ai/api/alpha/decisions";
        private const string CategoryDecisionKey = "category";
        private const string ExpenseGateQuestionKey = "is_expense";

        /// <summary>Cap on the extraction model's reply tokens (a JSON object is tiny).</summary>
        private const int ExtractMaxOutputTokens = 200;

        /// <summary>Jev noul probability of "yes" below which the note is rejected as non-expense.</summary>
        private const decimal ExpenseGateMinimumProbability = 0.5m;

        /// <summary>
        /// "Today" resolves in Lisbon time: the Lambda runs UTC, so a note logged just after
        /// 23:00 UTC would otherwise be dated the previous day for the user.
        /// </summary>
        private static readonly TimeZoneInfo LisbonTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

        private static DateTime LisbonToday() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LisbonTimeZone).Date;

        public async Task<ExpenseExtracted> ExtractMethodAsync(ExpenseExtract input,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(input.Prompt))
                throw new ProblemException(400, "prompt is required");

            var apiKey = (await secretsProvider.GetAsync(cancellationToken)).OpenRouterApiKey;
            if (string.IsNullOrEmpty(apiKey))
                throw new ProblemException(500, "OpenRouter API key is not configured");

            using var client = await openRouterClients.GetAsync(cancellationToken);

            // Jev gate first: reject non-expense notes before spending a chat-model call.
            await EnsureIsExpenseNoteAsync(client, apiKey, input.Prompt, cancellationToken);

            var extracted = await ExtractExpenseAsync(client, apiKey, input.Prompt, cancellationToken);
            var category = await ResolveCategoryAsync(client, apiKey, input.Prompt, extracted.Description, cancellationToken);

            return new ExpenseExtracted
            {
                Date = extracted.Date,
                Amount = extracted.Amount,
                Description = extracted.Description,
                Category = category,
            };
        }

        private async Task EnsureIsExpenseNoteAsync(HttpClient client, string apiKey,
            string prompt, CancellationToken cancellationToken)
        {
            var requestPayload = new JsonObject
            {
                ["model"] = options.Value.ArtificialIntelligence.CategoryModel,
                ["questions"] = new JsonObject
                {
                    [ExpenseGateQuestionKey] = new JsonObject
                    {
                        ["type"] = "noul",
                        ["criteria"] = new JsonObject
                        {
                            ["true"] = "The note records an expense: a purchase or payment with an amount.",
                            ["false"] = "The note is anything else: a question, a greeting, a command, or unrelated content.",
                        },
                        ["instructions"] = "Is this note recording an expense?",
                    },
                },
                ["state"] = new JsonObject
                {
                    ["note"] = prompt,
                },
            };

            var response = await SendAsync(client, apiKey, DecisionsEndpoint, requestPayload, cancellationToken);
            var probability = response?["answers"]?[ExpenseGateQuestionKey]?["noul"]?.GetValue<decimal>();
            if (probability is null || probability < ExpenseGateMinimumProbability)
                throw new ProblemException(400, "The prompt does not look like an expense note");
        }

        private async Task<ExpenseExtracted> ExtractExpenseAsync(HttpClient client, string apiKey,
            string prompt, CancellationToken cancellationToken)
        {
            var today = LisbonToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var systemPrompt =
                "You extract expense details from a short natural-language note in Portuguese (pt-PT). " +
                $"Today is {today}. Reply with ONLY a JSON object, no markdown: " +
                "{\"date\": \"YYYY-MM-DD\", \"amount\": <positive number in EUR>, \"description\": \"<the merchant or place name>\"}. " +
                "Amount is always POSITIVE. The description is ONLY the merchant/store name (e.g. \"café 2,30 no Coco Verde\" -> \"Coco Verde\"); " +
                "drop the item and every filler word. If no date is mentioned use today.";

            var requestPayload = new JsonObject
            {
                ["model"] = options.Value.ArtificialIntelligence.ExtractModel,
                ["max_tokens"] = ExtractMaxOutputTokens,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = prompt },
                },
            };

            var response = await SendAsync(client, apiKey, ChatCompletionsEndpoint, requestPayload, cancellationToken);
            var content = response?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
                throw new ProblemException(502, "Expense extraction model returned no content");

            var parsed = ParseExpenseJson(content);
            return new ExpenseExtracted
            {
                Date = parsed.Date,
                Amount = NormalizePositiveAmount(parsed.Amount),
                Description = parsed.Description,
            };
        }

        private async Task<string> ResolveCategoryAsync(HttpClient client, string apiKey,
            string prompt, string description, CancellationToken cancellationToken)
        {
            var categories = await categoryRepository.ListAsync(cancellationToken);
            if (categories.Count == 0)
                return string.Empty;

            var criteria = new JsonObject();
            foreach (var category in categories)
                criteria[category.Name] = category.Name;

            var requestPayload = new JsonObject
            {
                ["model"] = options.Value.ArtificialIntelligence.CategoryModel,
                ["questions"] = new JsonObject
                {
                    [CategoryDecisionKey] = new JsonObject
                    {
                        ["type"] = "choice",
                        ["criteria"] = criteria,
                        ["instructions"] = "Which expense category best fits this expense note?",
                    },
                },
                ["state"] = new JsonObject
                {
                    ["note"] = prompt,
                    ["description"] = description,
                },
            };

            var response = await SendAsync(client, apiKey, DecisionsEndpoint, requestPayload, cancellationToken);
            var choice = response?["answers"]?[CategoryDecisionKey]?["choice"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(choice))
                throw new ProblemException(502, "Category decision model returned no choice");

            return choice;
        }

        private static async Task<JsonNode?> SendAsync(HttpClient client, string apiKey,
            string endpoint, JsonObject requestPayload, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestPayload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new ProblemException(
                    502,
                    $"OpenRouter call failed with status {(int)response.StatusCode}: {body}");

            return JsonNode.Parse(body);
        }

        private static decimal NormalizePositiveAmount(decimal amount)
        {
            var positive = Math.Abs(amount);
            if (positive <= 0)
                throw new ProblemException(400, "Could not extract a positive amount from the prompt");
            return positive;
        }

        private static (DateTime Date, decimal Amount, string Description) ParseExpenseJson(string content)
        {
            // The model may wrap the JSON in markdown fences; strip any prose around it.
            var json = content.Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
                throw new ProblemException(502, "Expense extraction response is not JSON");

            try
            {
                using var document = JsonDocument.Parse(json[start..(end + 1)]);
                var root = document.RootElement;

                var dateText = root.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String
                    ? dateElement.GetString()
                    : null;
                var date = DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                    ? parsedDate.Date
                    : LisbonToday();

                var amount = root.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number
                    ? amountElement.GetDecimal()
                    : 0m;

                var description = root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString() ?? string.Empty
                    : string.Empty;

                return (date, amount, description);
            }
            catch (JsonException)
            {
                throw new ProblemException(502, "Expense extraction response is not valid JSON");
            }
        }
    }
}