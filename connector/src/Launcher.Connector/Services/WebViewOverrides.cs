namespace Launcher.Connector.Services;

/// <summary>
/// WebView2 legge alcune variabili d'ambiente che cambiano il browser integrato: argomenti aggiuntivi
/// (es. una porta di debug remoto, con cui un altro programma potrebbe leggere le pagine e i campi
/// compilati con le credenziali di Hudu), un altro runtime o un'altra cartella dei profili.
/// Il Launcher le toglie dal proprio processo all'avvio: valgono solo le impostazioni del Launcher.
/// Le stesse impostazioni nel registro stanno sotto le chiavi Policies, scrivibili solo dagli amministratori.
/// </summary>
public static class WebViewOverrides
{
    private static readonly string[] Variables =
    [
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
        "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
        "WEBVIEW2_USER_DATA_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE",
        "WEBVIEW2_CHANNEL_SEARCH_KIND",
        "WEBVIEW2_RELEASE_CHANNELS",
        "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER",
        "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER",
    ];

    public static void Clear()
    {
        foreach (var name in Variables)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, null);
                Log.Warn($"Variabile {name} ignorata: il browser integrato usa solo le impostazioni del Launcher");
            }
        }
    }
}
