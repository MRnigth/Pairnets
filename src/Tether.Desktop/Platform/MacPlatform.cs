using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Desktop.Platform;

/// <summary>macOS: login Keychain, Finder Trash (NSFileManager), Notification Center, LaunchAgent.</summary>
public sealed class MacPlatform : IPlatformServices
{
    public string Name => "macOS";

    public ISecretProtector Secrets { get; } = new KeychainSecrets();

    public ILocalTrash CreateTrash(ILogger log) => new MacTrash(log);

    public void Notify(string title, string text) =>
        // Values are passed as argv to the script, never spliced into AppleScript source.
        Proc.Run("osascript", ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, text], timeoutMs: 3000);

    private static string AgentFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "app.tether.client.plist");

    public bool IsAutoStartEnabled() => File.Exists(AgentFile);

    public void SetAutoStart(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(AgentFile))
                File.Delete(AgentFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile)!);
        var exe = System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "/Applications/Tether.app/Contents/MacOS/Tether");
        File.WriteAllText(AgentFile, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>app.tether.client</string>
              <key>ProgramArguments</key><array><string>{exe}</string><string>--autostart</string></array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);
    }

    public void Open(string path) => Proc.Start("open", path);

    public void Reveal(string path)
    {
        if (File.Exists(path))
            Proc.Start("open", "-R", path);
        else
            Open(path);
    }

    // ------------------------------------------------------------------ Keychain

    /// <summary>Generic password in the login keychain, through the Security framework (no secrets on command lines).</summary>
    private sealed class KeychainSecrets : ISecretProtector
    {
        private const string Marker = "keychain:v1";
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecDuplicateItem = -25299;
        private static readonly byte[] Service = Encoding.UTF8.GetBytes("Tether");
        private static readonly byte[] Account = Encoding.UTF8.GetBytes("sync-token");

        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        public string Protect(string plainText)
        {
            var data = Encoding.UTF8.GetBytes(plainText);
            var rc = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, (uint)data.Length, data, IntPtr.Zero);
            if (rc == ErrSecDuplicateItem)
            {
                rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, out _, out var old, out var item);
                if (rc == 0)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, old);
                    rc = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data);
                    CFRelease(item);
                }
            }
            if (rc != 0)
                throw new InvalidOperationException($"Could not store the token in the Keychain (error {rc}).");
            return Marker;
        }

        public string Unprotect(string protectedText)
        {
            if (protectedText != Marker)
                throw new InvalidOperationException("Unknown token storage.");
            var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, out var length, out var data, out var item);
            if (rc != 0)
                throw new InvalidOperationException($"The token is not in the Keychain (error {rc}).");
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }
        }
    }

    // ------------------------------------------------------------------ Trash

    /// <summary>[[NSFileManager defaultManager] trashItemAtURL:...] via the Objective-C runtime.</summary>
    private sealed class MacTrash(ILogger log) : ILocalTrash
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr resultingUrl, IntPtr error);

        public void Delete(string fullPath)
        {
            try
            {
                var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
                try
                {
                    var nsPath = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), fullPath);
                    var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), nsPath);
                    var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
                    if (SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, IntPtr.Zero) && !File.Exists(fullPath))
                        return;
                }
                finally
                {
                    Send(pool, sel_registerName("drain"));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                log.LogDebug("Trash API unavailable: {Error}", ex.Message);
            }
            log.LogWarning("Moving {Path} to the Trash failed; deleting permanently (the server keeps history)", fullPath);
            File.Delete(fullPath);
        }
    }
}
