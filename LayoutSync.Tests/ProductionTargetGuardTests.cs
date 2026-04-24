using LayoutSync.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="ProductionTargetGuard"/>. Two surfaces are covered:
///
/// <list type="bullet">
///   <item><description><see cref="ProductionTargetGuard.Classify"/> — pure URL shape
///     classification. Covers loopback literals, RFC1918 private-network ranges, public
///     FQDNs, and malformed URLs.</description></item>
///   <item><description><see cref="ProductionTargetGuard.Authorize"/> — gate behavior.
///     Verifies that Remote/Unknown targets require <c>--allow-remote-sync</c>, that Local
///     targets pass without any flag, and that the allow-banner fires (WARN-level) when
///     an operator opts in.</description></item>
/// </list>
///
/// The guard is host-based and tenant-agnostic — no tenant slug, layout identifier, or
/// hostname pattern referencing a specific tenant ever appears in these tests. A change
/// that introduces one would violate STOP Rule #23.
/// </summary>
public class ProductionTargetGuardTests
{
    // ── Classify: loopback literals ──────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://LOCALHOST:8080")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1")]
    public void Classify_LoopbackLiterals_ReturnsLocal(string url)
    {
        Assert.Equal(
            ProductionTargetClassification.Local,
            ProductionTargetGuard.Classify(url));
    }

    [Fact]
    public void Classify_IPv6Loopback_ReturnsLocal()
    {
        // IPv6 loopback in a URL is bracket-wrapped per RFC 3986.
        Assert.Equal(
            ProductionTargetClassification.Local,
            ProductionTargetGuard.Classify("http://[::1]:8080"));
    }

    // ── Classify: RFC1918 private-network IPv4 ranges ────────────────────────

