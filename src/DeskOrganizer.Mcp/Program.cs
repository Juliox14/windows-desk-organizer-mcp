using DeskOrganizer;
using DeskOrganizer.Mcp;
using Microsoft.Extensions.Hosting;

if (args is ["--desktop-worker"] or ["--desktop-worker-write"])
    return await DesktopWorker.RunAsync(args[0] == "--desktop-worker-write");

bool enableWrites = args is ["--enable-writes"];
if (args.Length != 0 && !enableWrites)
{
    Console.Error.WriteLine("Usage: DeskOrganizer.Mcp [--enable-writes] (stdio server)");
    return 1;
}

string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process executable unavailable.");
string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? new[] { typeof(DesktopTools).Assembly.Location } : Array.Empty<string>();
using var host = McpHost.Build(new WorkerProcessBackend(new WorkerLaunch(executable, prefix)), enableWrites);
await host.RunAsync();
return 0;
