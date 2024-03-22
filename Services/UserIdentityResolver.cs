using System.Security.Claims;
using GuitoApi.Model;

namespace GuitoApi.Services
{
    public class UserIdentityResolver : IUserIdentityResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public UserIdentityResolver(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string GetEmail()
        {
            UserIdentity userIdentity = ResolveUserIdentity();
            return userIdentity.Email;
        }

        public UserIdentity ResolveUserIdentity()
        {
            if (_httpContextAccessor.HttpContext == null)
                throw new Exception("Something is wrong. HttpContext is not available");

            var userClaims = _httpContextAccessor.HttpContext.User.Claims.ToList();

            var userIdentity = new UserIdentity
            {
                Name = userClaims.FirstOrDefault(x => x.Type.Equals(ClaimTypes.Name))?.Value ?? "Jon Doe",
                Email = userClaims.FirstOrDefault(x => x.Type.Equals(ClaimTypes.Email))?.Value ?? "JonDoe@Madafaka.com"
            };
            return userIdentity;
        }
    }
}