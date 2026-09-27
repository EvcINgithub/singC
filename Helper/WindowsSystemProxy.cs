using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace singC.Helpers;

public sealed class WindowsSystemProxy : ISystemProxyBackend
{
    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(0)] public IntPtr Text;
        [FieldOffset(0)] public System.Runtime.InteropServices.ComTypes.FILETIME FileTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Option { public int Id; public OptionValue Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count;
        public int Error;
        public IntPtr Options;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Query(IntPtr handle, int option, ref OptionList list, ref int size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Set(IntPtr handle, int option, ref OptionList list, int size);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Notify(IntPtr handle, int option, IntPtr buffer, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);

    public SystemProxySettings Read() => Exchange(null);
    public void Write(SystemProxySettings settings) => Exchange(settings);

    private static SystemProxySettings Exchange(SystemProxySettings? settings)
    {
        int optionSize = Marshal.SizeOf<Option>();
        var options = new Option[4];
        IntPtr buffer = Marshal.AllocHGlobal(optionSize * options.Length);
        bool queried = false;
        try
        {
            string[] values = { "", settings?.Server ?? "", settings?.Bypass ?? "", settings?.AutoConfigUrl ?? "" };
            for (int i = 0; i < options.Length; i++)
            {
                // FLAGS_UI reads the persisted auto-detect choice rather than its transient result.
                options[i].Id = settings == null && i == 0 ? 10 : i + 1;
                if (i == 0) options[i].Value.Number = settings?.Flags ?? 0;
                else if (settings != null) options[i].Value.Text = Marshal.StringToHGlobalUni(values[i]);
                Marshal.StructureToPtr(options[i], buffer + i * optionSize, false);
            }
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = options.Length, Options = buffer };
            if (settings == null)
            {
                int size = list.Size;
                bool success = Query(IntPtr.Zero, 75, ref list, ref size);
                queried = true;
                for (int i = 0; i < options.Length; i++) options[i] = Marshal.PtrToStructure<Option>(buffer + i * optionSize);
                if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 Windows 系统代理失败。");
                return new(options[0].Value.Number, Marshal.PtrToStringUni(options[1].Value.Text) ?? "",
                    Marshal.PtrToStringUni(options[2].Value.Text) ?? "", Marshal.PtrToStringUni(options[3].Value.Text) ?? "");
            }
            if (!Set(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "设置 Windows 系统代理失败。");
            if (!Notify(IntPtr.Zero, 39, IntPtr.Zero, 0) || !Notify(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "刷新 Windows 系统代理失败。");
            return settings;
        }
        finally
        {
            for (int i = 1; i < options.Length; i++)
                if (options[i].Value.Text != IntPtr.Zero)
                {
                    if (queried) GlobalFree(options[i].Value.Text);
                    else Marshal.FreeHGlobal(options[i].Value.Text);
                }
            Marshal.FreeHGlobal(buffer);
        }
    }
}
