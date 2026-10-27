using System.Runtime.InteropServices;

namespace Pairnets.Desktop.Platform;

/// <summary>
/// Notices from Pairnets itself (UNUserNotificationCenter, through the Objective-C runtime like MacTrash): they carry
/// Pairnets' icon, and a click runs the notice's action. The old way, "osascript display notification", made macOS
/// credit them to Script Editor, which then opened on a click.
/// </summary>
internal static class MacNotifications
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string Framework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    private const nuint AlertAndSound = (1 << 2) | (1 << 1); // UNAuthorizationOptionAlert | Sound
    private const nuint BannerListSound = (1 << 4) | (1 << 3) | (1 << 1); // UNNotificationPresentationOptionBanner | List | Sound
    private const int GlobalBlock = 1 << 28; // BLOCK_IS_GLOBAL

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getProtocol(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr2(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr3(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOptions(IntPtr receiver, IntPtr selector, nuint options, IntPtr block);

    private delegate void DelegateMethod(IntPtr self, IntPtr selector, IntPtr center, IntPtr argument, IntPtr completion);

    private delegate void AuthorizationBlock(IntPtr block, byte granted, IntPtr error);

    private delegate void CompletionWithOptions(IntPtr block, nuint options);

    private delegate void Completion(IntPtr block);

    // The runtime keeps only the raw pointers, so these stay referenced for as long as the app runs.
    private static readonly DelegateMethod DidReceive = DidReceiveResponse;
    private static readonly DelegateMethod WillPresent = WillPresentNotification;
    private static readonly AuthorizationBlock Authorized = AuthorizationAnswered;

    private static readonly NoticeActions Actions = new();
    private static readonly object Gate = new();
    private static readonly List<Notice> Waiting = [];
    private static bool _ready;
    private static bool _setUp;
    private static bool _granted;
    private static bool _asking;

    private sealed record Notice(string Id, string Title, string Text);

    /// <summary>
    /// Shows the notice as Pairnets. False when this is not a real Pairnets.app (a developer run has no bundle, and macOS
    /// refuses notices then): the caller shows it the old way. True when Pairnets took it, also when the user turned
    /// notices off for Pairnets in System Settings (then nothing shows, as they chose).
    /// </summary>
    public static bool TryShow(string title, string text, Action? onClick)
    {
        try
        {
            if (!SetUp())
                return false;
            var notice = new Notice(Guid.NewGuid().ToString("N"), title, text);
            Actions.Add(notice.Id, onClick);
            lock (Gate)
            {
                if (!_granted)
                {
                    Waiting.Add(notice);
                    if (!_asking)
                    {
                        _asking = true;
                        RequestAuthorization();
                    }
                    return true;
                }
            }
            Post(notice);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    private static bool SetUp()
    {
        lock (Gate)
        {
            if (_setUp)
                return _ready;
            _setUp = true;
            if (!OperatingSystem.IsMacOS() || !InsideAppBundle() || !NativeLibrary.TryLoad(Framework, out _))
                return false;
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "PairnetsNoticeDelegate", 0);
            if (cls == IntPtr.Zero)
                return false;
            class_addMethod(cls, sel_registerName("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                Marshal.GetFunctionPointerForDelegate(DidReceive), "v@:@@@?");
            class_addMethod(cls, sel_registerName("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                Marshal.GetFunctionPointerForDelegate(WillPresent), "v@:@@@?");
            var protocol = objc_getProtocol("UNUserNotificationCenterDelegate");
            if (protocol != IntPtr.Zero)
                class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
            var handler = Send(Send(cls, sel_registerName("alloc")), sel_registerName("init"));
            SendPtr(Center(), sel_registerName("setDelegate:"), handler); // (the centre holds it weakly; it lives as long as the app)
            _ready = true;
            return true;
        }
    }

    private static bool InsideAppBundle() =>
        Send(Send(objc_getClass("NSBundle"), sel_registerName("mainBundle")), sel_registerName("bundleIdentifier")) != IntPtr.Zero;

    private static IntPtr Center() => Send(objc_getClass("UNUserNotificationCenter"), sel_registerName("currentNotificationCenter"));

    /// <summary>Asks macOS (the first time it shows "Allow Pairnets to send notifications?"; later it answers by itself).</summary>
    private static void RequestAuthorization()
    {
        // A block that lives for good: isa, flags, reserved, invoke, descriptor (reserved, size).
        var block = Marshal.AllocHGlobal(64);
        var descriptor = block + 32;
        Marshal.WriteIntPtr(block, 0, NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock"));
        Marshal.WriteInt32(block, 8, GlobalBlock);
        Marshal.WriteInt32(block, 12, 0);
        Marshal.WriteIntPtr(block, 16, Marshal.GetFunctionPointerForDelegate(Authorized));
        Marshal.WriteIntPtr(block, 24, descriptor);
        Marshal.WriteIntPtr(descriptor, 0, IntPtr.Zero);
        Marshal.WriteIntPtr(descriptor, 8, 32);
        SendOptions(Center(), sel_registerName("requestAuthorizationWithOptions:completionHandler:"), AlertAndSound, block);
    }

    private static void AuthorizationAnswered(IntPtr block, byte granted, IntPtr error)
    {
        Notice[] now;
        lock (Gate)
        {
            _asking = false;
            _granted = granted != 0;
            now = [.. Waiting];
            Waiting.Clear();
        }
        // Not allowed: the notices are dropped, and the next one asks again (macOS then answers at once with the current choice).
        if (granted == 0)
            return;
        foreach (var notice in now)
            Post(notice);
    }

    private static void Post(Notice notice)
    {
        var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        try
        {
            var content = Send(Send(objc_getClass("UNMutableNotificationContent"), sel_registerName("alloc")), sel_registerName("init"));
            SendPtr(content, sel_registerName("setTitle:"), NsString(notice.Title));
            SendPtr(content, sel_registerName("setBody:"), NsString(notice.Text));
            SendPtr(content, sel_registerName("setSound:"), Send(objc_getClass("UNNotificationSound"), sel_registerName("defaultSound")));
            var request = SendPtr3(objc_getClass("UNNotificationRequest"), sel_registerName("requestWithIdentifier:content:trigger:"),
                NsString(notice.Id), content, IntPtr.Zero);
            SendPtr2(Center(), sel_registerName("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
            Send(content, sel_registerName("release"));
        }
        finally
        {
            Send(pool, sel_registerName("drain"));
        }
    }

    private static IntPtr NsString(string value) => SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);

    /// <summary>A notice was clicked: run its action (the window opens on the right page), then tell macOS we are done.</summary>
    private static void DidReceiveResponse(IntPtr self, IntPtr selector, IntPtr center, IntPtr response, IntPtr completion)
    {
        try
        {
            var notification = Send(response, sel_registerName("notification"));
            var request = Send(notification, sel_registerName("request"));
            var id = Marshal.PtrToStringUTF8(Send(Send(request, sel_registerName("identifier")), sel_registerName("UTF8String")));
            if (id is not null)
                Actions.Get(id)?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A click that cannot be handled just brings nothing up; the app keeps running.
        }
        if (completion != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<Completion>(Marshal.ReadIntPtr(completion, 16))(completion);
    }

    /// <summary>A notice arrives while Pairnets is the front app: show it anyway (macOS hides it otherwise).</summary>
    private static void WillPresentNotification(IntPtr self, IntPtr selector, IntPtr center, IntPtr notification, IntPtr completion)
    {
        var invoke = Marshal.GetDelegateForFunctionPointer<CompletionWithOptions>(Marshal.ReadIntPtr(completion, 16));
        invoke(completion, BannerListSound);
    }
}
