using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GhostScreen;

internal sealed unsafe class ParsecVddController : IDisposable
{
    public const string DISPLAY_ID = "PSCCDD0";
    public const string ADAPTER_GUID = "{00b41627-04c4-429e-a26e-0265cf50c8fa}";
    public const string HARDWARE_ID = @"Root\Parsec\VDA";
    public const string CLASS_GUID = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly object _lock = new();
    private IntPtr _vddHandle = IntPtr.Zero;
    private readonly List<int> _addedDisplayIndices = new();
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    public bool IsActive { get; private set; }

    public bool IsDriverInstalled()
    {
        Guid guid = Guid.Parse(CLASS_GUID);
        IntPtr devInfo = SetupDiGetClassDevsA(ref guid, null, null, DIGCF_PRESENT);
        if (devInfo == IntPtr.Zero || devInfo == INVALID_HANDLE_VALUE)
            return false;

        try
        {
            const int bufSize = 512;
            byte* hwidBuffer = stackalloc byte[bufSize];

            var devInfoData = new SP_DEVINFO_DATA
            {
                cbSize = sizeof(SP_DEVINFO_DATA)
            };

            for (uint index = 0; SetupDiEnumDeviceInfo(devInfo, index, &devInfoData); index++)
            {
                uint regDataType = 0;
                if (SetupDiGetDeviceRegistryPropertyA(
                    devInfo,
                    &devInfoData,
                    SPDRP_HARDWAREID,
                    &regDataType,
                    hwidBuffer,
                    bufSize,
                    null))
                {
                    if (regDataType == REG_SZ || regDataType == REG_MULTI_SZ)
                    {
                        int length = lstrlenA(hwidBuffer);
                        if (length == HARDWARE_ID.Length)
                        {
                            string? str = Marshal.PtrToStringAnsi((IntPtr)hwidBuffer, length);
                            if (string.Equals(str, HARDWARE_ID, StringComparison.OrdinalIgnoreCase))
                            {
                                uint devStatus = 0;
                                uint devProblem = 0;
                                if (CM_Get_DevNode_Status(&devStatus, &devProblem, devInfoData.DevInst, 0) == CR_SUCCESS)
                                {
                                    return (devStatus & (DN_DRIVER_LOADED | DN_STARTED)) != 0;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    public static string? FindDriverInstallerPath()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "drivers", "parsec-vdd-0.45.0.0.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "drivers", "parsec-vdd-0.45.0.0.exe")),
            @"C:\Users\Arjun\.gemini\antigravity\scratch\GhostScreen\drivers\parsec-vdd-0.45.0.0.exe"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public bool InstallDriverWithUac(out string errorMessage)
    {
        errorMessage = string.Empty;
        string? installerPath = FindDriverInstallerPath();
        if (string.IsNullOrEmpty(installerPath))
        {
            errorMessage = "Could not find drivers\\parsec-vdd-0.45.0.0.exe.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/S",
                UseShellExecute = true,
                Verb = "runas"
            };

            using Process? proc = Process.Start(psi);
            proc?.WaitForExit(30000);

            // Give Windows PnP a moment to finish starting the device node
            for (int i = 0; i < 15; i++)
            {
                if (IsDriverInstalled())
                    return true;
                Thread.Sleep(250);
            }

            return IsDriverInstalled();
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool ActivateZeroLightMode(out string statusMessage)
    {
        statusMessage = string.Empty;

        if (!IsDriverInstalled())
        {
            statusMessage = "Virtual Display Driver is not installed yet.";
            return false;
        }

        lock (_lock)
        {
            if (_vddHandle == IntPtr.Zero || _vddHandle == INVALID_HANDLE_VALUE)
            {
                if (!OpenDeviceHandle(out _vddHandle))
                {
                    statusMessage = "Failed to open Parsec Virtual Display Adapter handle.";
                    return false;
                }
            }

            if (_addedDisplayIndices.Count == 0)
            {
                if (!IoControl(_vddHandle, IoCtlCode.IOCTL_ADD, null, out int displayIndex, 5000))
                {
                    statusMessage = "Failed to plug in Virtual Display via IOCTL_ADD.";
                    return false;
                }
                _addedDisplayIndices.Add(displayIndex);
                IoControl(_vddHandle, IoCtlCode.IOCTL_UPDATE, null, 1000);
            }

            StartKeepAliveLoop();
            IsActive = true;
        }

        // Switch display topology on a background task so the UI/hotkey thread never freezes
        Task.Run(() => SwitchTopologyToVirtualOnly());

        statusMessage = "Virtual Display active • Switching physical laptop panel OFF (0% light)...";
        return true;
    }

    private void SwitchTopologyToVirtualOnly()
    {
        try
        {
            // Give Windows PnP ~500ms to register the hot-plugged virtual display
            Thread.Sleep(550);
            if (!IsActive) return;

            // 1. Tell Windows CCD to project to "Second screen only" (External / Virtual only)
            int sdcResult = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTERNAL);
            RunDisplaySwitch("/external");

            Thread.Sleep(650);
            if (!IsActive) return;

            // 2. If the physical internal monitor is still active (e.g., first-time plug before Windows has an External profile),
            // briefly stage Extend so the virtual monitor is attached to the desktop, then force Virtual-Only.
            if (IsPhysicalMonitorStillActive() || sdcResult != 0)
            {
                SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                Thread.Sleep(500);
                if (!IsActive) return;

                SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTERNAL);
                RunDisplaySwitch("/external");
                Thread.Sleep(500);

                if (IsPhysicalMonitorStillActive())
                {
                    ForceVirtualOnlyViaDevMode();
                }
            }
        }
        catch
        {
            // Ignore transient topology errors
        }
    }

    public void DeactivateZeroLightMode()
    {
        lock (_lock)
        {
            IsActive = false;

            try
            {
                _keepAliveCts?.Cancel();
            }
            catch { }

            if (_vddHandle != IntPtr.Zero && _vddHandle != INVALID_HANDLE_VALUE)
            {
                // Unplug in reverse order
                for (int i = _addedDisplayIndices.Count - 1; i >= 0; i--)
                {
                    int idx = _addedDisplayIndices[i];
                    byte[] input = new byte[2];
                    input[1] = (byte)(idx & 0xFF);
                    IoControl(_vddHandle, IoCtlCode.IOCTL_REMOVE, input, 1000);
                    IoControl(_vddHandle, IoCtlCode.IOCTL_UPDATE, null, 1000);
                }
                _addedDisplayIndices.Clear();

                CloseHandle(_vddHandle);
                _vddHandle = IntPtr.Zero;
            }
        }

        // Restore internal laptop screen topology
        try
        {
            SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_INTERNAL);
            RunDisplaySwitch("/internal");
        }
        catch { }
    }

    private void StartKeepAliveLoop()
    {
        if (_keepAliveTask != null && !_keepAliveTask.IsCompleted)
            return;

        _keepAliveCts = new CancellationTokenSource();
        CancellationToken token = _keepAliveCts.Token;

        _keepAliveTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                lock (_lock)
                {
                    if (_vddHandle != IntPtr.Zero && _vddHandle != INVALID_HANDLE_VALUE && IsActive)
                    {
                        IoControl(_vddHandle, IoCtlCode.IOCTL_UPDATE, null, 500);
                    }
                }
                if (token.WaitHandle.WaitOne(80))
                {
                    break;
                }
            }
        }, token);
    }

    private static string? FindVirtualDisplayAdapterName()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var ddMon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (int j = 0; EnumDisplayDevices(dd.DeviceName, j, ref ddMon, EDD_GET_DEVICE_INTERFACE_NAME); j++)
            {
                if (!string.IsNullOrEmpty(ddMon.DeviceID) &&
                    ddMon.DeviceID.Contains(DISPLAY_ID, StringComparison.OrdinalIgnoreCase))
                {
                    return dd.DeviceName;
                }
            }
        }
        return null;
    }

    private static bool IsPhysicalMonitorStillActive()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0)
                continue;

