using System;
using System.Runtime.InteropServices;

namespace Chronos.App.Services;

internal static class NativeFileDialog
{
    // COM CLSIDs
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");

    // COM IIDs
    private static readonly Guid IID_IFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [Flags]
    private enum FOS : uint
    {
        OVERWRITEPROMPT = 0x00000002,
        STRICTFILETYPES = 0x00000004,
        NOCHANGEDIR = 0x00000008,
        PICKFOLDERS = 0x00000020,
        FORCEFILESYSTEM = 0x00000040,
        FILEMUSTEXIST = 0x00001000,
        PATHMUSTEXIST = 0x00000800,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    public static bool TryPickOpenFile(IntPtr owner, string filter, out string? path)
    {
        path = null;
        var clsid = CLSID_FileOpenDialog;
        var iid = IID_IFileDialog;

        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj);
        if (hr != 0) return false;

        var dialog = (IFileDialog)obj;
        var filters = ParseFilters(filter);
        if (filters.Length > 0)
            dialog.SetFileTypes((uint)filters.Length, filters);

        dialog.SetOptions(FOS.FORCEFILESYSTEM | FOS.FILEMUSTEXIST | FOS.PATHMUSTEXIST | FOS.NOCHANGEDIR);

        hr = dialog.Show(owner);
        if (hr != 0) return false;

        dialog.GetResult(out var item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var resultPath);
        path = resultPath;
        return true;
    }

    public static bool TryPickFolder(IntPtr owner, out string? path)
    {
        path = null;
        var clsid = CLSID_FileOpenDialog;
        var iid = IID_IFileDialog;

        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj);
        if (hr != 0) return false;

        var dialog = (IFileDialog)obj;
        dialog.SetOptions(FOS.FORCEFILESYSTEM | FOS.PICKFOLDERS | FOS.PATHMUSTEXIST | FOS.NOCHANGEDIR);

        hr = dialog.Show(owner);
        if (hr != 0) return false;

        dialog.GetResult(out var item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var resultPath);
        path = resultPath;
        return true;
    }

    public static bool TryPickSaveFile(IntPtr owner, string filter, string defaultExt, string suggestedFileName, out string? path)
    {
        path = null;
        var clsid = CLSID_FileSaveDialog;
        var iid = IID_IFileDialog;

        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var obj);
        if (hr != 0) return false;

        var dialog = (IFileDialog)obj;
        var filters = ParseFilters(filter);
        if (filters.Length > 0)
            dialog.SetFileTypes((uint)filters.Length, filters);

        dialog.SetOptions(FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST | FOS.OVERWRITEPROMPT | FOS.NOCHANGEDIR);
        dialog.SetDefaultExtension(defaultExt);
        if (!string.IsNullOrWhiteSpace(suggestedFileName))
            dialog.SetFileName(suggestedFileName);

        hr = dialog.Show(owner);
        if (hr != 0) return false;

        dialog.GetResult(out var item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var resultPath);
        path = resultPath;
        return true;
    }

    /// <summary>
    /// Parse null-separated filter string "Name\0*.ext\0Name2\0*.ext2\0\0" into COMDLG_FILTERSPEC array.
    /// </summary>
    private static COMDLG_FILTERSPEC[] ParseFilters(string filter)
    {
        var parts = filter.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var specs = new COMDLG_FILTERSPEC[parts.Length / 2];
        for (int i = 0; i < specs.Length; i++)
        {
            specs[i] = new COMDLG_FILTERSPEC
            {
                pszName = parts[i * 2],
                pszSpec = parts[i * 2 + 1]
            };
        }
        return specs;
    }
}
