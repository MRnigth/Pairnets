using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using FileAttributes = System.IO.FileAttributes;

namespace Pairnets.Client.Platform.CloudFiles;

/// <summary>
/// The Pairnets folder as a Windows cloud folder, the way OneDrive does it: registered with Windows (Pairnets shows in
/// Explorer as a storage service), every file a "placeholder" whose tick, sync arrows or cloud Windows draws from what
/// Pairnets tells it, and, with Files On-Demand, files whose content stays on the nest until they are opened.
/// <para>
/// The folder's files are never copied or moved for this: they are marked where they are, and with
/// <see cref="LeaveAsync"/> they become plain files again. Windows calls back here when someone opens a file whose
/// content is on the nest (<see cref="Fetch"/> downloads it), and when it wants to free space on the drive.
/// </para>
/// <para>
/// Each placeholder carries the version it stands for and the path it had on the nest then ("hash\npath"), so a file
/// still on the nest can always be fetched, even after it was renamed or moved here.
/// </para>
/// </summary>
public sealed class WindowsCloudFolder : ICloudFolder
{
    /// <summary>The first part of every Pairnets cloud folder's id with Windows: "Pairnets!&lt;user&gt;!&lt;folder&gt;".</summary>
    public const string ProviderName = "Pairnets";

    private const int ChunkSize = 1024 * 1024; // a multiple of 4 KB, as Windows asks for every piece but the last

    private readonly string _root;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _fetches = new();
    private readonly ConcurrentDictionary<string, byte> _pinQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pinSignal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _reconcileGate = new();

    /// <summary>The version each file was last marked with here, so a check does not open every file to read it back.</summary>
    private readonly ConcurrentDictionary<string, string> _marked = new(StringComparer.OrdinalIgnoreCase);
    private readonly CfApi.Callback _onFetch;
    private readonly CfApi.Callback _onCancel;
    private readonly CfApi.Callback _onDehydrate;
    private long _connection;
    private volatile bool _connected;
    private FileSystemWatcher? _pins;
    private Task _applying = Task.CompletedTask;
    private bool _reconciling;
    private Func<string, string?>? _reconcileAgain;

    private WindowsCloudFolder(string root, CloudFolderMode mode, ILogger log)
    {
        _root = LongPath(root);
        Mode = mode;
        _log = log;
        // Kept in fields: Windows holds pointers to them for as long as the folder is connected.
        _onFetch = OnFetchData;
        _onCancel = OnCancelFetchData;
        _onDehydrate = OnNotifyDehydrate;
    }

    public CloudFolderMode Mode { get; }

    public string Root => _root;

    /// <summary>
    /// Downloads one version of a file from the nest into a stream, when Windows asks for a file's content (someone
    /// opened it, or "Always keep on this device"): (sync path on the nest, version hash, where to, bytes so far, stop).
    /// Set by the sync session once it runs; until then opening such a file fails (the opener can try again).
    /// </summary>
    public Func<string, string, Stream, Action<long>, CancellationToken, Task>? Fetch { get; set; }

    public bool IsActive => _connected;

    // ------------------------------------------------------------------ starting and leaving