            var ddMon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (int j = 0; EnumDisplayDevices(dd.DeviceName, j, ref ddMon, EDD_GET_DEVICE_INTERFACE_NAME); j++)
            {
                if ((ddMon.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 &&
                    !string.IsNullOrEmpty(ddMon.DeviceID) &&
                    !ddMon.DeviceID.Contains(DISPLAY_ID, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void ForceVirtualOnlyViaDevMode()
    {
        string? virtualAdapter = FindVirtualDisplayAdapterName();
        if (string.IsNullOrEmpty(virtualAdapter))
            return;

        var vMode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(virtualAdapter, -1, ref vMode))
        {
            // If the virtual display is attached but not yet active in desktop, set 1920x1080 @ 60Hz
            vMode.dmPelsWidth = 1920;
            vMode.dmPelsHeight = 1080;
            vMode.dmDisplayFrequency = 60;
            vMode.dmBitsPerPel = 32;
        }

        int offX = vMode.dmPositionX;
        int offY = vMode.dmPositionY;

        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            bool isVirtual = string.Equals(dd.DeviceName, virtualAdapter, StringComparison.OrdinalIgnoreCase);

            if (isVirtual)
            {
                vMode.dmPositionX = 0;
                vMode.dmPositionY = 0;
                vMode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                ChangeDisplaySettingsEx(
                    dd.DeviceName,
                    ref vMode,
                    IntPtr.Zero,
                    CDS_UPDATEREGISTRY | CDS_NORESET | CDS_SET_PRIMARY,
                    IntPtr.Zero);
            }
            else if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
            {
                // Detach the physical laptop display so its hardware backlight powers completely off
                var detachMode = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf<DEVMODE>(),
                    dmPelsWidth = 0,
                    dmPelsHeight = 0,
                    dmPositionX = -offX,
                    dmPositionY = -offY,
                    dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
                };

                ChangeDisplaySettingsEx(
                    dd.DeviceName,
                    ref detachMode,
                    IntPtr.Zero,
                    CDS_UPDATEREGISTRY | CDS_NORESET,
                    IntPtr.Zero);
            }
        }

        // Commit atomic changes
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }

    private static void RunDisplaySwitch(string arg)
    {
        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string exePath = Path.Combine(sysDir, "DisplaySwitch.exe");
            if (!File.Exists(exePath)) return;

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arg,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(2000);
        }
        catch { }
    }

