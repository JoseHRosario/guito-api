using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers;

[ApiController]
[Route("Auth/session")]
[Consumes("application/json")]
public sealed class UserSessionController(IEstablishUserSessionService establish, IRefreshUserSessionService refresh,
    ILogoutUserSessionService logout) : ControllerBase
{
    public const string CookieName = "__Host-guito-session";

    [HttpPost("token")]
    public async Task<IActionResult> TokenAsync(TokenExchangeRequest request, CancellationToken cancellationToken) =>
        await CallAsync(async () => Issue(await establish.EstablishAsync(request, cancellationToken)), false);

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshAsync([FromBody] System.Text.Json.JsonElement body, CancellationToken cancellationToken) =>
        await CallAsync(async () => Issue(await refresh.RefreshAsync(Request.Cookies[CookieName], cancellationToken)), true);

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync([FromBody] System.Text.Json.JsonElement body, CancellationToken cancellationToken) =>
        await CallAsync(async () =>
        {
            await logout.LogoutAsync(Request.Cookies[CookieName], cancellationToken);
            ClearCookie();
            return NoContent();
        }, false);

    private IActionResult Issue(UserSessionIssue issue)
    {
        Response.Cookies.Append(CookieName, issue.Handle, CookieOptions(TimeSpan.FromDays(7)));
        return Ok(new UserSessionResponse { IdToken = issue.Tokens.IdToken, AccessToken = issue.Tokens.AccessToken,
            ExpiresIn = issue.Tokens.ExpiresIn, SessionExpiresAt = issue.ExpiresAt });
    }

    private async Task<IActionResult> CallAsync(Func<Task<IActionResult>> action, bool clearExpired)
    {
        try { return await action(); }
        catch (UserSessionException exception)
        {
            if (clearExpired && exception.StatusCode == 401) ClearCookie();
            return Error(exception);
        }
        // Do not let raw provider/secret/Data API exceptions (which can contain credentials) reach logging handlers.
        catch (Exception) { return Error(UserSessionException.Unavailable()); }
    }

    private ObjectResult Error(UserSessionException exception) => StatusCode(exception.StatusCode,
        new GoogleTokenErrorResponse { Error = exception.Error, ErrorDescription = exception.Message });
    private void ClearCookie() => Response.Cookies.Append(CookieName, string.Empty, CookieOptions(TimeSpan.Zero));
    private static CookieOptions CookieOptions(TimeSpan maxAge) => new()
    { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = maxAge, IsEssential = true };
}
