namespace GuitoApi.Configuration;

public sealed class UserSessionOptions
{
    public string TableName { get; set; } = "";
    public string Region { get; set; } = "eu-west-1";
}