    private static bool OpenDeviceHandle(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        Guid interfaceGuid = Guid.Parse(ADAPTER_GUID);
        IntPtr devInfo = SetupDiGetClassDevsA(ref interfaceGuid, null, null, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (devInfo == IntPtr.Zero || devInfo == INVALID_HANDLE_VALUE)
            return false;

        try
        {
            var devInterface = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = sizeof(SP_DEVICE_INTERFACE_DATA)
            };

            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, null, ref interfaceGuid, i, &devInterface); ++i)
            {
                int detailSize = 0;
                SetupDiGetDeviceInterfaceDetailA(devInfo, &devInterface, null, 0, &detailSize, null);
                if (detailSize <= 0) continue;

                var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA_A*)Marshal.AllocHGlobal(detailSize);
                try
                {
                    detail->cbSize = IntPtr.Size == 8 ? 8 : 5;
                    if (SetupDiGetDeviceInterfaceDetailA(devInfo, &devInterface, detail, detailSize, &detailSize, null))
                    {
                        handle = CreateFileA(
                            &detail->DevicePath,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            null,
                            OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH,
                            null);

                        if (handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE)
                            return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal((IntPtr)detail);
                }
            }
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private enum IoCtlCode : uint
    {
        IOCTL_ADD = 0x22E004,
        IOCTL_REMOVE = 0x22A008,
        IOCTL_UPDATE = 0x22A00C,
        IOCTL_VERSION = 0x22E010
    }

