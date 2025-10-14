namespace Trignis.MicrosoftSQL.Models;

public class ApiEndpoint
{
    public string? Key { get; set; }
    public string? Url { get; set; }
    public ApiAuth? Auth { get; set; }
    public string? EnvironmentFile { get; set; }
}

public class ApiAuth
{
    public string? Type { get; set; }
    public string? Token { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? HeaderName { get; set; }
}