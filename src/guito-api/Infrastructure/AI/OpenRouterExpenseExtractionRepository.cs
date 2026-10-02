using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.AI
{
    /// <summary>
    /// OpenRouter implementation of expense extraction (issue #69): a chat model returns
    /// date/amount/merchant, and the Jev decision model answers the expense-note gate
    /// (noul) and picks the Category (choice). Owns every provider detail — endpoints,
    /// prompts, JSON shapes — so the service stays a thin adapter (ADR 0011). Never logs
    /// the API key.
    /// </summary>
    public class OpenRouterExpenseExtractionRepository(
        IOptions<AppConfigurationOptions> options,
        ISecretsProvider secretsProvider,
        IOpenRouterClientProvider openRouterClients) : IExpenseExtractionRepository
    {
        private const string ChatCompletionsEndpoint = "https://openrouter.ai/api/v1/chat/completions";
        private const string DecisionsEndpoint = "https://openrouter.ai/api/alpha/decisions";
        private const string CategoryDecisionKey = "category";
        private const string ExpenseGateQuestionKey = "is_expense";

        /// <summary>Cap on the extraction model's reply tokens (a JSON object is tiny).</summary>
        private const int ExtractMaxOutputTokens = 200;

        /// <summary>Jev noul probability of "yes" below which the note is not an expense.</summary>
        private const decimal ExpenseGateMinimumProbability = 0.5m;

        /// <summary>
        /// "Today" resolves in Lisbon time: the Lambda runs UTC, so a note logged just after
        /// 23:00 UTC would otherwise be dated the previous day for the user.
        /// </summary>
        private static readonly TimeZoneInfo LisbonTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

        public async Task<bool> IsExpenseNoteAsync(string prompt, CancellationToken cancellationToken = default)
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

            var response = await SendAsync(DecisionsEndpoint, requestPayload, cancellationToken);
            var probability = response?["answers"]?[ExpenseGateQuestionKey]?["noul"]?.GetValue<decimal>();
            return probability is not null && probability >= ExpenseGateMinimumProbability;
        }

        public async Task<ExpenseExtractionResult> ExtractAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LisbonTimeZone)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

            var response = await SendAsync(ChatCompletionsEndpoint, requestPayload, cancellationToken);
            var content = response?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
                throw new ProblemException(502, "Expense extraction model returned no content");

            return ParseExpenseJson(content);
        }

        public async Task<string> ResolveCategoryAsync(string prompt, string description,
            IReadOnlyList<CategoryListDetail> categories, CancellationToken cancellationToken = default)
        {
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

            var response = await SendAsync(DecisionsEndpoint, requestPayload, cancellationToken);
            var choice = response?["answers"]?[CategoryDecisionKey]?["choice"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(choice))
                throw new ProblemException(502, "Category decision model returned no choice");

            return choice;
        }

        private async Task<JsonNode?> SendAsync(string endpoint, JsonObject requestPayload,
            CancellationToken cancellationToken)
        {
            var apiKey = (await secretsProvider.GetAsync(cancellationToken)).OpenRouterApiKey;
            if (string.IsNullOrEmpty(apiKey))
                throw new ProblemException(500, "OpenRouter API key is not configured");

            using var client = await openRouterClients.GetAsync(cancellationToken);
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

        private static ExpenseExtractionResult ParseExpenseJson(string content)
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
                    : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LisbonTimeZone).Date;

                var amount = root.TryGetProperty("amount", out var amountElement) && amountElement.ValueKind == JsonValueKind.Number
                    ? amountElement.GetDecimal()
                    : 0m;

                var description = root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString() ?? string.Empty
                    : string.Empty;

                return new ExpenseExtractionResult { Date = date, Amount = amount, Description = description };
            }
            catch (JsonException)
            {
                throw new ProblemException(502, "Expense extraction response is not valid JSON");
            }
        }
    }
}