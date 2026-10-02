namespace Tether.Core.Sync;

/// <summary>How the engine removes a local file that was deleted on another device.</summary>
public interface ILocalTrash
{
    /// <summary>Removes the file (to the Recycle Bin when possible).</summary>
    void Delete(string fullPath);
}

/// <summary>Permanent deletion. The server keeps the content in history/ for the retention period.</summary>
public sealed class PermanentDeleteTrash : ILocalTrash
{
    public void Delete(string fullPath) => File.Delete(fullPath);
}
