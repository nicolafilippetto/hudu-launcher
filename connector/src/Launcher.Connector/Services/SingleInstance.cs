using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Launcher.Connector.Services;

/// <summary>
/// Una sola istanza per utente: i link successivi arrivano alla finestra già aperta tramite una
/// named pipe accessibile solo all'utente corrente.
/// </summary>
public static class SingleInstance
{
    public const string ActivateMessage = "::activate";

    private static readonly string Id = "HuduLauncher-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    private static Mutex? _mutex;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out var created);
        return created;
    }

    public static void SendToPrimary(string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(message);
            pipe.Write(bytes);
            pipe.Flush();
        }
        catch (TimeoutException)
        {
            Log.Warn("L'istanza principale non risponde");
        }
        catch (IOException ex)
        {
            Log.Error("Invio all'istanza principale non riuscito", ex);
        }
    }

    /// <summary>Ascolta i messaggi delle istanze successive finché l'app è aperta.</summary>
    public static async Task ListenAsync(Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var memory = new MemoryStream();
                var buffer = new byte[512];
                int read;
                while ((read = await pipe.ReadAsync(buffer, ct)) > 0 && memory.Length < 4096)
                {
                    memory.Write(buffer, 0, read);
                }

                onMessage(Encoding.UTF8.GetString(memory.ToArray()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                Log.Error("Errore sulla pipe dell'istanza", ex);
            }
        }
    }
}
