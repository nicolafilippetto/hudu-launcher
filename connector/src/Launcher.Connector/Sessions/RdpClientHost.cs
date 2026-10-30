using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Launcher.Connector.Sessions;

/// <summary>
/// Eventi del controllo Desktop remoto (dispinterface IMsTscAxEvents). I DISPID sono quelli
/// documentati da Microsoft; gli eventi non dichiarati vengono ignorati.
/// </summary>
[ComImport]
[Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)]
    void OnConnecting();

    [DispId(2)]
    void OnConnected();

    [DispId(3)]
    void OnLoginComplete();

    [DispId(4)]
    void OnDisconnected(int discReason);

    [DispId(10)]
    void OnFatalError(int errorCode);

    [DispId(11)]
    void OnWarning(int warningCode);

    [DispId(22)]
    void OnLogonError(int lError);
}

/// <summary>Proprietà con nome del controllo (IMsRdpExtendedSettings, interfaccia IUnknown).</summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);

    [return: MarshalAs(UnmanagedType.Struct)]
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>
/// Metodi non scriptabili del controllo (IMsRdpClientNonScriptable, solo vtable). I primi dieci sono
/// quelli di IMsTscNonScriptable: non si usano, ma vanno dichiarati per l'ordine della vtable.
/// </summary>
[ComImport]
[Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable
{
    void SetClearTextPassword(IntPtr value);

    void SetPortablePassword(IntPtr value);

    IntPtr GetPortablePassword();

    void SetPortableSalt(IntPtr value);

    IntPtr GetPortableSalt();

    void SetBinaryPassword(IntPtr value);

    IntPtr GetBinaryPassword();

    void SetBinarySalt(IntPtr value);

    IntPtr GetBinarySalt();

    void ResetPassword();

    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);

    /// <summary>Array passati come puntatori: VARIANT_BOOL (short) per i rilasci e scancode.</summary>
    void SendKeys(int numKeys, IntPtr keyUp, IntPtr keyData);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink(RdpClientHost host) : IMsTscAxEvents
{
    public void OnConnecting() => host.RaiseStatus("Connessione in corso…");

    public void OnConnected() => host.RaiseStatus("Connesso, accesso in corso…");

    public void OnLoginComplete() => host.RaiseLoginComplete();

    public void OnDisconnected(int discReason) => host.RaiseDisconnected(discReason);

    public void OnFatalError(int errorCode) => host.RaiseFatalError(errorCode);

    public void OnWarning(int warningCode) => host.RaiseStatus($"Avviso RDP {warningCode}");

    public void OnLogonError(int lError) => host.RaiseLogonError(lError);
}

/// <summary>
/// Controllo ActiveX di Desktop remoto (MsRdpClient9NotSafeForScripting, presente da Windows 8.1).
/// Le proprietà si impostano in late binding, senza assembly di interop generati.
/// </summary>
public sealed class RdpClientHost : AxHost
{
    private const string MsRdpClient9NotSafeForScripting = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
    private ConnectionPointCookie? _cookie;

    public RdpClientHost()
        : base(MsRdpClient9NotSafeForScripting)
    {
    }

    public event Action<string>? Status;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;
    public event Action<int>? LogonError;

    /// <summary>L'oggetto COM del controllo (IDispatch).</summary>
    public dynamic Rdp => GetOcx() ?? throw new InvalidOperationException("Controllo RDP non inizializzato.");

    /// <summary>Imposta una proprietà estesa; false se il controllo non la conosce.</summary>
    public bool TrySetExtended(string name, object value)
    {
        try
        {
            if (GetOcx() is IMsRdpExtendedSettings extended)
            {
                extended.SetProperty(name, ref value);
                return true;
            }
        }
        catch (COMException)
        {
        }
        catch (ArgumentException)
        {
        }

        return false;
    }

    /// <summary>Invia i tasti alla sessione in un'unica sequenza (max 20); false se il controllo non lo consente.</summary>
    public bool SendKeys(IReadOnlyList<Launcher.Connector.Core.KeyStroke> strokes)
    {
        try
        {
            if (strokes.Count is 0 or > Launcher.Connector.Core.KeyStrokes.MaxPerSend || GetOcx() is not IMsRdpClientNonScriptable client)
            {
                return false;
            }

            var up = strokes.Select(s => (short)(s.KeyUp ? -1 : 0)).ToArray();
            var codes = strokes.Select(s => s.ScanCode).ToArray();
            Focus();
            unsafe
            {
                fixed (short* pUp = up)
                fixed (int* pCodes = codes)
                {
                    client.SendKeys(codes.Length, (IntPtr)pUp, (IntPtr)pCodes);
                }
            }

            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    protected override void CreateSink()
    {
        try
        {
            _cookie = new ConnectionPointCookie(GetOcx()!, new RdpEventSink(this), typeof(IMsTscAxEvents));
        }
        catch (InvalidCastException)
        {
            _cookie = null;
        }
        catch (ArgumentException)
        {
            _cookie = null;
        }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }

    internal void RaiseStatus(string text) => Post(() => Status?.Invoke(text));

    internal void RaiseLoginComplete() => Post(() => LoginComplete?.Invoke());

    internal void RaiseDisconnected(int reason) => Post(() => Disconnected?.Invoke(reason));

    internal void RaiseFatalError(int code) => Post(() => FatalError?.Invoke(code));

    internal void RaiseLogonError(int code) => Post(() => LogonError?.Invoke(code));

    // Gli eventi COM arrivano durante le chiamate del controllo: si gestiscono subito dopo.
    private void Post(Action action)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(action);
        }
    }
}