    [Theory]
    [InlineData("http://10.0.0.5:443")]
    [InlineData("http://10.255.255.255")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    [InlineData("http://192.168.1.10:443")]
    [InlineData("http://192.168.0.1")]
    public void Classify_PrivateIPv4_ReturnsLocal(string url)
    {
        Assert.Equal(
            ProductionTargetClassification.Local,
            ProductionTargetGuard.Classify(url));
    }

    [Theory]
    [InlineData("http://11.0.0.1")]      // Just outside 10.0.0.0/8
    [InlineData("http://172.15.0.1")]    // Just below 172.16-31.x
    [InlineData("http://172.32.0.1")]    // Just above 172.16-31.x
    [InlineData("http://192.167.0.1")]   // Just outside 192.168/16
    [InlineData("http://8.8.8.8")]       // Google DNS — obvious public
    public void Classify_PublicIPv4_ReturnsRemote(string url)
    {
        Assert.Equal(
            ProductionTargetClassification.Remote,
            ProductionTargetGuard.Classify(url));
    }

    // ── Classify: public FQDNs ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://production-ravendb.example.com")]
    [InlineData("https://w31rd-prod.ravendb.community:443")]
    [InlineData("https://foo.ravendb.net")]
    [InlineData("https://some-app.azurewebsites.net")]
    public void Classify_PublicFqdn_ReturnsRemote(string url)
    {
        Assert.Equal(
            ProductionTargetClassification.Remote,
            ProductionTargetGuard.Classify(url));
    }

    // ── Classify: malformed URLs ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("://missing-scheme")]
    public void Classify_MalformedUrl_ReturnsUnknown(string? url)
    {
        Assert.Equal(
            ProductionTargetClassification.Unknown,
            ProductionTargetGuard.Classify(url));
    }

    // ── Authorize: gate behavior ─────────────────────────────────────────────

    [Fact]
    public void Authorize_LocalTarget_NoFlag_ReturnsTrueAndEmitsNoWarningBanner()
    {
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        bool allowed = guard.Authorize(
            url: "http://localhost:8080",
            allowRemoteSync: false,
            dryRun: false,
            layout: null,
            preserveIds: false,
            strict: false,
            clean: false);

        Assert.True(allowed);
        // Local → at most an Information-level classification line. No WARN banner, no
        // Critical refusal banner.
        Assert.Empty(logger.WarningEntries);
        Assert.Empty(logger.CriticalEntries);
    }

    [Fact]
    public void Authorize_RemoteTarget_NoFlag_ReturnsFalseAndEmitsFatalBanner()
    {
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        bool allowed = guard.Authorize(
            url: "https://production-ravendb.example.com",
            allowRemoteSync: false,
            dryRun: false,
            layout: null,
            preserveIds: false,
            strict: false,
            clean: false);

        Assert.False(allowed);

        // FATAL banner must mention the refusal and the opt-in flag.
        string joined = string.Join("\n", logger.CriticalEntries);
        Assert.Contains("REMOTE TARGET DETECTED", joined);
        Assert.Contains("--allow-remote-sync", joined);
        Assert.Contains("production-ravendb.example.com", joined);
    }

    [Fact]
    public void Authorize_UnknownTarget_NoFlag_ReturnsFalse()
    {
        // Unknown classification must be treated as Remote for safety — an operator
        // mistyping a URL should never cause a write against something we can't identify.
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        bool allowed = guard.Authorize(
            url: "not a url",
            allowRemoteSync: false,
            dryRun: false,
            layout: null,
            preserveIds: false,
            strict: false,
            clean: false);

        Assert.False(allowed);
        Assert.Contains(
            logger.CriticalEntries,
            e => e.Contains("REMOTE TARGET DETECTED", StringComparison.Ordinal));
    }

    [Fact]
    public void Authorize_RemoteTarget_WithFlag_ReturnsTrueAndEmitsWarningBanner()
    {
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        bool allowed = guard.Authorize(
            url: "https://production-ravendb.example.com",
            allowRemoteSync: true,
            dryRun: false,
            layout: "some-layout",
            preserveIds: true,
            strict: true,
            clean: false);

        Assert.True(allowed);

        // With opt-in, we expect a WARN banner (not a Critical refusal), and the banner
        // must surface all the operator-relevant state so a misuse is visible at a glance.
        string joined = string.Join("\n", logger.WarningEntries);
        Assert.Contains("Remote", joined);
        Assert.Contains("production-ravendb.example.com", joined);
        Assert.Contains("some-layout", joined);
        Assert.Contains("True", joined); // preserveIds / strict propagate
        Assert.Empty(logger.CriticalEntries);
    }

    [Fact]
    public void Authorize_RemoteTarget_WithFlag_DryRun_MentionsDryRunInBanner()
    {
        // --dry-run --allow-remote-sync is the explicit "audit prod" workflow. The banner
        // should make it obvious no writes will be issued so operators don't worry when
        // they see the WARN lines.
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        bool allowed = guard.Authorize(
            url: "https://production-ravendb.example.com",
            allowRemoteSync: true,
            dryRun: true,
            layout: null,
            preserveIds: false,
            strict: false,
            clean: false);

        Assert.True(allowed);
        string joined = string.Join("\n", logger.WarningEntries);
        Assert.Contains("dry-run", joined, StringComparison.OrdinalIgnoreCase);
    }

    // ── Completion banner ────────────────────────────────────────────────────

    [Fact]
    public void EmitCompletionBanner_LocalTarget_EmitsNothing()
    {
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        guard.EmitCompletionBanner("http://localhost:8080", dryRun: false);

        Assert.Empty(logger.WarningEntries);
        Assert.Empty(logger.CriticalEntries);
    }

    [Fact]
    public void EmitCompletionBanner_RemoteTarget_EmitsWarningBanner()
    {
        CapturingLogger logger = new();
        ProductionTargetGuard guard = new(logger);

        guard.EmitCompletionBanner("https://production-ravendb.example.com", dryRun: false);

        string joined = string.Join("\n", logger.WarningEntries);
        Assert.Contains("COMPLETED", joined);
        Assert.Contains("production-ravendb.example.com", joined);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that records formatted messages for WARN and
    /// Critical levels. Debug/Info are ignored because the guard's operator-facing signal
    /// lives on WARN (opt-in banner) and Critical (refusal banner).
    /// </summary>
    private sealed class CapturingLogger : ILogger<ProductionTargetGuard>
    {
        public List<string> WarningEntries { get; } = [];
        public List<string> CriticalEntries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state)!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
                WarningEntries.Add(message);
            else if (logLevel == LogLevel.Critical)
                CriticalEntries.Add(message);
        }
    }
}
