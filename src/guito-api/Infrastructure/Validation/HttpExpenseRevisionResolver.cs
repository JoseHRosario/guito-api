using System.Globalization;
using GuitoApi.Exceptions;
using GuitoApi.Services.Expense;

namespace GuitoApi.Infrastructure.Validation;

public sealed class HttpExpenseRevisionResolver(IHttpContextAccessor accessor) : IExpenseRevisionResolver
{
    public long RequireRevision()
    {
        var headers = accessor.HttpContext!.Request.Headers;
        if (!headers.ContainsKey("If-Match")) throw new ProblemException(428, "A loaded If-Match revision is required.");
        var values = headers.IfMatch;
        var token = values.Count == 1 ? values[0] : null;
        if (token is null || token.Length < 3 || token[0] != '"' || token[^1] != '"'
            || !long.TryParse(token.AsSpan(1, token.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || token != $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"")
            throw new ProblemException(400, "If-Match must contain exactly one supported strong quoted revision.");
        return revision;
    }
}
