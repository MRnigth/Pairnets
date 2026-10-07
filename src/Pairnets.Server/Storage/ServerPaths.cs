namespace Pairnets.Server.Storage;

/// <summary>Layout of the data directory.</summary>
public sealed class ServerPaths
{
    public ServerPaths(string dataDir)
    {
        DataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        Files = Path.Combine(DataDir, "files");
        History = Path.Combine(DataDir, "history");
        Tmp = Path.Combine(DataDir, "tmp");
        Database = Path.Combine(DataDir, "manifest.db");
        AuthDatabase = Path.Combine(DataDir, "auth.db");
        LockFile = Path.Combine(DataDir, ".lock");
    }

    public string DataDir { get; }

    /// <summary>Current version of every file, same relative paths as on the clients.</summary>
    public string Files { get; }

    /// <summary>Previous versions and deleted files: history/&lt;path&gt;/&lt;UTC timestamp&gt;-&lt;hash8&gt;.</summary>
    public string History { get; }

    /// <summary>In-flight uploads; emptied at startup.</summary>
    public string Tmp { get; }

    public string Database { get; }

    /// <summary>Computer keys and, later, sign-in data; kept apart from the file manifest.</summary>
    public string AuthDatabase { get; }

    public string LockFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(History);
        Directory.CreateDirectory(Tmp);
    }
}
