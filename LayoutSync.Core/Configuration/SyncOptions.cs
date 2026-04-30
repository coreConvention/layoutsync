namespace LayoutSync.Configuration;

/// <summary>
/// Configuration options for the layout sync tool.
/// </summary>
public class SyncOptions
{
    /// <summary>
    /// Path to the layouts directory to watch and sync.
    /// Must be provided via command-line or config. No default.
    /// </summary>
    public string? LayoutsPath { get; set; }

    /// <summary>
    /// Debounce delay in milliseconds for file change events.
    /// Prevents multiple rapid syncs when files are being edited.
    /// </summary>
    public int DebounceMs { get; set; } = 500;

    /// <summary>
    /// Number of retry attempts for failed sync operations.
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between retry attempts in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;
}

/// <summary>
/// Configuration options for RavenDB connection.
/// </summary>
public class RavenDbOptions
{
    /// <summary>
    /// URL of the RavenDB server.
    /// </summary>
    public string Url { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Name of the database to sync documents to.
    /// </summary>
    public string Database { get; set; } = "coreConvention";

    /// <summary>
    /// Path to .pfx certificate file for RavenDB Cloud authentication.
    /// Required when connecting to RavenDB Cloud.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Password for the .pfx certificate file.
    /// Use null or empty string for certificates without password.
    /// </summary>
    public string? CertificatePassword { get; set; }
}
