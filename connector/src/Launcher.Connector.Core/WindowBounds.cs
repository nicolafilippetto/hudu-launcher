using System.Globalization;

namespace Launcher.Connector.Core;

/// <summary>Area di lavoro di uno schermo, nelle stesse unità della finestra.</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height);

/// <summary>
/// Posizione e dimensione della finestra del Launcher (stato normale) e se era ingrandita: si salva
/// quando la finestra si nasconde e si riapplica al riavvio, se è ancora visibile su uno schermo.
/// </summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>Parte minima della barra del titolo che deve cadere su uno schermo per riusare la posizione.</summary>
    private const double VisibleWidth = 120;
    private const double VisibleHeight = 30;

    public string Serialize() => string.Join(';',
        Left.ToString("0", CultureInfo.InvariantCulture), Top.ToString("0", CultureInfo.InvariantCulture),
        Width.ToString("0", CultureInfo.InvariantCulture), Height.ToString("0", CultureInfo.InvariantCulture),
        Maximized ? "max" : "normal");

    public static WindowBounds? Parse(string? value)
    {
        var parts = (value ?? "").Split(';');
        if (parts.Length != 5)
        {
            return null;
        }

        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        if (numbers[2] < 100 || numbers[3] < 100 || parts[4] is not ("max" or "normal"))
        {
            return null;
        }

        return new WindowBounds(numbers[0], numbers[1], numbers[2], numbers[3], parts[4] == "max");
    }

    /// <summary>
    /// Posizione da usare con gli schermi di adesso: null se la barra del titolo non cadrebbe su nessuno
    /// (es. monitor esterno staccato), altrimenti la stessa, ridotta se è più grande dello schermo.
    /// </summary>
    public WindowBounds? FitTo(IReadOnlyCollection<ScreenArea> screens)
    {
        foreach (var screen in screens)
        {
            var overlapWidth = Math.Min(Left + Width, screen.Left + screen.Width) - Math.Max(Left, screen.Left);
            var overlapHeight = Math.Min(Top + VisibleHeight, screen.Top + screen.Height) - Math.Max(Top, screen.Top);
            if (overlapWidth >= Math.Min(VisibleWidth, Width) && overlapHeight >= VisibleHeight && Top >= screen.Top - 1)
            {
                return this with { Width = Math.Min(Width, screen.Width), Height = Math.Min(Height, screen.Height) };
            }
        }

        return null;
    }
}
