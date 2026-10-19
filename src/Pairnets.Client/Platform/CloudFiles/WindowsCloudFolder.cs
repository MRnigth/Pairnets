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
/// </summary>
public sealed class WindowsCloudFolder : ICloudFolder
{
    /// <summary>The first part of every Pairnets cloud folder's id with Windows: "Pairnets!&lt;user&gt;!&lt;folder&gt;".</summary>
    public const string ProviderName = "Pairnets";

    private const int ChunkSize = 1024 * 1024; // a multiple of 4 KB, as Windows asks for every piece but the last

    private readonly string _root;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _fetches = new();
    private readonly ConcurrentDictionary<string, byte> _pinWork = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reconcileGate = new();
    private readonly CfApi.Callback _onFetch;
    private readonly CfApi.Callback _onCancel;
    private readonly CfApi.Callback _onDehydrate;
    private long _connection;
    private volatile bool _connected;
    private FileSystemWatcher? _pins;
    private bool _reconciling;
    private bool _reconcileAgain;

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
    /// opened it, or "Always keep on this device"): (sync path, version hash, where to, bytes so far, stop).
    /// Set by the sync session once it runs; until then opening such a file waits and then fails.
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
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LongPath(root).ToUpperInvariant())))[..16];
        return $"{ProviderName}!{sid}!{key}";
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
    /// they are, in the background (<see cref="Reconcile"/>).
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
            folder.ApplyMode();
            folder.WatchPins();
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
        var folder = StorageFolder.GetFolderFromPathAsync(_root).AsTask().GetAwaiter().GetResult();
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
    /// Keep everything here: the folder is "Always keep on this device", so Windows never frees space in it (and
    /// fetches anything still on the nest). Files On-Demand: the folder's own choice is cleared, so files can be freed.
    /// </summary>
    private void ApplyMode()
    {
        var pinned = File.GetAttributes(_root).HasFlag(CloudPlaceholders.Pinned);
        if (Mode == CloudFolderMode.KeepAllHere && !pinned)
            WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStatePinned, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
        else if (Mode == CloudFolderMode.OnDemand && pinned)
            WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStateUnspecified, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
    }

    /// <summary>
    /// Turns the folder back into a plain folder: every file still on the nest is downloaded first (it fails, and the
    /// folder stays a cloud folder, when that is not possible), then every placeholder becomes a plain file again and
    /// Windows forgets the folder. Returns null when done, or what stopped it.
    /// </summary>
    public async Task<string?> LeaveAsync(CancellationToken ct)
    {
        if (!_connected)
            return null;
        WithHandle(_root, h => Check(CfApi.CfSetPinState(h, CfApi.PinStatePinned, CfApi.SetPinFlagRecurse, IntPtr.Zero)));
        var online = AllFiles().Where(IsCloudOnly).ToList();
        foreach (var file in online)
        {
            ct.ThrowIfCancellationRequested();
            if (!await Task.Run(() => Hydrate(file), ct).ConfigureAwait(false))
                return $"{online.Count(IsCloudOnly)} file(s) are still only on your nest (for example {Path.GetFileName(file)}). Pairnets needs to reach your nest to download them first.";
        }
        Disconnect();
        var stuck = RevertAll();
        UnregisterAll();
        return stuck == 0 ? null : $"{stuck} file(s) could not be turned back into plain files (they were in use). They still open normally.";
    }

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

    /// <summary>Every placeholder whose content is here becomes a plain file again; returns how many could not.</summary>
    private int RevertAll()
    {
        var stuck = 0;
        foreach (var path in AllFiles().Reverse().Append(_root))
        {
            if (!CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag) || !CloudPlaceholders.IsCloudTag(tag))
                continue;
            if (CloudPlaceholders.IsContentOnline(attributes) || !WithHandle(path, h => Check(CfApi.CfRevertPlaceholder(h, 0, IntPtr.Zero))))
                stuck++;
        }
        return stuck;
    }

    private void Disconnect()
    {
        _pins?.Dispose();
        _pins = null;
        if (!_connected)
            return;
        _connected = false;
        foreach (var fetch in _fetches.Values)
            fetch.Cancel();
        CfApi.CfDisconnectSyncRoot(_connection);
    }

    public void Dispose() => Disconnect();

    // ------------------------------------------------------------------ what the sync engine asks

    public bool IsCloudOnly(string fullPath) =>
        CloudPlaceholders.TryGetAttributesAndTag(fullPath, out var attributes, out _) && (attributes & FileAttributes.Directory) == 0
        && CloudPlaceholders.IsContentOnline(attributes);

    public void MarkSynced(string fullPath, string hash)
    {
        if (!_connected)
            return;
        var identity = Encoding.UTF8.GetBytes(hash);
        var ok = WithHandle(fullPath, h =>
        {
            var state = PlaceholderState(fullPath);
            if ((state & CfApi.PlaceholderStatePlaceholder) == 0)
                return Check(CfApi.CfConvertToPlaceholder(h, identity, identity.Length, CfApi.ConvertFlagMarkInSync, IntPtr.Zero, IntPtr.Zero));
            return Check(CfApi.CfUpdatePlaceholder(h, IntPtr.Zero, identity, identity.Length, IntPtr.Zero, 0, CfApi.UpdateFlagMarkInSync, IntPtr.Zero, IntPtr.Zero));
        });
        if (!ok)
            _log.LogDebug("Could not mark {Path} as synced for Explorer yet (in use?); the next check does", fullPath);
        MarkFoldersAbove(fullPath);
    }

    public bool KeepOnNest(string fullPath)
    {
        if (!_connected || Mode != CloudFolderMode.OnDemand)
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
        var identity = Encoding.UTF8.GetBytes(hash);
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
            // A placeholder of an older version: the new version's details, content left on the nest.
            ok = IsCloudOnly(fullPath) && WithHandle(fullPath, h => Check(CfApi.CfUpdatePlaceholder(h, ref metadata, identity, identity.Length,
                IntPtr.Zero, 0, CfApi.UpdateFlagMarkInSync | CfApi.UpdateFlagDehydrate, IntPtr.Zero, IntPtr.Zero)));
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
    /// Brings every file's mark in line with Pairnets' notes: <paramref name="syncedHash"/> gives a synced file's version,
    /// or null when the file is not synced (new, changed, or a problem), which then shows sync arrows. Pairnets' own
    /// bookkeeping files count as synced. Runs in the background; a call while one runs makes it run once more after.
    /// </summary>
    public void Reconcile(Func<string, string?> syncedHash)
    {
        if (!_connected)
            return;
        lock (_reconcileGate)
        {
            if (_reconciling)
            {
                _reconcileAgain = true;
                return;
            }
            _reconciling = true;
        }
        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    ReconcileOnce(syncedHash);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not update the Explorer marks of {Folder}", _root);
                }
                lock (_reconcileGate)
                {
                    if (!_reconcileAgain || !_connected)
                    {
                        _reconciling = false;
                        return;
                    }
                    _reconcileAgain = false;
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
            var hash = IsOwnFile(path) ? "pairnets" : syncedHash(path);
            if (hash is not null && !inSync)
                MarkSynced(path, hash);
            else if (hash is null && inSync && !CloudPlaceholders.IsContentOnline(attributes))
                WithHandle(path, h => Check(CfApi.CfSetInSyncState(h, CfApi.InSyncStateNotInSync, 0, IntPtr.Zero)));
        }
    }

    /// <summary>Pairnets' own files in the folder (its marker and the folder icon): never synced, never shown as waiting.</summary>
    private bool IsOwnFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(name, PathRules.MarkerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ "Always keep on this device" and "Free up space"

    /// <summary>Explorer's two choices change a file's or folder's pinned flags; Pairnets then fetches or frees the files.</summary>
    private void WatchPins()
    {
        _pins = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _pins.Changed += (_, e) => OnPinChanged(e.FullPath);
        _pins.EnableRaisingEvents = true;
    }

    private void OnPinChanged(string path)
    {
        if (!_pinWork.TryAdd(path, 0))
            return; // already being looked at
        _ = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(300); // Explorer changes a whole folder at once: let it finish
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
                    else if (!online && Mode == CloudFolderMode.OnDemand && attributes.HasFlag(CloudPlaceholders.Unpinned)
                        && (PlaceholderState(file) & CfApi.PlaceholderStateInSync) != 0)
                        WithHandle(file, h => Check(CfApi.CfDehydratePlaceholder(h, 0, CfApi.Eof, 0, IntPtr.Zero)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("Could not follow the keep/free choice on {Path}: {Error}", path, ex.Message);
            }
            finally
            {
                _pinWork.TryRemove(path, out _);
            }
        });
    }

    /// <summary>Downloads a file whose content is on the nest (Windows calls <see cref="OnFetchData"/> for it). False when it could not.</summary>
    private bool Hydrate(string file) =>
        WithHandle(file, h => Check(CfApi.CfHydratePlaceholder(h, 0, CfApi.Eof, 0, IntPtr.Zero)), CfApi.OpenFlagNone);

    // ------------------------------------------------------------------ Windows calling back

    private void OnFetchData(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
        var stop = new CancellationTokenSource();
        _fetches[info.TransferKey] = stop;
        var full = FullPathOf(info);
        var hash = info.FileIdentityLength > 0 ? Marshal.PtrToStringUTF8(info.FileIdentity, info.FileIdentityLength) : null;
        _ = Task.Run(() => HydrateAsync(info, full, hash, stop));
    }

    private async Task HydrateAsync(CfApi.CallbackInfo info, string full, string? hash, CancellationTokenSource stop)
    {
        using var transfer = new TransferStream(info.ConnectionKey, info.TransferKey, info.RequestKey, info.FileSize);
        try
        {
            if (Fetch is not { } fetch || hash is null || FileStatusReader.ToSyncPath(_root, full) is not { Length: > 0 } rel)
            {
                transfer.Fail(CfApi.StatusCloudFileNetworkUnavailable);
                return;
            }
            _log.LogInformation("Downloading {Path} because it was opened", rel);
            await fetch(rel, hash, transfer, done => CfApi.CfReportProviderProgress(info.ConnectionKey, info.TransferKey, info.FileSize, done), stop.Token)
                .ConfigureAwait(false);
            transfer.Finish();
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
        var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
        if (_fetches.TryGetValue(info.TransferKey, out var stop))
            stop.Cancel();
    }

    /// <summary>Windows wants to free a file's space: allowed with Files On-Demand, refused when everything stays here.</summary>
    private void OnNotifyDehydrate(IntPtr callbackInfo, IntPtr callbackParameters)
    {
        var info = Marshal.PtrToStructure<CfApi.CallbackInfo>(callbackInfo);
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
            CompletionStatus = Mode == CloudFolderMode.OnDemand ? CfApi.StatusSuccess : CfApi.StatusCloudFileNotSupported,
            FileIdentity = info.FileIdentity,
            FileIdentityLength = info.FileIdentityLength,
        };
        CfApi.CfExecuteAckDehydrate(ref op, ref parameters);
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
