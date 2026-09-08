using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Jester.Host.Services;

/// <summary>
/// Windows system proxy via WinINET PER_CONNECTION options
/// (same approach as Clash Verge) so Settings → Proxy shows enabled.
/// </summary>
public static class SystemProxy
{
    const int InternetOptionPerConnectionOption = 75;
    const int InternetOptionSettingsChanged = 39;
    const int InternetOptionRefresh = 37;
    const int InternetOptionProxySettingsChanged = 95;

    const int InternetPerConnFlags = 1;
    const int InternetPerConnProxyServer = 2;
    const int InternetPerConnProxyBypass = 3;

    const uint ProxyTypeDirect = 0x00000001;
    const uint ProxyTypeProxy = 0x00000002;

    public static void Set(bool enable, int port)
    {
        var server = $"127.0.0.1:{Math.Max(1, port)}";
        var bypass = "localhost;127.*;10.*;192.168.*;172.16.*;172.17.*;172.18.*;172.19.*;<local>";

        if (enable) SetPerConn(ProxyTypeDirect | ProxyTypeProxy, server, bypass);
        else SetPerConn(ProxyTypeDirect, null, null);

        using (var key = Registry.CurrentUser.OpenSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
        {
            if (key != null)
            {
                if (enable)
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", server, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
                    try { key.DeleteValue("AutoConfigURL", false); } catch { /* ignore */ }
                }
                else
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }
            }
        }

        Notify();
        SetWinHttp(enable, port);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
        return key != null && Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 1;
    }

    static void SetPerConn(uint flags, string? server, string? bypass)
    {
        var allocations = new List<IntPtr>();
        try
        {
            var opts = new List<Native.Option>();
            opts.Add(new Native.Option
            {
                DwOption = InternetPerConnFlags,
                Value = new Native.OptionValue { DwValue = flags }
            });

            if (!string.IsNullOrEmpty(server))
            {
                var p = Marshal.StringToHGlobalUni(server);
                allocations.Add(p);
                opts.Add(new Native.Option
                {
                    DwOption = InternetPerConnProxyServer,
                    Value = new Native.OptionValue { PszValue = p }
                });
            }

            if (!string.IsNullOrEmpty(bypass))
            {
                var p = Marshal.StringToHGlobalUni(bypass);
                allocations.Add(p);
                opts.Add(new Native.Option
                {
                    DwOption = InternetPerConnProxyBypass,
                    Value = new Native.OptionValue { PszValue = p }
                });
            }

            var arr = opts.ToArray();
            var arrSize = Marshal.SizeOf<Native.Option>() * arr.Length;
            var arrPtr = Marshal.AllocHGlobal(arrSize);
            allocations.Add(arrPtr);
            for (var i = 0; i < arr.Length; i++)
                Marshal.StructureToPtr(arr[i], arrPtr + i * Marshal.SizeOf<Native.Option>(), false);

            var list = new Native.OptionList
            {
                Size = (uint)Marshal.SizeOf<Native.OptionList>(),
                Connection = IntPtr.Zero,
                OptionCount = (uint)arr.Length,
                OptionError = 0,
                Options = arrPtr
            };

            var listPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.OptionList>());
            allocations.Add(listPtr);
            Marshal.StructureToPtr(list, listPtr, false);

            Native.InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, listPtr,
                (uint)Marshal.SizeOf<Native.OptionList>());
        }
        finally
        {
            // Free strings first (first N), then buffers — track separately
            foreach (var p in allocations)
            {
                try { Marshal.FreeHGlobal(p); } catch { /* ignore */ }
            }
        }
    }

    static void Notify()
    {
        Native.InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0);
        Native.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        Native.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    static void SetWinHttp(bool enable, int port)
    {
        try
        {
            var args = enable
                ? $"winhttp set proxy proxy-server=\"127.0.0.1:{port}\" bypass-list=\"localhost;127.*;10.*;192.168.*;172.16.*;<local>\""
                : "winhttp reset proxy";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(4000);
        }
        catch { /* optional */ }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct OptionList
        {
            public uint Size;
            public IntPtr Connection;
            public uint OptionCount;
            public uint OptionError;
            public IntPtr Options;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Option
        {
            public int DwOption;
            public OptionValue Value;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct OptionValue
        {
            [FieldOffset(0)] public uint DwValue;
            [FieldOffset(0)] public IntPtr PszValue;
            [FieldOffset(0)] public long FtValue;
        }

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, uint dwBufferLength);
    }
}
