namespace Launcher.Connector.Core;

/// <summary>
/// Negoziazione Telnet minima: separa i dati dai comandi IAC e prepara le risposte.
/// Accetta ECHO e SUPPRESS-GO-AHEAD dal server, rifiuta tutto il resto.
/// </summary>
public sealed class TelnetFilter
{
    public const byte Iac = 255;
    public const byte Dont = 254;
    public const byte Do = 253;
    public const byte Wont = 252;
    public const byte Will = 251;
    public const byte Sb = 250;
    public const byte Se = 240;
    public const byte OptionEcho = 1;
    public const byte OptionSuppressGoAhead = 3;

    private enum State
    {
        Data,
        Iac,
        Option,
        Subnegotiation,
        SubnegotiationIac,
    }

    private State _state = State.Data;
    private byte _verb;

    /// <summary>Elabora i byte ricevuti: restituisce i dati da mostrare e le risposte da inviare.</summary>
    public (byte[] Data, byte[] Reply) Process(ReadOnlySpan<byte> input)
    {
        var data = new List<byte>(input.Length);
        var reply = new List<byte>();
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                    {
                        _state = State.Iac;
                    }
                    else
                    {
                        data.Add(b);
                    }

                    break;

                case State.Iac:
                    if (b == Iac)
                    {
                        data.Add(Iac);
                        _state = State.Data;
                    }
                    else if (b is Do or Dont or Will or Wont)
                    {
                        _verb = b;
                        _state = State.Option;
                    }
                    else if (b == Sb)
                    {
                        _state = State.Subnegotiation;
                    }
                    else
                    {
                        _state = State.Data;
                    }

                    break;

                case State.Option:
                    if (_verb == Will)
                    {
                        reply.AddRange([Iac, b is OptionEcho or OptionSuppressGoAhead ? Do : Dont, b]);
                    }
                    else if (_verb == Do)
                    {
                        reply.AddRange([Iac, b == OptionSuppressGoAhead ? Will : Wont, b]);
                    }

                    _state = State.Data;
                    break;

                case State.Subnegotiation:
                    if (b == Iac)
                    {
                        _state = State.SubnegotiationIac;
                    }

                    break;

                case State.SubnegotiationIac:
                    _state = b == Se ? State.Data : State.Subnegotiation;
                    break;
            }
        }

        return (data.ToArray(), reply.ToArray());
    }

    /// <summary>Raddoppia i byte 255 nei dati inviati, come richiesto dal protocollo.</summary>
    public static byte[] Escape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        foreach (var b in data)
        {
            output.Add(b);
            if (b == Iac)
            {
                output.Add(Iac);
            }
        }

        return output.ToArray();
    }
}
