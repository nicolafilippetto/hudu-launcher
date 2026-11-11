using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Launcher.Connector.Services;

/// <summary>
/// "Scalda e rilascia": poco dopo l'avvio, a Launcher inattivo, carica una volta i componenti lenti al
/// primo uso (runtime WebView2 di terminale e pagine web, controllo ActiveX di Desktop remoto, SSH.NET)
/// e li rilascia subito. Il costo di DLL, JIT e cache del disco è già pagato quando arriva la prima
/// connessione, senza tenere memoria occupata. Tutto invisibile: nessuna finestra, nessun processo
/// che resti aperto; ogni errore finisce solo nel registro.
/// </summary>
public static class Prewarm
{
    /// <summary>Attesa dopo l'avvio: all'accesso a Windows il PC ha altro da fare.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(45);

    private const string MsRdpClient9NotSafeForScripting = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
    private static int _started;

    /// <param name="parentWindow">Finestra del Launcher (anche nascosta) a cui agganciare la WebView2 invisibile.</param>
    /// <param name="isIdle">false se il tecnico sta già usando il Launcher: in quel caso non serve.</param>
    public static async Task RunAsync(IntPtr parentWindow, Func<bool> isIdle)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        await Task.Delay(Delay);
        if (!isIdle())
        {
            Log.Info("Preparazione dei componenti saltata: il Launcher è già in uso");
            return;
        }

        var ssh = Task.Run(WarmSsh);
        var rdp = WarmRdpAsync();
        var web = await WarmWebViewAsync(parentWindow);
        Log.Info($"Componenti preparati in background: WebView2 {web}, Desktop remoto {await rdp}, SSH {await ssh}");
    }

    /// <summary>
    /// WebView2 con la stessa cartella dati del terminale: controller nascosto e di dimensione zero sulla
    /// finestra del Launcher, chiuso subito. Avvia e chiude il runtime, senza mostrare nulla.
    /// </summary>
    private static async Task<string> WarmWebViewAsync(IntPtr parentWindow)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ConnectorSettings.WebViewDataDirectory);
            var controller = await environment.CreateCoreWebView2ControllerAsync(parentWindow);
            try
            {
                controller.IsVisible = false;
                controller.Bounds = System.Drawing.Rectangle.Empty;
                controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
            }
            finally
            {
                controller.Close();
            }

            return $"{watch.ElapsedMilliseconds} ms";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            return "non preparato (" + ex.Message + ")";
        }
    }

    /// <summary>
    /// Desktop remoto: l'oggetto COM del controllo (mstscax.dll) creato e rilasciato su un thread STA a
    /// parte, senza ospitarlo in alcuna finestra.
    /// </summary>
    private static Task<string> WarmRdpAsync()
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid(MsRdpClient9NotSafeForScripting), throwOnError: true)!;
                var instance = Activator.CreateInstance(type);
                if (instance is not null && Marshal.IsComObject(instance))
                {
                    Marshal.FinalReleaseComObject(instance);
                }

                done.TrySetResult($"{watch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException or UnauthorizedAccessException or ArgumentException)
            {
                done.TrySetResult("non preparato (" + ex.Message + ")");
            }
        })
        {
            IsBackground = true,
            Name = "Preparazione Desktop remoto",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>SSH.NET: tipi, algoritmi e crittografia inizializzati senza collegarsi a nulla.</summary>
    private static string WarmSsh()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var password = new Renci.SshNet.PasswordAuthenticationMethod("preparazione", "preparazione");
            var info = new Renci.SshNet.ConnectionInfo("127.0.0.1", 22, "preparazione", password);
            using var client = new Renci.SshNet.SshClient(info);
            return $"{watch.ElapsedMilliseconds} ms";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return "non preparato (" + ex.Message + ")";
        }
    }
}