    /// <summary>
    /// Why the folder cannot be a Windows cloud folder (it stays a plain folder then), or null when it can: it must be
    /// on a fixed NTFS drive of this PC, and not inside (or around) another cloud folder such as OneDrive.
    /// </summary>
    public static string? WhyNot(string root)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return "It needs Windows 10 (version 1809) or newer.";
        try
        {
            var full = LongPath(root);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return "Your Pairnets folder is on a network drive. Windows only does this on this PC's own drives.";
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType != DriveType.Fixed)
                return "Your Pairnets folder is on a removable or network drive. Windows only does this on this PC's own drives.";
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return $"Your Pairnets folder's drive uses {drive.DriveFormat}. Windows only does this on NTFS drives.";
            foreach (var (id, path, name) in AllSyncRoots())
            {
                if (id.StartsWith(ProviderName + "!", StringComparison.Ordinal))
                    continue;
                if (IsSameOrInside(full, path) || IsSameOrInside(path, full))
                    return $"Your Pairnets folder is inside (or around) another cloud folder ({name}, {path}). Windows allows only one.";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or COMException)
        {
            return "Windows could not check the folder: " + ex.Message;
        }
    }

    /// <summary>
    /// A folder's full path with every part in its long form ("C:\PROGRA~1" becomes "C:\Program Files"), so two ways of
    /// writing the same folder compare equal.
    /// </summary>
    public static string LongPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(full, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? Path.TrimEndingDirectorySeparator(buffer.ToString()) : full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathNameW(string shortPath, StringBuilder longPath, int bufferLength);

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>This Windows user's id for the folder with Windows (one Pairnets folder per user, keyed by its path).</summary>
    public static string SyncRootId(string root)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LongPath(root).ToUpperInvariant())))[..16];
        return $"{ProviderName}!{UserSid}!{key}";
    }

    /// <summary>The Pairnets cloud folders this Windows user has with Windows (normally none or one).</summary>
    public static IReadOnlyList<(string Id, string Path)> Registered()
    {
        var prefix = $"{ProviderName}!{UserSid}!";
        return AllSyncRoots().Where(r => r.Id.StartsWith(prefix, StringComparison.Ordinal)).Select(r => (r.Id, r.Path)).ToList();
    }

    private static string UserSid => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";

    private const string SyncRootManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>
    /// Every cloud folder this Windows user has (OneDrive, Pairnets, ...), read from Windows' own list in the registry, which
    /// any program may read; Windows' API for it (GetCurrentSyncRoots) answers empty in some programs, such as test hosts.
    /// </summary>
    private static List<(string Id, string Path, string Name)> AllSyncRoots()
    {
        var found = new List<(string, string, string)>();
        try
        {
            using var manager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SyncRootManagerKey);
            foreach (var id in manager?.GetSubKeyNames() ?? [])
            {
                using var root = manager!.OpenSubKey(id);
                using var users = root?.OpenSubKey("UserSyncRoots");
                if (users?.GetValue(UserSid) is string path && path.Length > 0)
                    found.Add((id, LongPath(path), root!.GetValue("DisplayNameResource") as string ?? id));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Windows' API below is the second way.
        }
        if (found.Count > 0)
            return found;
        try
        {
            return StorageProviderSyncRootManager.GetCurrentSyncRoots()
                .Select(r => (r.Id, LongPath(r.Path.Path), r.DisplayNameResource))
                .ToList();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            return found;
        }
    }

    /// <summary>
    /// Makes <paramref name="root"/> a Windows cloud folder (or connects to it again after a restart) and returns it, or
    /// null with <paramref name="problem"/> saying why it stays a plain folder. Files already there are marked where
    /// they are, in the background (<see cref="Reconcile"/>); so is the mode's keep-here choice on every file.
    /// </summary>
    public static WindowsCloudFolder? Start(string root, CloudFolderMode mode, string iconPath, ILogger log, out string? problem)
    {
        problem = WhyNot(root);
        if (problem is not null)
            return null;
        var folder = new WindowsCloudFolder(root, mode, log);
        try
        {
            folder.Register(iconPath);
            folder.Connect();
            folder.WatchPins();
            folder._applying = Task.Run(folder.ApplyMode); // a whole folder tree: not on the window's thread
            return folder;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            problem = "Windows did not accept the Pairnets folder as a cloud folder: " + ex.Message;
            log.LogWarning(ex, "Could not make {Folder} a cloud folder", root);
            folder.Dispose();
            return null;
        }
    }

    private void Register(string iconPath)
    {
        var id = SyncRootId(_root);
        foreach (var (otherId, path) in Registered())
        {
            if (otherId != id || !string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Taking the old Pairnets cloud folder {Path} away from Windows", path);
                StorageProviderSyncRootManager.Unregister(otherId);
            }
        }
        if (Registered().Any(r => r.Id == id))
            return;
        // The WinRT call finishes on a pool thread, so waiting here (also on the window's thread) cannot deadlock.
        var folder = Task.Run(() => StorageFolder.GetFolderFromPathAsync(_root).AsTask()).GetAwaiter().GetResult();
        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = folder,
            DisplayNameResource = "Pairnets",
            IconResource = iconPath + ",0",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.Default,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Version = "1",
            AllowPinning = true,
            ProtectionMode = StorageProviderProtectionMode.Personal,
            Context = CryptographicBuffer.ConvertStringToBinary(id, BinaryStringEncoding.Utf8),
        };
        StorageProviderSyncRootManager.Register(info);
        _log.LogInformation("{Folder} is now a Windows cloud folder ({Id})", _root, id);
    }

    private void Connect()
    {
        var table = new[]
        {
            new CfApi.CallbackRegistration { Type = CfApi.CallbackFetchData, Callback = Marshal.GetFunctionPointerForDelegate(_onFetch) },
            new CfApi.CallbackRegistration { Type = CfApi.CallbackCancelFetchData, Callback = Marshal.GetFunctionPointerForDelegate(_onCancel) },
            new CfApi.CallbackRegistration { Type = CfApi.CallbackNotifyDehydrate, Callback = Marshal.GetFunctionPointerForDelegate(_onDehydrate) },
            new CfApi.CallbackRegistration { Type = CfApi.CallbackNone, Callback = IntPtr.Zero },
        };
        // Pairnets itself never fetches a file by reading it (only on purpose, with CfHydratePlaceholder): a mistake in
        // the sync engine then fails instead of downloading every file.
        var hr = CfApi.CfConnectSyncRoot(_root, table, IntPtr.Zero, CfApi.ConnectFlagRequireFullFilePath | CfApi.ConnectFlagBlockSelfImplicitHydration, out _connection);
        Marshal.ThrowExceptionForHR(hr);
        _connected = true;
    }

    /// <summary>
    /// Keep everything here: the folder is "Always keep on this device", so Windows never frees space in it, and anything
    /// still on the nest is fetched. Files On-Demand: the folder's own choice is cleared, so files can be freed.
    /// Pairnets' own files are always kept here.
    /// </summary>
    private void ApplyMode()
    {
        try
        {
            var pinned = File.GetAttributes(_root).HasFlag(CloudPlaceholders.Pinned);
            if (Mode == CloudFolderMode.KeepAllHere && !pinned)
                WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStatePinned, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
            else if (Mode == CloudFolderMode.OnDemand && pinned)
                WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStateUnspecified, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
            if (Mode == CloudFolderMode.KeepAllHere)
                FetchEverything();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not apply the keep-here choice to {Folder}: {Error}", _root, ex.Message);
        }
    }

    /// <summary>Fetches every file still only on the nest (keeping everything here, or leaving); false when one could not.</summary>
    private bool FetchEverything()
    {
        var ok = true;
        foreach (var file in AllFiles().Where(IsCloudOnly).ToList())
        {
            if (!_connected || _stop.IsCancellationRequested)
                return false;
            ok &= Hydrate(file);
        }
        return ok;
    }

    /// <summary>
    /// Turns the folder back into a plain folder: every file still on the nest is downloaded first (it fails, and the
    /// folder stays a cloud folder, when that is not possible), then every placeholder file and folder becomes a plain
    /// one again and Windows forgets the folder. Returns null when done, or what stopped it.
    /// </summary>
    public Task<string?> LeaveAsync(CancellationToken ct) => Task.Run(() =>
    {
        _applying.Wait(ct); // the start's own work on the folder is finished first
        if (!_connected)
            return null;
        WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStatePinned, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
        foreach (var file in AllFiles().Where(IsCloudOnly).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!Hydrate(file))
            {
                var left = AllFiles().Count(IsCloudOnly);
                return $"{left} file(s) are still only on your nest (for example {Path.GetFileName(file)}). Pairnets needs to reach your nest to download them first.";
            }
        }
        ct.ThrowIfCancellationRequested();
        Disconnect();
        // Files become plain files again. Windows may keep its cloud mark on folders after this (checked on Windows 11);
        // they are ordinary folders to everyone, Pairnets' scanner included, and every file in them is here.
        var stuck = RevertAll();
        UnregisterAll();
        return stuck == 0 ? null : $"{stuck} file(s) could not be turned back into plain files (they were in use). They still open normally.";
    }, ct);

    /// <summary>
    /// For the uninstaller: how many files in Pairnets cloud folders are only on the nest (they could not open
    /// after Pairnets is gone).
    /// </summary>
    public static int CountCloudOnly()
    {
        var count = 0;
        foreach (var (_, path) in Registered())
        {
            if (Directory.Exists(path))
                count += EnumerateFiles(path).Count(f => CloudPlaceholders.TryGetAttributesAndTag(f, out var a, out _) && CloudPlaceholders.IsContentOnline(a));
        }
        return count;
    }

    /// <summary>
    /// For the uninstaller and "Reset": every Pairnets cloud folder becomes a plain folder again, without asking the nest
    /// for anything (files still only on the nest stay behind as placeholders). Returns how many files were left.
    /// </summary>
    public static int ForgetAll()
    {
        var left = 0;
        foreach (var (id, path) in Registered())
        {
            if (Directory.Exists(path))
                left += new WindowsCloudFolder(path, CloudFolderMode.Off, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RevertAll();
            try
            {
                StorageProviderSyncRootManager.Unregister(id);
            }
            catch (COMException)
            {
                left++;
            }
        }
        return left;
    }

    private static void UnregisterAll()
    {
        foreach (var (id, _) in Registered())
            StorageProviderSyncRootManager.Unregister(id);
    }

    /// <summary>
    /// Every placeholder file and folder whose content is here becomes a plain one again, deepest first; returns how many
    /// files could not (in use, or still only on the nest).
    /// </summary>
    private int RevertAll()
    {
        var stuck = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        var entries = Directory.EnumerateFileSystemEntries(_root, "*", options)
            .OrderByDescending(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .Append(_root)
            .ToList();
        foreach (var path in entries)
        {
            if (!CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag) || !CloudPlaceholders.IsCloudTag(tag))
                continue;
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            if (!isFolder && CloudPlaceholders.IsContentOnline(attributes))
            {
                stuck++;
                continue;
            }
            if (!Revert(path, isFolder) && !isFolder)
                stuck++;
        }
        return stuck;
    }

    /// <summary>
    /// Makes one placeholder a plain file or folder again, and checks that it is: through the cloud files API's own
    /// handle first, then through a plain one (an attribute and write handle, which is all Windows asks for).
    /// </summary>
    private bool Revert(string path, bool isFolder)
    {
        var hr = 0;
        WithHandle(path, h => Check(hr = CfApi.CfRevertPlaceholder(h, 0, IntPtr.Zero)));
        if (StillCloud(path))
        {
            using var handle = CreateFileW(path, 0x0002 /* FILE_WRITE_DATA */ | 0x0100 /* FILE_WRITE_ATTRIBUTES */ | 0x0080 /* FILE_READ_ATTRIBUTES */,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                isFolder ? 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */ : 0, IntPtr.Zero);
            if (!handle.IsInvalid)
                hr = CfApi.CfRevertPlaceholder(handle.DangerousGetHandle(), 0, IntPtr.Zero);
            else
                hr = Marshal.GetHRForLastWin32Error();
        }
        if (!StillCloud(path))
            return true;
        if (!isFolder)
            _log.LogWarning("{Path} could not be made a plain file again (0x{Result:X8})", path, hr);
        return false;
    }

    private static bool StillCloud(string path) =>
        CloudPlaceholders.TryGetAttributesAndTag(path, out _, out var tag) && CloudPlaceholders.IsCloudTag(tag);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, int access, FileShare share, IntPtr security,
        FileMode mode, int flags, IntPtr template);

    private void Disconnect()
    {
        _pins?.Dispose();
        _pins = null;
        if (!_connected)
            return;
        _connected = false;
        _stop.Cancel();
        foreach (var fetch in _fetches.Values)
            TryCancel(fetch);
        CfApi.CfDisconnectSyncRoot(_connection);
    }

    public void Dispose() => Disconnect();

    // ------------------------------------------------------------------ what the sync engine asks

    public bool IsCloudOnly(string fullPath) =>
        CloudPlaceholders.TryGetAttributesAndTag(fullPath, out var attributes, out _) && (attributes & FileAttributes.Directory) == 0
        && CloudPlaceholders.IsContentOnline(attributes);

    public string? CloudOnlyVersion(string fullPath) => IsCloudOnly(fullPath) ? ReadIdentity(fullPath)?.Hash : null;

    /// <summary>"hash\npath": the version and where it was on the nest.</summary>
    private byte[] IdentityFor(string fullPath, string hash) =>
        Encoding.UTF8.GetBytes(hash + "\n" + (FileStatusReader.ToSyncPath(_root, LongPath(fullPath)) ?? string.Empty));

    private static (string Hash, string? Path)? ParseIdentity(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
            return null;
        var split = identity.IndexOf('\n');
        return split < 0 ? (identity, null) : (identity[..split], identity[(split + 1)..] is { Length: > 0 } p ? p : null);
    }

    private static (string Hash, string? Path)? ReadIdentity(string fullPath)
    {
        string? identity = null;
        WithHandle(fullPath, h =>
        {
            var buffer = new byte[4096];
            if (!CfApi.Ok(CfApi.CfGetPlaceholderInfo(h, CfApi.PlaceholderInfoBasic, buffer, buffer.Length, out _)))
                return false;
            var length = BitConverter.ToInt32(buffer, CfApi.BasicInfoIdentityLengthOffset);
            if (length > 0 && length <= buffer.Length - CfApi.BasicInfoIdentityOffset)
                identity = Encoding.UTF8.GetString(buffer, CfApi.BasicInfoIdentityOffset, length);
            return true;
        }, CfApi.OpenFlagNone);
        return ParseIdentity(identity);
    }

    public void MarkSynced(string fullPath, string hash)
    {
        if (!_connected)
            return;
        var identity = IdentityFor(fullPath, hash);
        var ok = WithHandle(fullPath, h =>
        {
            var state = PlaceholderState(fullPath);
            if ((state & CfApi.PlaceholderStatePlaceholder) == 0)
                return Check(CfApi.CfConvertToPlaceholder(h, identity, identity.Length, CfApi.ConvertFlagMarkInSync, IntPtr.Zero, IntPtr.Zero));
            return Check(CfApi.CfUpdatePlaceholder(h, IntPtr.Zero, identity, identity.Length, IntPtr.Zero, 0, CfApi.UpdateFlagMarkInSync, IntPtr.Zero, IntPtr.Zero));
        }, CfApi.OpenFlagExclusive | CfApi.OpenFlagWriteAccess); // nobody writes to it meanwhile
        if (ok)
            _marked[fullPath] = hash;
        else
            _log.LogDebug("Could not mark {Path} as synced for Explorer yet (in use?); the next check does", fullPath);
        if (IsOwnFile(fullPath))
            WithHandle(fullPath, h => Check(CfApi.CfSetPinState(h, CfApi.PinStatePinned, 0, IntPtr.Zero))); // never freed
        MarkFoldersAbove(fullPath);
    }

    public bool KeepOnNest(string fullPath)
    {
        if (!_connected || Mode != CloudFolderMode.OnDemand || IsOwnFile(fullPath))
            return false;
        // The nearest file or folder that says "Always keep on this device" or "Free up space" decides; none: on the nest.
        for (var path = fullPath; path.Length >= _root.Length; path = Path.GetDirectoryName(path) ?? string.Empty)
        {
            if (CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out _))
            {
                if (attributes.HasFlag(CloudPlaceholders.Pinned))
                    return false;
                if (attributes.HasFlag(CloudPlaceholders.Unpinned))
                    return true;
            }
            if (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return true;
    }

    public bool PlaceOnline(string fullPath, string hash, long size, DateTime modifiedUtc)
    {
        if (!_connected)
            return false;
        var identity = IdentityFor(fullPath, hash);
        var time = modifiedUtc.ToFileTimeUtc();
        var metadata = new CfApi.FsMetadata
        {
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
            FileAttributes = 0x80, // FILE_ATTRIBUTE_NORMAL
            FileSize = size,
        };
        bool ok;
        if (File.Exists(fullPath))
        {
            // A placeholder of an older version: the new version's details, content left on the nest. Only while nobody
            // has it open and it is still the untouched placeholder (a change made here meanwhile is never thrown away).
            ok = WithHandle(fullPath, h => IsCloudOnly(fullPath) && Check(CfApi.CfUpdatePlaceholder(h, ref metadata, identity, identity.Length,
                IntPtr.Zero, 0, CfApi.UpdateFlagVerifyInSync | CfApi.UpdateFlagMarkInSync | CfApi.UpdateFlagDehydrate, IntPtr.Zero, IntPtr.Zero)),
                CfApi.OpenFlagExclusive | CfApi.OpenFlagWriteAccess);
        }
        else
        {
            var name = Marshal.StringToHGlobalUni(Path.GetFileName(fullPath));
            var id = Marshal.AllocHGlobal(identity.Length);
            try
            {
                Marshal.Copy(identity, 0, id, identity.Length);
                var create = new[]
                {
                    new CfApi.PlaceholderCreateInfo
                    {
                        RelativeFileName = name,
                        FsMetadata = metadata,
                        FileIdentity = id,
                        FileIdentityLength = identity.Length,
                        Flags = CfApi.CreateFlagMarkInSync,
                    },
                };
                ok = Check(CfApi.CfCreatePlaceholders(Path.GetDirectoryName(fullPath)!, create, 1, 0, out var done)) && done == 1 && create[0].Result >= 0;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(id);
            }
        }
        if (ok)
            MarkFoldersAbove(fullPath);
        else
            _log.LogInformation("Could not leave {Path} on the nest; downloading it instead", fullPath);
        return ok;
    }

    /// <summary>The folders between a file and the Pairnets folder are cloud folders too, in sync (folders have nothing to sync).</summary>
    private void MarkFoldersAbove(string fullPath)
    {
        for (var dir = Path.GetDirectoryName(fullPath); dir is not null && dir.Length >= _root.Length; dir = Path.GetDirectoryName(dir))
        {
            MarkFolder(dir);
            if (string.Equals(dir, _root, StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    private void MarkFolder(string dir)
    {
        var state = PlaceholderState(dir);
        if ((state & CfApi.PlaceholderStatePlaceholder) == 0)
            WithHandle(dir, h => Check(CfApi.CfConvertToPlaceholder(h, null, 0, CfApi.ConvertFlagMarkInSync, IntPtr.Zero, IntPtr.Zero)));
        else if ((state & CfApi.PlaceholderStateInSync) == 0)
            WithHandle(dir, h => Check(CfApi.CfSetInSyncState(h, CfApi.InSyncStateInSync, 0, IntPtr.Zero)));
    }

    /// <summary>
    /// Brings every file's mark in line with Pairnets' notes: <paramref name="syncedHash"/> gives a synced file's version
    /// from its sync path, or null when it is not synced (new, changed, or a problem), which then shows sync arrows.
    /// Pairnets' own files count as synced. With everything kept here, a file still on the nest is fetched. Runs in the
    /// background; a call while one runs makes it run once more after.
    /// </summary>
    public void Reconcile(Func<string, string?> syncedHash)
    {
        if (!_connected)
            return;
        lock (_reconcileGate)
        {
            if (_reconciling)
            {
                _reconcileAgain = syncedHash;
                return;
            }
            _reconciling = true;
        }
        _ = Task.Run(() =>
        {
            var next = syncedHash;
            while (true)
            {
                try
                {
                    ReconcileOnce(next);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not update the Explorer marks of {Folder}", _root);
                }
                lock (_reconcileGate)
                {
                    if (_reconcileAgain is null || !_connected)
                    {
                        _reconciling = false;
                        return;
                    }
                    next = _reconcileAgain;
                    _reconcileAgain = null;
                }
            }
        });
    }

    private void ReconcileOnce(Func<string, string?> syncedHash)
    {
        MarkFolder(_root);
        foreach (var path in AllEntries())
        {
            if (!_connected)
                return;
            if (!CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag))
                continue;
            var state = CfApi.CfGetPlaceholderStateFromAttributeTag((int)attributes, (int)tag);
            var isPlaceholder = (state & CfApi.PlaceholderStatePlaceholder) != 0;
            var inSync = isPlaceholder && (state & CfApi.PlaceholderStateInSync) != 0;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!inSync)
                    MarkFolder(path);
                continue;
            }
            var online = CloudPlaceholders.IsContentOnline(attributes);
            if (online)
            {
                if (Mode == CloudFolderMode.KeepAllHere)
                    Hydrate(path); // everything stays here: a file left on the nest (switched modes while offline) comes home
                continue; // its mark and version are kept as they were placed
            }
            var rel = FileStatusReader.ToSyncPath(_root, path);
            var hash = IsOwnFile(path) ? "pairnets" : rel is { Length: > 0 } ? syncedHash(rel) : null;
            if (hash is not null && (!inSync || (_marked.TryGetValue(path, out var marked) ? marked : ReadIdentity(path)?.Hash) != hash))
                MarkSynced(path, hash); // (also a version that changed since it was marked)
            else if (hash is null && inSync)
                WithHandle(path, h => Check(CfApi.CfSetInSyncState(h, CfApi.InSyncStateNotInSync, 0, IntPtr.Zero)));
        }
    }

    /// <summary>Pairnets' own files in the folder (its marker and the folder icon): never synced, never freed.</summary>
    private bool IsOwnFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(Path.GetDirectoryName(LongPath(path)), _root, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(name, PathRules.MarkerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ "Always keep on this device" and "Free up space"

    /// <summary>
    /// Explorer's two choices change a file's or folder's pinned flags; Pairnets then fetches or frees the files. One
    /// worker takes the changes in turn (a whole folder changes at once), and a lost batch (too many at once) is made
    /// up for by looking at the whole folder.
    /// </summary>
    private void WatchPins()
    {
        _pins = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _pins.Changed += (_, e) => QueuePin(e.FullPath);
        _pins.Error += (_, _) => QueuePin(_root);
        _pins.EnableRaisingEvents = true;
        _ = Task.Run(PinWorkerAsync);
    }

    private void QueuePin(string path)
    {
        if (_pinQueue.TryAdd(path, 0))
            _pinSignal.Release();
    }

    private async Task PinWorkerAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pinSignal.WaitAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(300, _stop.Token).ConfigureAwait(false); // Explorer changes a whole folder at once: let it finish
                foreach (var path in _pinQueue.Keys.ToList())
                {
                    _pinQueue.TryRemove(path, out _);
                    FollowPin(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FollowPin(string path)
    {
        try
        {
            var files = Directory.Exists(path) ? EnumerateFiles(path).ToList() : [path];
            foreach (var file in files)
            {
                if (!_connected)
                    return;
                if (!CloudPlaceholders.TryGetAttributesAndTag(file, out var attributes, out _))
                    continue;
                var online = CloudPlaceholders.IsContentOnline(attributes);
                if (online && !KeepOnNest(file))
                    Hydrate(file);
                else if (!online && Mode == CloudFolderMode.OnDemand && attributes.HasFlag(CloudPlaceholders.Unpinned) && !IsOwnFile(file)
                    && (PlaceholderState(file) & CfApi.PlaceholderStateInSync) != 0)
                    WithHandle(file, h => Check(CfApi.CfDehydratePlaceholder(h, 0, CfApi.Eof, 0, IntPtr.Zero)), CfApi.OpenFlagExclusive | CfApi.OpenFlagWriteAccess);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not follow the keep/free choice on {Path}: {Error}", path, ex.Message);
        }
    }

    /// <summary>Downloads a file whose content is on the nest (Windows calls <see cref="OnFetchData"/> for it). False when it could not.</summary>
    private bool Hydrate(string file) =>
        WithHandle(file, h => Check(CfApi.CfHydratePlaceholder(h, 0, CfApi.Eof, 0, IntPtr.Zero)), CfApi.OpenFlagNone);

    // ------------------------------------------------------------------ Windows calling back (never let an error escape: it would end the app)

    private void OnFetchData(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        try
        {
            var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
            var full = FullPathOf(info);
            var identity = ParseIdentity(info.FileIdentityLength > 0 ? Marshal.PtrToStringUTF8(info.FileIdentity, info.FileIdentityLength) : null);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _fetches[info.TransferKey] = stop;
            _ = Task.Run(() => HydrateAsync(info, full, identity, stop));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start a download Windows asked for");
        }
    }

    private async Task HydrateAsync(CfApi.CallbackInfo info, string full, (string Hash, string? Path)? identity, CancellationTokenSource stop)
    {
        using var transfer = new TransferStream(info.ConnectionKey, info.TransferKey, info.RequestKey, info.FileSize);
        try
        {
            var rel = identity?.Path ?? FileStatusReader.ToSyncPath(_root, LongPath(full));
            if (Fetch is not { } fetch || identity is not { } version || string.IsNullOrEmpty(rel))
            {
                transfer.Fail(CfApi.StatusCloudFileNetworkUnavailable);
                return;
            }
            _log.LogInformation("Downloading {Path} because it was opened", rel);
            await fetch(rel, version.Hash, transfer, done => CfApi.CfReportProviderProgress(info.ConnectionKey, info.TransferKey, info.FileSize, done), stop.Token)
                .ConfigureAwait(false);
            transfer.Finish();
            // Opened after "Free up space": it stays here now, as OneDrive does, until it is freed again.
            if (File.GetAttributes(full).HasFlag(CloudPlaceholders.Unpinned))
                WithHandle(full, h => Check(CfApi.CfSetPinState(h, CfApi.PinStateUnspecified, 0, IntPtr.Zero)));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not download {Path} when it was opened: {Error}", full, ex.Message);
            transfer.Fail(ex is OperationCanceledException ? CfApi.StatusCloudFileRequestCanceled : CfApi.StatusCloudFileNetworkUnavailable);
        }
        finally
        {
            _fetches.TryRemove(info.TransferKey, out _);
            stop.Dispose();
        }
    }

    private void OnCancelFetchData(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        try
        {
            var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
            if (_fetches.TryGetValue(info.TransferKey, out var stop))
                TryCancel(stop);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not stop a download: {Error}", ex.Message);
        }
    }

    private static void TryCancel(CancellationTokenSource stop)
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It just finished.
        }
    }

    /// <summary>Windows wants to free a file's space: allowed with Files On-Demand, refused when everything stays here.</summary>
    private void OnNotifyDehydrate(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        try
        {
            var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
            var allowed = Mode == CloudFolderMode.OnDemand && !IsOwnFile(FullPathOf(info));
            var op = new CfApi.OperationInfo
            {
                StructSize = Marshal.SizeOf<CfApi.OperationInfo>(),
                Type = CfApi.OperationAckDehydrate,
                ConnectionKey = info.ConnectionKey,
                TransferKey = info.TransferKey,
                RequestKey = info.RequestKey,
            };
            var parameters = new CfApi.AckDehydrateParameters
            {
                ParamSize = CfApi.AckDehydrateParameters.Size,
                CompletionStatus = allowed ? CfApi.StatusSuccess : CfApi.StatusCloudFileDehydrationDisallowed,
                FileIdentity = info.FileIdentity,
                FileIdentityLength = info.FileIdentityLength,
            };
            CfApi.CfExecuteAckDehydrate(ref op, ref parameters);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not answer Windows about freeing a file's space");
        }
    }

    /// <summary>The file a callback is about: its volume and its path on that volume.</summary>
    private static string FullPathOf(CfApi.CallbackInfo info)
    {
        var path = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
        if (path.Length >= 2 && path[1] == ':')
            return path;
        var volume = Marshal.PtrToStringUni(info.VolumeDosName) ?? string.Empty;
        return volume + path;
    }

    /// <summary>
    /// What a download writes into: passed on to Windows in pieces of 1 MB (TRANSFER_DATA), the last one when the
    /// download is finished and checked. Windows hands the bytes to whoever is opening the file as they arrive.
    /// </summary>
    private sealed class TransferStream(long connection, long transferKey, long requestKey, long size) : Stream
    {
        private readonly byte[] _bytes = GC.AllocateArray<byte>(ChunkSize, pinned: true);
        private int _filled;
        private long _sent;
        private bool _done;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _sent + _filled;
        public override long Position { get => Length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                if (_sent + _filled + data.Length > size)
                    throw new IOException("The nest sent more than the file's size: the file changed there.");
                var take = Math.Min(data.Length, ChunkSize - _filled);
                data[..take].CopyTo(_bytes.AsSpan(_filled));
                _filled += take;
                data = data[take..];
                if (_filled == ChunkSize && _sent + _filled < size)
                    Send(); // the last piece waits for Finish (after the download was checked)
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        /// <summary>The download is complete and checked: the rest goes to Windows.</summary>
        public void Finish()
        {
            if (_sent + _filled != size)
                throw new IOException($"The nest sent {_sent + _filled} bytes for a file of {size}: the file changed there.");
            if (_filled > 0 || size == 0)
                Send();
            _done = true;
        }

        private void Send()
        {
            var status = Execute(CfApi.StatusSuccess, Marshal.UnsafeAddrOfPinnedArrayElement(_bytes, 0), _sent, _filled);
            Marshal.ThrowExceptionForHR(status);
            _sent += _filled;
            _filled = 0;
        }

        /// <summary>Tells Windows the rest of the file cannot come (whoever opened it gets an error).</summary>
        public void Fail(int ntStatus)
        {
            if (_done)
                return;
            _done = true;
            Execute(ntStatus, IntPtr.Zero, _sent, Math.Max(size - _sent, 1));
        }

        private int Execute(int status, IntPtr buffer, long offset, long length)
        {
            var op = new CfApi.OperationInfo
            {
                StructSize = Marshal.SizeOf<CfApi.OperationInfo>(),
                Type = CfApi.OperationTransferData,
                ConnectionKey = connection,
                TransferKey = transferKey,
                RequestKey = requestKey,
            };
            var parameters = new CfApi.TransferDataParameters
            {
                ParamSize = CfApi.TransferDataParameters.Size,
                CompletionStatus = status,
                Buffer = buffer,
                Offset = offset,
                Length = length,
            };
            return CfApi.CfExecute(ref op, ref parameters);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_done)
                Fail(CfApi.StatusCloudFileUnsuccessful);
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static int PlaceholderState(string path) =>
        CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag)
            ? CfApi.CfGetPlaceholderStateFromAttributeTag((int)attributes, (int)tag)
            : 0;

    /// <summary>Opens a file or folder the way the cloud files API wants (never fetching its content) and runs <paramref name="action"/>.</summary>
    private static bool WithHandle(string path, Func<IntPtr, bool> action, int flags = CfApi.OpenFlagWriteAccess)
    {
        if (!CfApi.Ok(CfApi.CfOpenFileWithOplock(path, flags, out var protectedHandle)))
            return false;
        try
        {
            return action(CfApi.CfGetWin32HandleFromProtectedHandle(protectedHandle));
        }
        finally
        {
            CfApi.CfCloseHandle(protectedHandle);
        }
    }

    private static bool Check(int hresult) => CfApi.Ok(hresult);

    /// <summary>Every file and folder below the Pairnets folder, except Pairnets' download folder.</summary>
    private IEnumerable<string> AllEntries()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        return Directory.EnumerateFileSystemEntries(_root, "*", options)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + PathRules.TempFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> AllFiles() => EnumerateFiles(_root);

    private static IEnumerable<string> EnumerateFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true })
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + PathRules.TempFolderName, StringComparison.OrdinalIgnoreCase));
}
