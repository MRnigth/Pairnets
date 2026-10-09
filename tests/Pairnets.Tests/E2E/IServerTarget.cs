using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// A test server the API tour and the browser tests run against (see docs/PREDEPLOY.md, "Test servers"): the server
/// in the test process, the real program as its own process, or the server installed on the Linux test box. Each
/// has its website on https://localhost:&lt;port&gt;, a fake mail server and a fake Google.
/// </summary>
public interface IServerTarget : IAsyncDisposable
{
    /// <summary>"in-process", "process" or "installed-linux".</summary>
    string Name { get; }

    /// <summary>The plain-HTTP API (what the tunnel brings the apps to).</summary>
    Uri ApiUrl { get; }

    /// <summary>The nest's website, https://localhost:&lt;port&gt;/, also its public name.</summary>
    Uri WebsiteUrl { get; }

    /// <summary>The old shared token.</summary>
    string Token { get; }

    /// <summary>True when "Update server" really runs the root updater (the installed server); otherwise it only leaves its request.</summary>
    bool RealUpdater { get; }

    /// <summary>Every mail the fake mail server got, oldest first.</summary>
    Task<IReadOnlyList<RawMail>> MailsAsync();

    /// <summary>Runs <c>pairnets-server &lt;args&gt; --data-dir &lt;the data folder&gt;</c> the way an admin would on that server.</summary>
    Task<CliResult> RunCliAsync(params string[] args);

    /// <summary>Stops the server (systemctl stop); the maintenance commands that need it stopped can run then.</summary>
    Task StopServerAsync();

    /// <summary>Starts it again and waits until it answers.</summary>
    Task StartServerAsync();

    /// <summary>Writes a file into the data folder as the server's own user ("files/notes.txt").</summary>
    Task WriteDataFileAsync(string relative, byte[] content);

    /// <summary>Renames a file in the data folder.</summary>
    Task MoveDataFileAsync(string from, string to);

    /// <summary>True when "Update server" left its request for the updater.</summary>
    Task<bool> UpdateRequestedAsync();

    /// <summary>The server's log so far (for "never logs a secret" and the request lines).</summary>
    Task<IReadOnlyList<string>> ServerLogAsync();

    /// <summary>Puts what the server itself saw into <paramref name="record"/>; false when this target cannot tell.</summary>
    Task<bool> CollectServerSideAsync(TourRecord record);
}

/// <summary>The PAIRNETS_E2E_* settings from docs/PREDEPLOY.md.</summary>
public static class E2EEnvironment
{
    public const string InstalledLinux = "installed-linux";

    /// <summary>True when the pre-deploy runner points the tests at the Linux test box.</summary>
    public static bool WantsInstalledLinux =>
        string.Equals(Environment.GetEnvironmentVariable("PAIRNETS_E2E_TARGET"), InstalledLinux, StringComparison.OrdinalIgnoreCase);

    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} must be set for the {InstalledLinux} target (see docs/PREDEPLOY.md).");
}
