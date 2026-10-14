using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Sheets;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.Validation;

/// <summary>Rejects unavailable personal operations on invocation, never during controller construction.</summary>
public sealed class PersonalExpenseOperationFilter(IOptions<AppConfigurationOptions> options, ISheetScopeResolver scope) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (scope.IsSmokeScope || options.Value.ExpenseStore != "Postgres")
            throw new ProblemException(409, "Personal Expense operations require Postgres authority; smoke scope is unavailable.");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
