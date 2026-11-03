using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Velopack.Locators;

namespace Launcher.Connector.Services;

/// <summary>
/// Sistema i collegamenti del Launcher (menu Start e desktop dell'utente). Il Launcher dichiara a
/// Windows un AppUserModelID (quello di Velopack): la barra delle applicazioni prende icona e
/// raggruppamento dal collegamento con lo stesso ID. L'installer exe lo scrive nel collegamento,
/// l'MSI no, e con l'MSI la finestra compariva con l'icona generica di Windows. A ogni avvio i
/// collegamenti che puntano al Launcher ricevono ID e icona giusti (solo se mancano o sono diversi).
/// </summary>
public static class ShortcutRepair
{
    public static void Run()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current is not { AppUserModelId: { Length: > 0 } aumid, RootAppDir: { Length: > 0 } root }
                || Environment.ProcessPath is not { } exe)
            {
                return;
            }

            var rootDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var link in Candidates())
            {
                try
                {
                    Repair(link, rootDir, exe, aumid);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Log.Warn($"Collegamento {Path.GetFileName(link)} non aggiornato: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Controllo dei collegamenti non riuscito: " + ex.Message);
        }
    }

    /// <summary>File .lnk nel menu Start (anche in una sottocartella) e sul desktop dell'utente.</summary>
    private static IEnumerable<string> Candidates()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
        return new[] { programs, desktop }
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", options));
    }

    private static void Repair(string link, string rootDir, string exe, string aumid)
    {
        var shellLink = (IShellLinkW)new CShellLink();
        try
        {
            var file = (IPersistFile)shellLink;
            // In lettura e scrittura: aperto in sola lettura, il salvataggio sullo stesso file viene
            // rifiutato con STG_E_ACCESSDENIED.
            file.Load(link, StgmReadWrite);

            var target = new StringBuilder(1024);
            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            if (target.Length == 0 || !Path.GetFullPath(target.ToString()).StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var store = (IPropertyStore)shellLink;
            var key = AppUserModelIdKey;
            var current = ReadString(store, ref key);
            var icon = new StringBuilder(1024);
            shellLink.GetIconLocation(icon, icon.Capacity, out _);
            var iconOk = icon.Length > 0 && File.Exists(Environment.ExpandEnvironmentVariables(icon.ToString()));
            if (current == aumid && iconOk)
            {
                return;
            }

            if (!iconOk)
            {
                shellLink.SetIconLocation(exe, 0);
            }

            if (current != aumid)
            {
                var value = PropVariant.FromString(aumid);
                try
                {
                    Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                    Marshal.ThrowExceptionForHR(store.Commit());
                }
                finally
                {
                    value.Clear();
                }
            }

            file.Save(null, true);
            Log.Info($"Collegamento {Path.GetFileName(link)} aggiornato (ID dell'app{(iconOk ? "" : " e icona")}) per la barra delle applicazioni");
        }
        finally
        {
            Marshal.ReleaseComObject(shellLink);
        }
    }

    private static string? ReadString(IPropertyStore store, ref PropertyKey key)
    {
        var value = default(PropVariant);
        try
        {
            return store.GetValue(ref key, out value) == 0 && value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            value.Clear();
        }
    }

    private const ushort VtLpwstr = 31;
    private const int StgmReadWrite = 0x2;

    /// <summary>PKEY_AppUserModel_ID.</summary>
    private static PropertyKey AppUserModelIdKey => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // 24 byte come PROPVARIANT a 64 bit: Windows può scrivere l'intera unione.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;

        public static PropVariant FromString(string text) => new() { VarType = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(text) };

        public void Clear()
        {
            _ = PropVariantClear(ref this);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
