using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Model;

namespace GuitoApi.Services.Auth;

public interface IEstablishUserSessionService
{
    Task<UserSessionIssue> EstablishAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default);
}
