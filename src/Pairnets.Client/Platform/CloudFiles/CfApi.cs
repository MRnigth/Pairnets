using System.Runtime.InteropServices;

namespace Pairnets.Client.Platform.CloudFiles;

/// <summary>
/// Windows' Cloud Files API (cldapi.dll, Windows 10 1709 and later): the part of Windows OneDrive is built on. Only
/// what Pairnets uses, laid out as in cfapi.h for 64-bit Windows.
/// </summary>
internal static class CfApi
{
    private const string Dll = "cldapi.dll";

    // ---- callbacks Windows makes to the app that owns the folder

    public const int CallbackFetchData = 0;
    public const int CallbackCancelFetchData = 2;
    public const int CallbackNotifyDehydrate = 7;
    public const int CallbackNone = unchecked((int)0xFFFFFFFF);

    public const int OperationTransferData = 0;
    public const int OperationAckDehydrate = 5;

    public const int ConnectFlagRequireProcessInfo = 0x2;
    public const int ConnectFlagRequireFullFilePath = 0x4;
    public const int ConnectFlagBlockSelfImplicitHydration = 0x8;

    // ---- placeholders

    public const int ConvertFlagMarkInSync = 0x1;
    public const int ConvertFlagDehydrate = 0x2;

    public const int CreateFlagMarkInSync = 0x2;
    public const int CreateFlagSupersede = 0x4;

    public const int UpdateFlagMarkInSync = 0x2;
    public const int UpdateFlagDehydrate = 0x4;

    public const int InSyncStateNotInSync = 0;
    public const int InSyncStateInSync = 1;

    public const int PinStateUnspecified = 0;
    public const int PinStatePinned = 1;
    public const int PinStateUnpinned = 2;
    public const int SetPinFlagRecurse = 0x1;

    public const int OpenFlagNone = 0x0;
    public const int OpenFlagWriteAccess = 0x2;

    // ---- NTSTATUS answers in the cloud range (anything else counts as "unsuccessful")

    public const int StatusSuccess = 0;
    public const int StatusCloudFileNetworkUnavailable = unchecked((int)0xC000CF11);
    public const int StatusCloudFileUnsuccessful = unchecked((int)0xC000CF12);
    public const int StatusCloudFileNotSupported = unchecked((int)0xC000CF09);
    public const int StatusCloudFileRequestCanceled = unchecked((int)0xC000CF1B);

    /// <summary>The length that means "to the end of the file".</summary>
    public const long Eof = -1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Callback(IntPtr callbackInfo, IntPtr callbackParameters);

    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackRegistration
    {
        public int Type;
        public IntPtr Callback;
    }

    /// <summary>CF_CALLBACK_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackInfo
    {
        public int StructSize;
        public long ConnectionKey;
        public IntPtr CallbackContext;
        public IntPtr VolumeGuidName;
        public IntPtr VolumeDosName;
        public int VolumeSerialNumber;
        public long SyncRootFileId;
        public IntPtr SyncRootIdentity;
        public int SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public IntPtr FileIdentity;
        public int FileIdentityLength;
        public IntPtr NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public IntPtr CorrelationVector;
        public IntPtr ProcessInfo;
        public long RequestKey;
    }

    /// <summary>CF_CALLBACK_PARAMETERS for FETCH_DATA (the union starts 8 bytes in).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FetchDataParameters
    {
        public int ParamSize;
        public int Padding;
        public int Flags;
        public long RequiredFileOffset;
        public long RequiredLength;
        public long OptionalFileOffset;
        public long OptionalLength;
        public long LastDehydrationTime;
        public int LastDehydrationReason;
    }

    /// <summary>CF_OPERATION_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OperationInfo
    {
        public int StructSize;
        public int Type;
        public long ConnectionKey;
        public long TransferKey;
        public IntPtr CorrelationVector;
        public IntPtr SyncStatus;
        public long RequestKey;
    }

    /// <summary>CF_OPERATION_PARAMETERS with its TransferData member.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TransferDataParameters
    {
        public int ParamSize;
        public int Padding;
        public int Flags;
        public int CompletionStatus;
        public IntPtr Buffer;
        public long Offset;
        public long Length;

        public static int Size => Marshal.SizeOf<TransferDataParameters>();
    }

