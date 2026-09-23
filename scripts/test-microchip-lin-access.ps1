[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Uses the installed Windows HID driver. No administrator rights, device writes,
# feature/output reports, firmware changes, or LIN initialization are performed.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class MicrochipLinAccessProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct Caps {
        public ushort Usage, UsagePage, InputBytes, OutputBytes, FeatureBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons,
            OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", CharSet=CharSet.Unicode)]
    static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder text, int bytes);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);

    public sealed class Result {
        public string Product { get; set; }
        public string UsbIdentity { get; set; }
        public bool ReadAccess { get; set; }
        public int OpenError { get; set; }
        public int InputReportBytes { get; set; }
        public int OutputReportBytes { get; set; }
    }
    public static Result[] Run() {
        var results = new List<Result>();
        Guid guid; HidD_GetHidGuid(out guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            for (uint index=0;;index++) {
                var item = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item)) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                uint needed;
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                if (needed < 8) throw new InvalidOperationException("Invalid HID interface detail size.");
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, needed, out needed, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path.IndexOf("vid_04d8&pid_0a04", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var result = new Result { UsbIdentity="04D8:0A04", Product="LIN Bus Analyzer" };
                    // GENERIC_READ only; do not acquire write access or send commands.
                    using (var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                        result.ReadAccess = !handle.IsInvalid;
                        result.OpenError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                        if (!handle.IsInvalid) {
                            var name = new StringBuilder(128);
                            if (HidD_GetProductString(handle, name, 256) && name.Length > 0) result.Product=name.ToString();
                            IntPtr preparsed;
                            if (HidD_GetPreparsedData(handle, out preparsed)) {
                                try {
                                    Caps caps;
                                    if (HidP_GetCaps(preparsed, out caps) >= 0) {
                                        result.InputReportBytes=caps.InputBytes;
                                        result.OutputReportBytes=caps.OutputBytes;
                                    }
                                } finally { HidD_FreePreparsedData(preparsed); }
                            }
                        }
                    }
                    results.Add(result);
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return results.ToArray();
    }
}
'@
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$results = @([MicrochipLinAccessProbe]::Run())
[pscustomobject]@{ Elevated=$isAdministrator; Devices=$results; LinCommandsSent=0; FramesRead=0 } | ConvertTo-Json -Depth 4
if ($results.Count -eq 0) { throw 'Microchip LIN analyzer 04D8:0A04 was not found.' }
if (!($results | Where-Object { $_.ReadAccess })) { throw 'Windows denied read access; see OpenError in the report.' }
