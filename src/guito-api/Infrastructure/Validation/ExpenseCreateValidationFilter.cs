using GuitoApi.DataTransferObjects.Input;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GuitoApi.Infrastructure.Validation;

public sealed class ExpenseCreateValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var input = context.ActionArguments.Values.OfType<ExpenseCreate>().SingleOrDefault();
        if (input?.AdditionalProperties is not { } properties) return;

        // Ignore unrelated extensions, but never silently discard a removed occurrence field.
        foreach (var key in properties.Keys.Where(IsRemovedField))
            context.ModelState.AddModelError(key, $"{key} is no longer accepted; supply bookingDate.");
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState));
    }

    private static bool IsRemovedField(string key) =>
        key.Equals("date", StringComparison.OrdinalIgnoreCase)
        || key.Equals("occurredAt", StringComparison.OrdinalIgnoreCase);

    public void OnActionExecuted(ActionExecutedContext context) { }
}