    /// <summary>CF_OPERATION_PARAMETERS with its AckDehydrate member.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AckDehydrateParameters
    {
        public int ParamSize;
        public int Padding;
        public int Flags;
        public int CompletionStatus;
        public IntPtr FileIdentity;
        public int FileIdentityLength;
        public int Padding2;

        public static int Size => Marshal.SizeOf<AckDehydrateParameters>();
    }

    /// <summary>CF_FS_METADATA: FILE_BASIC_INFO and the file's size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FsMetadata
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public int FileAttributes;
        public int Padding;
        public long FileSize;
    }

    /// <summary>CF_PLACEHOLDER_CREATE_INFO.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PlaceholderCreateInfo
    {
        public IntPtr RelativeFileName;
        public FsMetadata FsMetadata;
        public IntPtr FileIdentity;
        public int FileIdentityLength;
        public int Flags;
        public int Result;
        public int Padding;
        public long CreateUsn;
    }

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfConnectSyncRoot([MarshalAs(UnmanagedType.LPWStr)] string syncRootPath, [In] CallbackRegistration[] callbackTable,
        IntPtr callbackContext, int connectFlags, out long connectionKey);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfExecute(ref OperationInfo opInfo, ref TransferDataParameters opParams);

    [DllImport(Dll, ExactSpelling = true, EntryPoint = "CfExecute")]
    public static extern int CfExecuteAckDehydrate(ref OperationInfo opInfo, ref AckDehydrateParameters opParams);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfReportProviderProgress(long connectionKey, long transferKey, long providerProgressTotal, long providerProgressCompleted);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfOpenFileWithOplock([MarshalAs(UnmanagedType.LPWStr)] string filePath, int flags, out IntPtr protectedHandle);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern void CfCloseHandle(IntPtr fileHandle);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfConvertToPlaceholder(IntPtr fileHandle, byte[]? fileIdentity, int fileIdentityLength, int convertFlags,
        IntPtr convertUsn, IntPtr overlapped);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfRevertPlaceholder(IntPtr fileHandle, int revertFlags, IntPtr overlapped);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfSetInSyncState(IntPtr fileHandle, int inSyncState, int inSyncFlags, IntPtr inSyncUsn);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfSetPinState(IntPtr fileHandle, int pinState, int pinFlags, IntPtr overlapped);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfCreatePlaceholders([MarshalAs(UnmanagedType.LPWStr)] string baseDirectoryPath, [In, Out] PlaceholderCreateInfo[] placeholderArray,
        int placeholderCount, int createFlags, out int entriesProcessed);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfUpdatePlaceholder(IntPtr fileHandle, ref FsMetadata fsMetadata, byte[]? fileIdentity, int fileIdentityLength,
        IntPtr dehydrateRangeArray, int dehydrateRangeCount, int updateFlags, IntPtr updateUsn, IntPtr overlapped);

    /// <summary>CfUpdatePlaceholder without new details (null metadata): only the version and the flags change.</summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfUpdatePlaceholder(IntPtr fileHandle, IntPtr fsMetadata, byte[]? fileIdentity, int fileIdentityLength,
        IntPtr dehydrateRangeArray, int dehydrateRangeCount, int updateFlags, IntPtr updateUsn, IntPtr overlapped);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfHydratePlaceholder(IntPtr fileHandle, long startingOffset, long length, int hydrateFlags, IntPtr overlapped);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfDehydratePlaceholder(IntPtr fileHandle, long startingOffset, long length, int dehydrateFlags, IntPtr overlapped);

    /// <summary>The placeholder state flags (CF_PLACEHOLDER_STATE) from a file's attributes and reparse tag.</summary>
    [DllImport(Dll, ExactSpelling = true)]
    public static extern int CfGetPlaceholderStateFromAttributeTag(int fileAttributes, int reparseTag);

    public const int PlaceholderStatePlaceholder = 0x1;
    public const int PlaceholderStateInSync = 0x8;

    /// <summary>True for an HRESULT that means it worked.</summary>
    public static bool Ok(int hresult) => hresult >= 0;
}
