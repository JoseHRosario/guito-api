using GuitoApi.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GuitoApi.Tests;

public class ExceptionToProblemDetailsHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ShouldLogExceptionDetailsInMessage_WhenUnexpectedError()
    {
        var problemDetailsService = new FakeProblemDetailsService();
        var logger = new CapturingLogger();
        var handler = new ExceptionToProblemDetailsHandler(problemDetailsService, logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/Expense/latest/5";
        var exception = new InvalidOperationException("the real cause");

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        var logged = logger.LastState?.ToString() ?? string.Empty;
        Assert.Contains("InvalidOperationException", logged);
        Assert.Contains("the real cause", logged);
    }

    [Fact]
    public async Task TryHandleAsync_ShouldNotLogUnexpectedError_WhenProblemException()
    {
        var problemDetailsService = new FakeProblemDetailsService();
        var logger = new CapturingLogger();
        var handler = new ExceptionToProblemDetailsHandler(problemDetailsService, logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/Expense";
        var exception = new ProblemException(502, "expected problem");

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.Null(logger.LastState);
        Assert.Equal(502, context.Response.StatusCode);
    }

    private class FakeProblemDetailsService : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
    }

    private class CapturingLogger : ILogger<ExceptionToProblemDetailsHandler>
    {
        public object? LastState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastState = state;
        }
    }
}