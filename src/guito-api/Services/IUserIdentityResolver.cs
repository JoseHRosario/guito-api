using GuitoApi.Model;

namespace GuitoApi.Services
{
    public interface IUserIdentityResolver
    {
        UserIdentity ResolveUserIdentity();
        string GetEmail();
    }
}
