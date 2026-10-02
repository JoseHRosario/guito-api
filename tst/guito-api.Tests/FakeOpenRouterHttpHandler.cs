using System.Text.RegularExpressions;

namespace GuitoApi.Tests
{
    /// <summary>
    /// Fake OpenRouter HTTP transport: records request bodies and serves canned
    /// chat-completions / Decisions responses (same pattern as FakeSheetsHttpHandler).
    /// </summary>
    public class FakeOpenRouterHttpHandler : HttpMessageHandler
    {
        public List<string> ChatRequestBodies { get; } = [];
        public List<string> DecisionRequestBodies { get; } = [];

        /// <summary>Content of the canned extraction model reply (a JSON object string).</summary>
        public string ChatResponseContent { get; set; } =
            "{\"date\": \"2026-10-02\", \"amount\": 2.30, \"description\": \"Cafe No Coco Verde\"}";

        /// <summary>Choice the canned category decision returns.</summary>
        public string DecisionChoice { get; set; } = "Restaurants";

        public int? FailChatWithStatus { get; set; }
        public int? FailDecisionWithStatus { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;

            if (request.RequestUri?.ToString().Contains("/api/v1/chat/completions") == true)
            {
                ChatRequestBodies.Add(content);
                if (FailChatWithStatus is { } status)
                    return Task.FromResult(Json(status, new { error = new { message = "upstream chat failure" } }));
                var wrapped = $"{{ \"choices\": [ {{ \"message\": {{ \"content\": {System.Text.Json.JsonSerializer.Serialize(ChatResponseContent)} }} }} ] }}";
                return Task.FromResult(Json(200, wrapped));
            }

            if (request.RequestUri?.ToString().Contains("/api/alpha/decisions") == true)
            {
                DecisionRequestBodies.Add(content);
                if (FailDecisionWithStatus is { } status)
                    return Task.FromResult(Json(status, new { error = new { message = "upstream decision failure" } }));
                var payload = new
                {
                    answers = new
                    {
                        category = new { type = "choice", choice = DecisionChoice, confidence = 0.9 },
                    },
                };
                return Task.FromResult(Json(200, System.Text.Json.JsonSerializer.Serialize(payload)));
            }

            throw new InvalidOperationException($"Unexpected OpenRouter URL: {request.RequestUri}");
        }

        private static HttpResponseMessage Json(int statusCode, object payload)
        {
            var body = payload is string s ? s : System.Text.Json.JsonSerializer.Serialize(payload);
            return new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Extracts a single JSON field value from a recorded request body.</summary>
        public static string? Field(string body, string field)
        {
            var match = Regex.Match(body, $"\"{field}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}