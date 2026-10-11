using System.Text;
using System.Text.Json;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.Serialization;

public sealed class ExpenseListResponseLimitFilter(IOptions<JsonOptions> options) : IAsyncResultFilter
{
    // A conservative whole-body budget leaves room for JSON/base64 Lambda envelope
    // expansion below Lambda's 6 MB synchronous limit and API Gateway's 10 MB limit.
    private const int MaximumJsonBytes = 2_000_000;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ExpenseListLatest expenses })
        {
            context.Result = CheckedContent(expenses);
        }
        await next();
    }

    private ContentResult CheckedContent(ExpenseListLatest expenses)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(expenses, options.Value.JsonSerializerOptions);
        if (bytes.Length > MaximumJsonBytes)
            throw new ProblemException(502, "The complete expense result exceeds the 2,000,000-byte JSON response budget; no partial result was returned.");
        // Emit the exact serialization that was checked, before any response can start.
        return new ContentResult
        {
            Content = Encoding.UTF8.GetString(bytes), ContentType = "application/json; charset=utf-8", StatusCode = 200
        };
    }
}
