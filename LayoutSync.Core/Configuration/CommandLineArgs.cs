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

    /// <summary>
    /// Collection folder names to exclude from discovery, sync, and orphan detection
    /// (repeatable <c>--exclude-collection</c>). Values are folder names per
    /// <c>CollectionFolders</c>, validated by <c>ExclusionValidator</c> before the run
    /// starts. A sync filter, NOT write protection — file-only tooling (manifest
    /// subcommands, MCP) can still mutate files inside excluded folders. See issue #9.
    /// </summary>
    public string[] ExcludeCollections { get; init; } = [];

    /// <summary>
    /// Layout directory names to exclude entirely (repeatable <c>--exclude-layout</c>),
    /// e.g. a test-fixture layout that must never reach a shared DB. Also gates orphan
    /// deletion conservatively — see <c>DocumentSyncService.FilterOrphansForExcludedLayouts</c>.
    /// See issue #9.
    /// </summary>
    public string[] ExcludeLayouts { get; init; } = [];

    /// <summary>
    /// Optional override for <see cref="SyncOptions.DebounceMs"/>. Null means "use
    /// appsettings / default". 0 means "fire on the next thread-pool tick" — safe
    /// because FileWatcherService serializes batches via a SemaphoreSlim gate.
    /// </summary>
    public int? DebounceMs { get; init; }

    /// <summary>
    /// Convenience flag equivalent to <c>--debounce-ms 0</c>. When true, overrides
    /// any other DebounceMs source.
    /// </summary>
    public bool NoDebounce { get; init; }
}
