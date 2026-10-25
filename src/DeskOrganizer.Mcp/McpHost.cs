using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Diagnostics;

namespace DeskOrganizer.Mcp;

public static class McpHost
{
    public static IHost Build(IDesktopBackend backend, bool enableWrites = false, IOperationStore? operationStore = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(backend);
        builder.Services.AddSingleton(new DesktopAccessPolicy(enableWrites));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<DesktopPlanningService>();
        var mcp = builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<DesktopTools>();
        if (enableWrites)
        {
            if (backend is not IDesktopExecutionBackend writer)
                throw new InvalidOperationException("Writing requires an execution backend.");
            builder.Services.AddSingleton(writer);
            builder.Services.AddSingleton(operationStore ?? new FileOperationStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskOrganizer", "sessions",
                Process.GetCurrentProcess().SessionId.ToString(), "operations")));
            builder.Services.AddSingleton<DesktopExecutionService>();
            mcp.WithTools<DesktopWriteTools>();
        }
        return builder.Build();
    }
}
