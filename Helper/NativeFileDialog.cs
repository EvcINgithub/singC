using System;
using System.Runtime.InteropServices;

namespace singC
{
    public static class NativeFileDialog
    {
        public static string? ShowOpenFileDialog(IntPtr hwndOwner, string filter, string? defaultFolder = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();

            // 设置过滤器
            string[] parts = filter.Split('|');
            if (parts.Length == 2)
            {
                var spec = new COMDLG_FILTERSPEC
                {
                    pszName = parts[0],
                    pszSpec = parts[1]
                };
                dialog.SetFileTypes(1, new[] { spec });
            }

            // 选项
            dialog.SetOptions(
                FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST |
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM);

            // 默认文件夹（可选）
            if (!string.IsNullOrEmpty(defaultFolder))
            {
                try
                {
                    if (SHCreateItemFromParsingName(defaultFolder, IntPtr.Zero, typeof(IShellItem).GUID, out IntPtr psi) == 0)
                    {
                        dialog.SetDefaultFolder((IShellItem)Marshal.GetObjectForIUnknown(psi));
                        Marshal.Release(psi);
                    }
                }
                catch { }
            }

            int hr = dialog.Show(hwndOwner);
            if (hr != 0) return null;

            dialog.GetResult(out IShellItem psiResult);
            if (psiResult == null) return null;

            string? path = GetShellItemDisplayName(psiResult);
            Marshal.ReleaseComObject(psiResult);
            return path;
        }

        private static string? GetShellItemDisplayName(IShellItem shellItem)
        {
            IntPtr pszName = IntPtr.Zero;
            try
            {
                shellItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out pszName);
                if (pszName != IntPtr.Zero)
                {
                    string? path = Marshal.PtrToStringUni(pszName);
                    Marshal.FreeCoTaskMem(pszName);
                    return path;
                }
                return null;
            }
            catch
            {
                if (pszName != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pszName);
                return null;
            }
        }

        // COM 类
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            int SetFileTypes(int cFileTypes, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] COMDLG_FILTERSPEC[] rgFilterSpec);
            int SetFileTypeIndex(int iFileType);
            int GetFileTypeIndex(out int piFileType);
            int Advise(IntPtr pfde, out uint pdwCookie);
            int Unadvise(uint dwCookie);
            int SetOptions(FILEOPENDIALOGOPTIONS fos);
            int GetOptions(out FILEOPENDIALOGOPTIONS pfos);
            int SetDefaultFolder(IShellItem psi);
            int SetFolder(IShellItem psi);
            int GetFolder(out IShellItem ppsi);
            int GetCurrentSelection(out IShellItem ppsi);
            int SetFileName(string pszName);
            int GetFileName(out string pszName);
            int SetTitle(string pszTitle);
            int SetOkButtonLabel(string pszText);
            int SetFileNameLabel(string pszLabel);
            int GetResult(out IShellItem ppsi);
            int AddPlace(IShellItem psi, int alignment);
            int SetDefaultExtension(string pszDefaultExtension);
            int Close(int hr);
            int SetClientGuid(ref Guid guid);
            int ClearClientData();
            int SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            int GetParent(out IShellItem ppsi);
            int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            int GetAttributes(uint sfgaoAttribs, out uint psfgaoAttribs);
            int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000
        }

        [Flags]
        private enum FILEOPENDIALOGOPTIONS : uint
        {
            FOS_FILEMUSTEXIST = 0x00001000,
            FOS_FORCEFILESYSTEM = 0x00000002
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppv);
    }
}
