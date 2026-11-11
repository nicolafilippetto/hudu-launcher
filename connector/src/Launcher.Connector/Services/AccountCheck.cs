using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Launcher.Connector.Services;

/// <summary>
/// Il Launcher si installa per utente: se l'installer viene avviato con un altro account (per esempio
/// "Esegui come altro utente" con l'account amministrativo), finirebbe nel profilo di quell'account e
/// il tecnico non lo troverebbe. All'avvio si confronta l'account del processo con quello collegato
/// a Windows in questa sessione.
/// </summary>
internal static partial class AccountCheck
{
    /// <summary>
    /// L'account collegato alla sessione Windows, se diverso da quello del processo. Null se sono lo
    /// stesso o se non si riesce a stabilirlo (in caso di dubbio non si blocca nessuno).
    /// </summary>
    public static string? OtherSessionUser(out string processUser)
    {
        using var identity = WindowsIdentity.GetCurrent();
        processUser = identity.Name;
        try
        {
            if (SessionAccount() is not { } session)
            {
                return null;
            }

            var sessionName = session.Domain.Length > 0 ? $"{session.Domain}\\{session.User}" : session.User;
            if (string.Equals(UserPart(processUser), session.User, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Nomi diversi: si confrontano gli identificativi (SID), che non dipendono da come è
            // scritto il dominio. Se il nome non si traduce, non si blocca.
            var sessionSid = (SecurityIdentifier)new NTAccount(sessionName).Translate(typeof(SecurityIdentifier));
            return sessionSid == identity.User ? null : sessionName;
        }
        catch (SystemException ex)
        {
            Log.Warn("Controllo dell'account non riuscito: " + ex.Message);
            return null;
        }
    }

    private static string UserPart(string name) => name[(name.LastIndexOf('\\') + 1)..];

    private static (string Domain, string User)? SessionAccount()
    {
        var user = Query(WtsUserName);
        if (string.IsNullOrEmpty(user))
        {
            // Nessun utente collegato (servizio, sessione 0): niente da confrontare.
            return null;
        }

        return (Query(WtsDomainName) ?? "", user);
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    private const int CurrentSession = -1;

    private static string? Query(int infoClass)
    {
        if (!WTSQuerySessionInformation(CurrentServer, CurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
