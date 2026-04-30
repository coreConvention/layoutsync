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
/// <c>layoutsync manifest set-route</c> / <c>from-json</c> commands.
///
/// Transport: stdio. Logs are routed to stderr so stdout is reserved for MCP framing
/// (any byte on stdout that isn't a JSON-RPC frame would corrupt the protocol).
///
/// Configuration:
/// <list type="bullet">
///   <item><c>LAYOUTSYNC_LAYOUTS_PATH</c> (env var, required) — absolute path to the
///         <c>layouts/</c> directory. The server refuses to start without it.</item>
/// </list>
///
/// Once running, the server registers under the name "layoutsync" in <c>.mcp.json</c>
/// and exposes the tools defined in <see cref="ManifestTools"/> and
/// <see cref="ManifestReadTools"/>.
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

            // MCP server with stdio transport. WithTools<T> registers each tool class.
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<ManifestTools>()
                .WithTools<ManifestReadTools>();

            using IHost host = builder.Build();
            Log.Information("LayoutSync MCP server starting. layouts: {Path}", layoutsPath);
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

        string resolved = Path.IsPathRooted(raw)
            ? raw
            : Path.GetFullPath(raw, Directory.GetCurrentDirectory());

        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException(
                $"LAYOUTSYNC_LAYOUTS_PATH points to a directory that does not exist: {resolved}");
        }

        return resolved;
    }
}

/// <summary>
/// Holds the resolved <c>layouts/</c> path for injection into tool classes. A trivial
/// wrapper rather than a primitive string so DI can disambiguate it from other strings
/// in the container.
/// </summary>
public sealed record LayoutsPathProvider(string Path);
