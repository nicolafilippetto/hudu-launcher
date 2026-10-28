using System.Diagnostics;
using System.Runtime.InteropServices;

// Le DLL di Windows si caricano solo da System32, mai dalla cartella dell'app o corrente (DLL hijacking).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Launcher.Connector.Services;

/// <summary>Funzioni di Windows per trovare, ridurre a icona e chiudere le finestre di RustDesk.</summary>
internal static partial class Native
{
    private const int SwShowMinNoActive = 7;
    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // DllImport: il marshalling dei delegate non è supportato da LibraryImport.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hwnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Finestre visibili dei processi indicati il cui titolo contiene il testo dato.</summary>
    public static List<IntPtr> FindWindows(IReadOnlySet<int> processIds, string titleContains)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains((int)pid))
            {
                return true;
            }

            var buffer = new char[512];
            var length = GetWindowText(hwnd, buffer, buffer.Length);
            if (length > 0 && new string(buffer, 0, length).Contains(titleContains, StringComparison.Ordinal))
            {
                result.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, SwShowMinNoActive);

    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

    public static HashSet<int> ProcessIds(string processName) =>
        Process.GetProcessesByName(processName).Select(p =>
        {
            using (p)
            {
                return p.Id;
            }
        }).ToHashSet();
}
