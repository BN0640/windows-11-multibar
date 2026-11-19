using System.IO;
using System.Runtime.InteropServices;

namespace MultiBar;

public sealed record JumpItem(string Name, string Target);

/// <summary>
/// Reads an app's "Recent" jump list (the same list the Windows taskbar shows on right-click)
/// through the public IApplicationDocumentLists API.
/// </summary>
public static class JumpLists
{
    [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IObjectArray list);
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        void GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
    }

    static readonly Guid CLSID_ApplicationDocumentLists = new("86bec222-30f2-47e0-9f25-60d11cd75c28");
    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_FILESYSPATH = 0x80058000, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    public static List<JumpItem> Recent(string? appId, int max = 10)
    {
        var result = new List<JumpItem>();
        if (string.IsNullOrWhiteSpace(appId)) return result;
        try
        {
            var lists = (IApplicationDocumentLists)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ApplicationDocumentLists)!)!;
            lists.SetAppID(appId);
            if (lists.GetList(0 /*ADLT_RECENT*/, (uint)max, typeof(IObjectArray).GUID, out var array) != 0 || array == null) return result;
            array.GetCount(out uint count);
            for (uint i = 0; i < count && result.Count < max; i++)
            {
                var item = ReadItem(array, i);
                if (item != null && !result.Any(r => string.Equals(r.Target, item.Target, StringComparison.OrdinalIgnoreCase)))
                    result.Add(item);
            }
            Marshal.ReleaseComObject(array);
            Marshal.ReleaseComObject(lists);
        }
        catch { }
        return result;
    }

    static JumpItem? ReadItem(IObjectArray array, uint i)
    {
        if (array.GetAt(i, typeof(IShellItem).GUID, out var o) == 0 && o is IShellItem si)
        {
            string? target = Name(si, SIGDN_FILESYSPATH) ?? Name(si, SIGDN_DESKTOPABSOLUTEPARSING);
            string name = Name(si, SIGDN_NORMALDISPLAY) ?? (target != null ? Path.GetFileName(target) : "");
            Marshal.ReleaseComObject(si);
            if (target == null) return null;
            if (target.Length > 2 && target[1] == ':' && !File.Exists(target) && !Directory.Exists(target)) return null;
            return new JumpItem(name, target);
        }
        if (array.GetAt(i, typeof(IShellLinkW).GUID, out o) == 0 && o is IShellLinkW link)
        {
            var path = new System.Text.StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var desc = new System.Text.StringBuilder(512);
            try { link.GetDescription(desc, desc.Capacity); } catch { }
            Marshal.ReleaseComObject(link);
            if (path.Length == 0) return null;
            return new JumpItem(desc.Length > 0 ? desc.ToString() : Path.GetFileName(path.ToString()), path.ToString());
        }
        return null;
    }

    static string? Name(IShellItem si, uint sigdn)
    {
        if (si.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// AppID Windows uses for the app's jump list: its explicit AUMID, or for apps without one the exe path
    /// with the known-folder GUID in front (e.g. {6D809377-...}\Inkscape\bin\inkscape.exe).
    /// </summary>
    public static string? AppIdFor(string? aumid, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(aumid)) return aumid;
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        (string folder, string guid)[] known =
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "{6D809377-6AF0-444B-8957-A3773F02200E}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.System), "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
        };
        foreach (var (folder, guid) in known)
            if (exePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                return guid + exePath[folder.Length..];
        return exePath;
    }
}
