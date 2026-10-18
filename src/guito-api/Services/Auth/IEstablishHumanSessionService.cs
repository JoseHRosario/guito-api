using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Model;

namespace GuitoApi.Services.Auth;

public interface IEstablishHumanSessionService
{
    Task<HumanSessionIssue> EstablishAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default);
}
