using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests
{
    /// <summary>POST /AI/extract boundary tests over the faked OpenRouter HTTP transport.</summary>
    public class AiExtractBoundaryTests
    {
        [Fact]
        public async Task Extract_ShouldReturnParsedExpense_WhenPromptIsNaturalLanguage()
        {
            using var factory = new CustomWebApplicationFactory();
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract
            {
                Prompt = "cafe 2,30 no Coco Verde",
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("2026-10-02", body!.RootElement.GetProperty("date").GetDateTime().ToString("yyyy-MM-dd"));
            Assert.Equal(2.30m, body.RootElement.GetProperty("amount").GetDecimal());
            Assert.Equal("Cafe No Coco Verde", body.RootElement.GetProperty("description").GetString());
            Assert.Equal("Restaurants", body.RootElement.GetProperty("category").GetString());

            var chatRequest = factory.OpenRouterHandler.ChatRequestBodies.Last();
            Assert.Contains("cafe 2,30 no Coco Verde", chatRequest);
            Assert.Equal("google/gemini-2.5-flash", FakeOpenRouterHttpHandler.Field(chatRequest, "model"));
            Assert.Equal("Bearer test-openrouter-key", factory.OpenRouterHandler.ChatAuthorizationHeaders.Last());

            var decisionRequest = factory.OpenRouterHandler.DecisionRequestBodies.Last();
            Assert.Contains("Restaurants", decisionRequest);
            Assert.Contains("cafe 2,30 no Coco Verde", decisionRequest);
            Assert.Equal("Bearer test-openrouter-key", factory.OpenRouterHandler.DecisionAuthorizationHeaders.Last());
        }

        [Fact]
        public async Task Extract_ShouldReturnBadRequest_WhenPromptIsEmpty()
        {
            using var factory = new CustomWebApplicationFactory();
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(factory.OpenRouterHandler.ChatRequestBodies);
        }

        [Fact]
        public async Task Extract_ShouldNormalizeNegativeAmountToPositive_WhenModelReturnsNegativeAmount()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.ChatResponseContent =
                "{\"date\": \"2026-10-02\", \"amount\": -2.30, \"description\": \"Cafe\"}";
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal(2.30m, body!.RootElement.GetProperty("amount").GetDecimal());
        }

        [Fact]
        public async Task Extract_ShouldReturnBadRequest_WhenExtractedAmountIsZero()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.ChatResponseContent =
                "{\"date\": \"2026-10-02\", \"amount\": 0, \"description\": \"Cafe\"}";
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe" });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Extract_ShouldUseToday_WhenModelReturnsNoUsableDate()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.ChatResponseContent =
                "{\"amount\": 2.30, \"description\": \"Cafe\"}";
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal(DateTime.Today, body!.RootElement.GetProperty("date").GetDateTime());
        }

        [Fact]
        public async Task Extract_ShouldParseFencedJson_WhenModelWrapsJsonInMarkdown()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.ChatResponseContent =
                "```json\n{\"date\": \"2026-10-02\", \"amount\": 2.30, \"description\": \"Cafe\"}\n```";
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("Cafe", body!.RootElement.GetProperty("description").GetString());
        }

        [Fact]
        public async Task Extract_ShouldReturnBadGateway_WhenChatCallFails()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.FailChatWithStatus = 500;
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        [Fact]
        public async Task Extract_ShouldReturnBadGateway_WhenDecisionCallFails()
        {
            using var factory = new CustomWebApplicationFactory();
            factory.OpenRouterHandler.FailDecisionWithStatus = 500;
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        [Fact]
        public async Task Extract_ShouldReturnServerError_WhenApiKeyIsMissing()
        {
            using var factory = new CustomWebApplicationFactory().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    var descriptor = services.Single(d => d.ServiceType == typeof(ISecretsProvider));
                    services.Remove(descriptor);
                    services.AddSingleton<ISecretsProvider>(new FakeSecretsProvider
                    {
                        Payload = { OpenRouterApiKey = "" },
                    });
                }));
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/AI/extract", new ExpenseExtract { Prompt = "cafe 2,30" });

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }
}
