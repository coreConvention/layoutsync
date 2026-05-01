namespace LayoutSync.Configuration;

/// <summary>
/// Command-line arguments parsed from the command line. Pure POCO — no parsing logic
/// lives here. The CLI exe (<c>LayoutSync.csproj</c>) populates this from
/// <c>System.CommandLine</c> in <c>Program.cs</c>; services in this library consume it
/// without depending on the exe.
/// </summary>
public class CommandLineArgs
{
    public string? LayoutsPath { get; init; }
    public string? RavenUrl { get; init; }
    public string? Database { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    public string? Layout { get; init; }
    public bool SyncOnce { get; init; }
    public bool ValidateOnly { get; init; }
    public bool FixIds { get; init; }
    public bool DryRun { get; init; }
    public bool Clean { get; init; }
    public bool Verbose { get; init; }
    public bool PreserveIds { get; init; }
    public bool Strict { get; init; }
    public bool AllowRemoteSync { get; init; }
    public bool AllowCrossWorktreeSync { get; init; }
}