    private static bool IoControl(IntPtr handle, IoCtlCode code, byte[]? input, int* result, int timeout)
    {
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            return false;

        byte[] inBuffer = new byte[32];
        var overlapped = new OVERLAPPED();

        if (input != null && input.Length > 0)
            Array.Copy(input, inBuffer, Math.Min(input.Length, inBuffer.Length));

        fixed (byte* buffer = inBuffer)
        {
            int outputLength = result != null ? sizeof(int) : 0;
            overlapped.hEvent = CreateEventW(null, false, false, null);
            if (overlapped.hEvent == IntPtr.Zero)
                return false;

            try
            {
                DeviceIoControl(
                    handle,
                    (uint)code,
                    buffer,
                    inBuffer.Length,
                    result,
                    outputLength,
                    null,
                    ref overlapped);

                bool success = GetOverlappedResultEx(handle, ref overlapped, out _, timeout, false);
                if (!success)
                {
                    CancelIoEx(handle, ref overlapped);
                    GetOverlappedResult(handle, ref overlapped, out _, true);
                }
                return success;
            }
            finally
            {
                CloseHandle(overlapped.hEvent);
            }
        }
    }

    private static bool IoControl(IntPtr handle, IoCtlCode code, byte[]? input, int timeout)
        => IoControl(handle, code, input, null, timeout);

    private static bool IoControl(IntPtr handle, IoCtlCode code, byte[]? input, out int result, int timeout)
    {
        int output = 0;
        bool ok = IoControl(handle, code, input, &output, timeout);
        result = output;
        return ok;
    }

    public void Dispose()
    {
        DeactivateZeroLightMode();
    }

    // Win32 SetupAPI / CfgMgr32 / Kernel32 / DisplayConfig P/Invoke
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint REG_SZ = 1;
    private const uint REG_MULTI_SZ = 7;
    private const uint CR_SUCCESS = 0;
    private const uint DN_DRIVER_LOADED = 0x00000002;
    private const uint DN_STARTED = 0x00000008;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    private const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    private const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    private const uint SDC_APPLY = 0x00000080;

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const uint DISPLAY_DEVICE_ACTIVE = 0x1;
    private const uint DISPLAY_DEVICE_ATTACHED = 0x2;

    private const int DM_POSITION = 0x00000020;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_SET_PRIMARY = 0x00000010;
    private const uint CDS_NORESET = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        private IntPtr _Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        private IntPtr _Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DETAIL_DATA_A
    {
        public int cbSize;
        public byte DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public IntPtr Pointer;
        public IntPtr hEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsA(ref Guid ClassGuid, void* Enumerator, void* hwndParent, uint Flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        SP_DEVINFO_DATA* DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint MemberIndex,
        SP_DEVICE_INTERFACE_DATA* DeviceInterfaceData);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailA(
        IntPtr DeviceInfoSet,
        SP_DEVICE_INTERFACE_DATA* DeviceInterfaceData,
        void* DeviceInterfaceDetailData,
        int DeviceInterfaceDetailDataSize,
        int* RequiredSize,
        SP_DEVINFO_DATA* DeviceInfoData);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, SP_DEVINFO_DATA* DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyA(
        IntPtr DeviceInfoSet,
        SP_DEVINFO_DATA* DeviceInfoData,
        uint Property,
        uint* PropertyRegDataType,
        void* PropertyBuffer,
        int PropertyBufferSize,
        int* RequiredSize);

    [DllImport("setupapi.dll")]
    private static extern uint CM_Get_DevNode_Status(uint* pulStatus, uint* pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("kernel32.dll")]
    private static extern int lstrlenA(byte* lpString);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileA(
        byte* lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        void* lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        void* hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint code,
        void* lpInBuffer,
        int nInBufferSize,
        void* lpOutBuffer,
        int nOutBufferSize,
        void* lpBytesReturned,
        ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResultEx(
        IntPtr handle,
        ref OVERLAPPED lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        int dwMilliseconds,
        [MarshalAs(UnmanagedType.Bool)] bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(IntPtr handle, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        IntPtr handle,
        ref OVERLAPPED lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(
        void* lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        IntPtr pathArray,
        uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        IntPtr lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);
}
