using Tether.Server;
using Tether.Server.Cli;

if (CliCommands.IsCliCommand(args))
    return await CliCommands.RunAsync(args, Console.Out, Console.Error);

WebApplication app;
try
{
    app = TetherServerHost.Build(args);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("tether-server cannot start: " + ex.Message);
    return 1;
}

await app.RunAsync();
return 0;

/// <summary>Entry point marker (lets tests reference the assembly's Program type).</summary>
public partial class Program;
