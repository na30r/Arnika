namespace WebMirror.Api.Options;

public sealed class MirrorOptions
{
    public const string SectionName = "Mirror";

    public string FrontendRoot { get; set; } = "../frontend";
    public string? PlaywrightExecutablePath { get; set; }
    public int MaxDepth { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
    public int RequestsPerMinute { get; set; } = 30;
    public List<string> DomainWhitelist { get; set; } = [];
}
