namespace Launcher.Connector.Core;

/// <summary>Pressione o rilascio di un tasto, come scancode della tastiera fisica.</summary>
public readonly record struct KeyStroke(int ScanCode, bool KeyUp);

/// <summary>Sequenze di tasti per il controllo Desktop remoto, che ne accetta al massimo 20 per invio.</summary>
public static class KeyStrokes
{
    public const int MaxPerSend = 20;
    public const int Shift = 0x2A;
    public const int Ctrl = 0x1D;
    public const int Alt = 0x38;

    /// <summary>Tasti premuti nell'ordine dato e rilasciati al contrario.</summary>
    public static IReadOnlyList<KeyStroke> Chord(IReadOnlyList<int> scanCodes) =>
        [.. scanCodes.Select(c => new KeyStroke(c, false)), .. scanCodes.Reverse().Select(c => new KeyStroke(c, true))];

    /// <summary>Un tasto con i modificatori richiesti (Ctrl+Alt equivale ad AltGr).</summary>
    public static IReadOnlyList<KeyStroke> Key(int scanCode, bool shift = false, bool ctrl = false, bool alt = false)
    {
        var codes = new List<int>(4);
        if (ctrl)
        {
            codes.Add(Ctrl);
        }

        if (alt)
        {
            codes.Add(Alt);
        }

        if (shift)
        {
            codes.Add(Shift);
        }

        codes.Add(scanCode);
        return Chord(codes);
    }

    /// <summary>Raggruppa i tasti in invii da al massimo 20, senza mai dividere un carattere.</summary>
    public static IEnumerable<IReadOnlyList<KeyStroke>> Batch(IEnumerable<IReadOnlyList<KeyStroke>> characters)
    {
        var batch = new List<KeyStroke>(MaxPerSend);
        foreach (var strokes in characters)
        {
            if (batch.Count + strokes.Count > MaxPerSend && batch.Count > 0)
            {
                yield return batch;
                batch = new List<KeyStroke>(MaxPerSend);
            }

            batch.AddRange(strokes);
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}

/// <summary>Combinazione di tasti da inviare alla sessione RDP.</summary>
public sealed record KeyCombination(string Label, string ToolTip, IReadOnlyList<int> ScanCodes)
{
    public IReadOnlyList<KeyStroke> Strokes => KeyStrokes.Chord(ScanCodes);
}

/// <summary>
/// Tasti speciali per le sessioni RDP. Solo scancode non estesi: sono quelli che il controllo
/// Desktop remoto accetta in modo affidabile (niente tasto Windows, che è esteso).
/// </summary>
public static class RemoteKeys
{
    private const int Esc = 0x01;
    private const int Tab = 0x0F;
    private const int Ctrl = KeyStrokes.Ctrl;
    private const int Shift = KeyStrokes.Shift;
    private const int Alt = KeyStrokes.Alt;
    private const int F4 = 0x3E;
    private const int Delete = 0x53;

    public static KeyCombination CtrlAltDel { get; } = new("Ctrl+Alt+Canc", "Schermata di sicurezza: blocca, cambia password, disconnetti", [Ctrl, Alt, Delete]);

    public static KeyCombination CtrlEsc { get; } = new("Ctrl+Esc", "Apre il menu Start", [Ctrl, Esc]);

    /// <summary>Combinazioni del menu "Altri tasti".</summary>
    public static IReadOnlyList<KeyCombination> Others { get; } =
    [
        new("Ctrl+Maiusc+Esc", "Gestione attività", [Ctrl, Shift, Esc]),
        new("Alt+Tab", "Passa all'applicazione successiva", [Alt, Tab]),
        new("Alt+F4", "Chiude la finestra attiva", [Alt, F4]),
        CtrlEsc,
    ];
}

/// <summary>Un tasto da digitare: un carattere Unicode oppure un tasto virtuale (Invio, Tab).</summary>
public readonly record struct TypedKey(char Character, ushort VirtualKey)
{
    public const ushort Return = 0x0D;
    public const ushort Tab = 0x09;

    public bool IsVirtual => VirtualKey != 0;
}

/// <summary>Testo degli appunti trasformato in tasti da digitare nella sessione.</summary>
public static class ClipboardTyping
{
    /// <summary>Oltre questa lunghezza la digitazione diventa lenta e rischiosa: si rifiuta.</summary>
    public const int MaxCharacters = 4000;

    /// <summary>
    /// Ritorni a capo (\r\n, \r o \n) diventano Invio, le tabulazioni Tab; gli altri caratteri di
    /// controllo si scartano. I caratteri si traducono poi nei tasti del layout di tastiera in uso.
    /// </summary>
    public static IReadOnlyList<TypedKey> Plan(string text)
    {
        var keys = new List<TypedKey>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    keys.Add(new TypedKey('\0', TypedKey.Return));
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n':
                    keys.Add(new TypedKey('\0', TypedKey.Return));
                    break;
                case '\t':
                    keys.Add(new TypedKey('\0', TypedKey.Tab));
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        keys.Add(new TypedKey(c, 0));
                    }

                    break;
            }
        }

        return keys;
    }
}

/// <summary>Livelli di zoom della sessione RDP (scala del desktop remoto, in percentuale).</summary>
public static class DisplayScale
{
    public static IReadOnlyList<uint> Levels { get; } = [100, 125, 150, 175, 200, 250, 300];

    /// <summary>Livello successivo (direction &gt; 0) o precedente; null se si è già al limite.</summary>
    public static uint? Step(uint current, int direction) =>
        direction > 0
            ? Levels.Where(l => l > current).Select(l => (uint?)l).FirstOrDefault()
            : Levels.Where(l => l < current).Select(l => (uint?)l).LastOrDefault();

    /// <summary>Scala del dispositivo che Windows accetta insieme a quella del desktop (100, 140 o 180).</summary>
    public static uint DeviceScale(uint desktopScale) => desktopScale >= 180 ? 180u : desktopScale >= 140 ? 140u : 100u;
}

/// <summary>Nomi di file proposti per screenshot e registri.</summary>
public static class FileNames
{
    public static string For(string name, string extension, DateTime now)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length == 0)
        {
            safe = "sessione";
        }
        else if (safe.Length > 80)
        {
            safe = safe[..80];
        }

        return $"{safe} {now:yyyy-MM-dd HHmmss}.{extension}";
    }
}
