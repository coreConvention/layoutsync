using LayoutSync.Mcp.Tools;
using LayoutSync.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LayoutSync.Mcp;

/// <summary>
/// Entry point for the LayoutSync MCP server. This is a thin wrapper over the file
/// mutation services in <c>LayoutSync.Core</c> — every tool method translates an MCP
/// invocation into a call against the same service that powers the CLI's
/// <c>layoutsync manifest</c> subcommands (<c>set-route</c> / <c>from-json</c>,
/// <c>add-section</c> / <c>rename-section</c> / <c>remove-section</c>).
///
/// Transport: stdio. Logs are routed to stderr so stdout is reserved for MCP framing
/// (any byte on stdout that isn't a JSON-RPC frame would corrupt the protocol).
///
/// Configuration:
/// <list type="bullet">
///   <item><c>LAYOUTSYNC_LAYOUTS_PATH</c> (env var, required) — path to the
///         <c>layouts/</c> directory; a relative value resolves against the directory the
///         client launched the server in. The server refuses to start without it. This is
///         only the DEFAULT target: every tool also accepts a <c>layoutsPath</c> argument
///         that overrides it per call, for sessions editing a different git worktree than
///         the one the server was launched in (issue #29, <see cref="LayoutsPathProvider"/>).</item>
/// </list>
///
/// Once running, the server registers under the name "layoutsync" in <c>.mcp.json</c>
/// and exposes the tools defined in <see cref="ManifestTools"/>,
/// <see cref="ManifestSectionTools"/> and <see cref="ManifestReadTools"/>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Serilog → stderr only. stdout is reserved for MCP JSON-RPC framing.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();

            // Resolve the layouts path up-front so a misconfiguration fails fast,
            // before any tool is ever invoked.
            string layoutsPath = ResolveLayoutsPath();
            builder.Services.AddSingleton(new LayoutsPathProvider(layoutsPath));

            // Core services from LayoutSync.Core — same DI graph as the CLI's
            // ManifestCommands.BuildManifestHost.
            builder.Services.AddSingleton<LocalFileService>();
            builder.Services.AddSingleton<ManifestSectionValidator>();
            builder.Services.AddSingleton<ManifestMutationService>();
            builder.Services.AddSingleton<ManifestSectionRegistryService>();

            // MCP server with stdio transport. WithTools<T> registers each tool class.
            // The instructions carry the one fact no static tool description can: the
            // concrete default path, so an agent can compare it with the checkout it is
            // editing before its first call (issue #29).
            builder.Services
                .AddMcpServer(options => options.ServerInstructions = BuildServerInstructions(layoutsPath))
                .WithStdioServerTransport()
                .WithTools<ManifestTools>()
                .WithTools<ManifestSectionTools>()
                .WithTools<ManifestReadTools>();

            using IHost host = builder.Build();
            Log.Information(
                "LayoutSync MCP server starting. Default layouts: {Path} (tools accept a per-call layoutsPath override).",
                layoutsPath);
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "LayoutSync MCP server failed to start.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Resolves the <c>layouts/</c> directory from the <c>LAYOUTSYNC_LAYOUTS_PATH</c>
    /// environment variable. Throws when unset or when the path doesn't exist —
    /// failing fast prevents silent "tool just returns errors" runtime behavior.
    /// </summary>
    private static string ResolveLayoutsPath()
    {
        string? raw = Environment.GetEnvironmentVariable("LAYOUTSYNC_LAYOUTS_PATH");
        if (string.IsNullOrEmpty(raw))
        {
            throw new InvalidOperationException(
                "LAYOUTSYNC_LAYOUTS_PATH environment variable is required. "
                + "Set it to the absolute path of your layouts/ directory in .mcp.json's env block.");
        }

        // GetFullPath normalizes absolute input too (separators, "..", trailing slash), so
        // the default echoes in manifestPath in the same form as a per-call layoutsPath.
        string resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(raw, Directory.GetCurrentDirectory()));

        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException(
                $"LAYOUTSYNC_LAYOUTS_PATH points to a directory that does not exist: {resolved}");
        }

        return resolved;
    }

    /// <summary>
    /// Server instructions sent to the client during the MCP handshake. Clients such as
    /// Claude Code surface them to the model as standing context.
    /// </summary>
    internal static string BuildServerInstructions(string defaultLayoutsPath)
        => $"The manifest tools default to this layouts directory: {defaultLayoutsPath}. It belongs "
           + "to the checkout this server was launched in (normally where the session started), which "
           + "is not necessarily the checkout you are editing. When you edit a different git worktree, "
           + "pass that worktree's layouts/ directory as layoutsPath on every manifest tool call, and "
           + "check the manifestPath in each response.";
}
