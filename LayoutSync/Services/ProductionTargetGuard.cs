using System.Net;
using Microsoft.Extensions.Logging;

namespace LayoutSync.Services;

/// <summary>
/// Classification of a RavenDB target by the shape of its URL host.
///
/// <list type="bullet">
///   <item><description><see cref="Local"/> — loopback (localhost/127.0.0.1/::1) or an RFC1918
///     private-network address. Safe to sync to without additional guards.</description></item>
///   <item><description><see cref="Remote"/> — any publicly routable host (FQDN, cloud RavenDB,
///     Azure-hosted). Requires explicit operator opt-in before LayoutSync will write.</description></item>
///   <item><description><see cref="Unknown"/> — the URL could not be parsed. Treated as Remote
///     for safety; the operator must pass the opt-in flag to proceed.</description></item>
/// </list>
/// </summary>
public enum ProductionTargetClassification
{
    Local,
    Remote,
    Unknown,
}

/// <summary>
/// Classifies a RavenDB connection URL as <see cref="ProductionTargetClassification.Local"/>,
/// <see cref="ProductionTargetClassification.Remote"/>, or
/// <see cref="ProductionTargetClassification.Unknown"/>, and enforces an explicit opt-in
/// (<c>--allow-remote-sync</c>) before LayoutSync will write against a non-local target.
///
/// Motivation: LayoutSync is a dev tool that occasionally runs against the Azure-hosted
/// production RavenDB. A careless sync in that mode can overwrite real user entities with
/// seed-stub versions, silently regenerate NanoIDs, or (with <c>--clean</c>) delete
/// static-collection documents. The guard makes the production target visually and
/// procedurally distinct from the localhost happy path so operators notice every time.
///
/// Classification is host-based. It does NOT consider certificate presence — local dev
/// certificates are legitimate, and certificate-authenticated localhost is still Local.
///
/// Tenant-agnostic: no layout identifier, tenant slug, or hostname pattern referencing a
/// specific tenant ever appears here.
/// </summary>
public sealed class ProductionTargetGuard(ILogger<ProductionTargetGuard> logger)
{
    private readonly ILogger<ProductionTargetGuard> _logger = logger;

    /// <summary>
    /// Loopback host literals. Both the string form (for direct comparison) and the
    /// <see cref="IPAddress"/> representation of <c>127.0.0.1</c> / <c>::1</c> are
    /// matched during classification.
    /// </summary>
    private static readonly string[] LoopbackHosts =
    [
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]",
    ];

    /// <summary>
    /// Classifies <paramref name="url"/> without side effects. Called with every RavenDB URL
    /// at startup and from unit tests.
    /// </summary>
    /// <param name="url">The RavenDB URL (e.g. <c>http://localhost:8080</c>).</param>
    /// <returns>The classification. Never throws — malformed URLs return
    /// <see cref="ProductionTargetClassification.Unknown"/>.</returns>
    public static ProductionTargetClassification Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ProductionTargetClassification.Unknown;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
            return ProductionTargetClassification.Unknown;

        string host = parsed.Host;
        if (string.IsNullOrWhiteSpace(host))
            return ProductionTargetClassification.Unknown;

        // Loopback literal fast path — covers `localhost`, `127.0.0.1`, and `::1`.
        foreach (string loopback in LoopbackHosts)
        {
            if (string.Equals(host, loopback, StringComparison.OrdinalIgnoreCase))
                return ProductionTargetClassification.Local;
        }

        // IP literal branch: IPv4/IPv6 parsing + loopback + RFC1918 ranges.
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            if (IPAddress.IsLoopback(ip))
                return ProductionTargetClassification.Local;

            if (IsPrivateIPv4(ip))
                return ProductionTargetClassification.Local;

            // Public IP literal — treat as Remote.
            return ProductionTargetClassification.Remote;
        }

        // Named host that is not a known loopback alias — Remote.
        // We intentionally do NOT resolve DNS here. DNS resolution would couple classification
        // to network availability and could surprise operators by "promoting" an internal
        // hostname to Local via /etc/hosts tricks. Hostname shape alone is the contract.
        return ProductionTargetClassification.Remote;
    }

    /// <summary>
    /// RFC1918 private-network ranges for IPv4:
    /// <list type="bullet">
    ///   <item><description><c>10.0.0.0/8</c></description></item>
    ///   <item><description><c>172.16.0.0/12</c></description></item>
    ///   <item><description><c>192.168.0.0/16</c></description></item>
    /// </list>
    /// IPv6 unique-local (<c>fc00::/7</c>) is intentionally NOT matched — the URL form for
    /// IPv6 is rare in dev setups and we prefer a conservative default (Remote) over a
    /// silently-permissive one.
    /// </summary>
    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] bytes = ip.GetAddressBytes();

        if (bytes[0] == 10)
            return true;

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;

        if (bytes[0] == 192 && bytes[1] == 168)
            return true;

        return false;
    }

    /// <summary>
    /// Gate the run. If the target is <see cref="ProductionTargetClassification.Remote"/>
    /// or <see cref="ProductionTargetClassification.Unknown"/> and
    /// <paramref name="allowRemoteSync"/> is <c>false</c>, returns <c>false</c> and logs a
    /// banner-style FATAL that instructs the operator how to re-run. Otherwise logs the
    /// classification and (for Remote+opt-in) emits a loud warning banner, then returns
    /// <c>true</c>.
    /// </summary>
    /// <param name="url">The resolved RavenDB URL.</param>
    /// <param name="allowRemoteSync">Whether the operator passed <c>--allow-remote-sync</c>.</param>
    /// <param name="dryRun">Whether the run is a dry-run (no writes). Dry-run against Remote
    /// is safe because no writes are issued; a softer note is logged.</param>
    /// <param name="layout">Optional specific layout being synced (for the banner).</param>
    /// <param name="preserveIds">Preserve-IDs flag state (for the banner).</param>
    /// <param name="strict">Strict mode state (for the banner).</param>
    /// <param name="clean">Clean-mode state (for the banner).</param>
    /// <returns><c>true</c> if the sync may proceed; <c>false</c> if the guard blocked it.</returns>
    public bool Authorize(
        string? url,
        bool allowRemoteSync,
        bool dryRun,
        string? layout,
        bool preserveIds,
        bool strict,
        bool clean)
    {
        ProductionTargetClassification classification = Classify(url);

        _logger.LogInformation(
            "Target classification: {Classification} (host: {Host})",
            classification,
            SafeHostOf(url));

        // Local is always allowed without flags.
        if (classification == ProductionTargetClassification.Local)
            return true;

        // Dry-run is non-destructive. We still require the flag for Remote/Unknown because
        // a dry-run can leak hostnames / credentials into logs, but we make the refusal
        // message explicit that --dry-run alone is not an override.
        if (!allowRemoteSync)
        {
            EmitRefusalBanner(classification, url);
            return false;
        }

        EmitAllowBanner(classification, url, dryRun, layout, preserveIds, strict, clean);
        return true;
    }

    /// <summary>
    /// Extracts the host portion of the URL for logging, or returns a safe placeholder when
    /// parsing fails. Never throws.
    /// </summary>
    private static string SafeHostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(null)";
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
            return parsed.Host;
        return "(unparseable)";
    }

    /// <summary>
    /// Width of the banner bar. Keeps banners consistent and wide enough to stand out in a
    /// mixed log stream.
    /// </summary>
    private const int BannerWidth = 76;

    private static string BannerBar() => new('=', BannerWidth);

    private void EmitRefusalBanner(ProductionTargetClassification classification, string? url)
    {
        string bar = BannerBar();
        _logger.LogCritical("{Bar}", bar);
        _logger.LogCritical("FATAL: REMOTE TARGET DETECTED — refusing to sync.");
        _logger.LogCritical("{Bar}", bar);
        _logger.LogCritical("Classification: {Classification}", classification);
        _logger.LogCritical("RavenDB host: {Host}", SafeHostOf(url));
        _logger.LogCritical("RavenDB URL:  {Url}", url ?? "(null)");
        _logger.LogCritical("");
        _logger.LogCritical(
            "LayoutSync will NOT write to a non-localhost RavenDB without explicit consent."
        );
        _logger.LogCritical(
            "If you genuinely intend to sync to this target, re-run with --allow-remote-sync."
        );
        _logger.LogCritical(
            "Safe preflight: --dry-run --allow-remote-sync audits what would change without writing."
        );
        _logger.LogCritical("{Bar}", bar);
    }

    private void EmitAllowBanner(
        ProductionTargetClassification classification,
        string? url,
        bool dryRun,
        string? layout,
        bool preserveIds,
        bool strict,
        bool clean)
    {
        string bar = BannerBar();
        _logger.LogWarning("{Bar}", bar);
        _logger.LogWarning(
            "WARNING: syncing to a {Classification} RavenDB target.",
            classification);
        _logger.LogWarning("{Bar}", bar);
        _logger.LogWarning("RavenDB URL:    {Url}", url ?? "(null)");
        _logger.LogWarning("Layout filter:  {Layout}", string.IsNullOrEmpty(layout) ? "(all)" : layout);
        _logger.LogWarning("Preserve IDs:   {PreserveIds}", preserveIds);
        _logger.LogWarning("Strict mode:    {Strict}", strict);
        _logger.LogWarning("Clean mode:     {Clean}", clean);
        _logger.LogWarning("Dry-run:        {DryRun}", dryRun);
        if (dryRun)
        {
            _logger.LogWarning("(dry-run: no writes will be issued)");
        }
        _logger.LogWarning("{Bar}", bar);
    }

    /// <summary>
    /// Emits a completion banner mirroring <see cref="EmitAllowBanner"/> so the run's tail
    /// in the log stream makes the Remote target visible even when the startup banner has
    /// scrolled off. Called from <see cref="Program"/> after the sync finishes.
    /// </summary>
    public void EmitCompletionBanner(string? url, bool dryRun)
    {
        ProductionTargetClassification classification = Classify(url);
        if (classification == ProductionTargetClassification.Local)
            return;

        string bar = BannerBar();
        _logger.LogWarning("{Bar}", bar);
        _logger.LogWarning(
            "COMPLETED sync against {Classification} target: {Host}",
            classification,
            SafeHostOf(url));
        if (dryRun)
        {
            _logger.LogWarning("(dry-run: nothing was written)");
        }
        _logger.LogWarning("{Bar}", bar);
    }
}
