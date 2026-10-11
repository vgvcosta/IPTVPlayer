using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using IPTVPlayer.Services;

namespace IPTVPlayer.Views;

/// <summary>
/// Comando Xbox / XInput → teclas da app (sem configurar nada):
/// cruzeta/stick = setas · A = Enter · B = Esc (voltar) · X = pausa · Y = ecrã inteiro ·
/// LB/RB = canal anterior/seguinte · LT/RT = recuar/avançar 60 s · View = Guia TV · Menu = pesquisar.
/// Só envia teclas quando uma janela da app está ativa (nunca para outros programas).
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    private const ushort Start = 0x0010, Back = 0x0020, Lb = 0x0100, Rb = 0x0200;
    private const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    private const short StickThreshold = 16000;
    private const byte TriggerThreshold = 128;

    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(110);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _pad = -1;
    private DateTime _nextScanUtc;
    private uint _previous;
    private uint _heldDirection;
    private DateTime _nextRepeatUtc;
    private bool _available = true;

    public GamepadService()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value && _available) _timer.Start();
            else _timer.Stop();
        }
    }

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        try
        {
            if (!TryRead(out var state)) return;

            // Só com a app em primeiro plano.
            if (!Application.Current.Windows.OfType<Window>().Any(w => w.IsActive))
            {
                _previous = state;
                return;
            }

            var pressed = state & ~_previous;
            _previous = state;

            // Botões (uma vez por toque)
            if ((pressed & A) != 0) Send(0x0D);               // Enter
            if ((pressed & B) != 0) Send(0x1B);               // Esc
            if ((pressed & X) != 0) Send(0x20);               // Espaço
            if ((pressed & Y) != 0) Send(0x46);               // F
            if ((pressed & Lb) != 0) Send(0x21);              // PgUp
            if ((pressed & Rb) != 0) Send(0x22);              // PgDn
            if ((pressed & Back) != 0) Send(0x47);            // G
            if ((pressed & Start) != 0) Send(0x46, ctrl: true); // Ctrl+F
            if ((pressed & LtBit) != 0) Send(0x25, ctrl: true); // Ctrl+← (recuar 60 s)
            if ((pressed & RtBit) != 0) Send(0x27, ctrl: true); // Ctrl+→ (avançar 60 s)

            // Direções (com repetição enquanto estiver carregado)
            var direction = state & (DpadUp | DpadDown | DpadLeft | DpadRight);
            var now = DateTime.UtcNow;
            if (direction == 0)
            {
                _heldDirection = 0;
            }
            else if (direction != _heldDirection)
            {
                _heldDirection = direction;
                _nextRepeatUtc = now + RepeatDelay;
                SendDirection(direction);
            }
            else if (now >= _nextRepeatUtc)
            {
                _nextRepeatUtc = now + RepeatRate;
                SendDirection(direction);
            }
        }
        catch (DllNotFoundException)
        {
            // Windows sem XInput: desligar sem incomodar.
            _available = false;
            _timer.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao ler o comando.", ex);
            _timer.Stop();
        }
    }

    private const uint LtBit = 0x10000, RtBit = 0x20000;

    /// <summary>Estado do primeiro comando ligado (botões + gatilhos + stick como cruzeta).</summary>
    private bool TryRead(out uint buttons)
    {
        buttons = 0;
        var now = DateTime.UtcNow;
        if (_pad < 0)
        {
            // Procurar comandos é lento: no máximo a cada 2 s.
            if (now < _nextScanUtc) return false;
            _nextScanUtc = now.AddSeconds(2);
            for (var i = 0; i < 4 && _pad < 0; i++)
            {
                if (XInputGetState(i, out _) == 0) _pad = i;
            }
            if (_pad < 0) return false;
        }

        if (XInputGetState(_pad, out var s) != 0)
        {
            _pad = -1;
            return false;
        }

        var g = s.Gamepad;
        buttons = g.Buttons;
        if (g.LeftTrigger > TriggerThreshold) buttons |= LtBit;
        if (g.RightTrigger > TriggerThreshold) buttons |= RtBit;
        if (g.ThumbLY > StickThreshold) buttons |= DpadUp;
        if (g.ThumbLY < -StickThreshold) buttons |= DpadDown;
        if (g.ThumbLX < -StickThreshold) buttons |= DpadLeft;
        if (g.ThumbLX > StickThreshold) buttons |= DpadRight;
        return true;
    }

    private static void SendDirection(uint direction)
    {
        if ((direction & DpadUp) != 0) Send(0x26);
        else if ((direction & DpadDown) != 0) Send(0x28);
        else if ((direction & DpadLeft) != 0) Send(0x25);
        else if ((direction & DpadRight) != 0) Send(0x27);
    }

    private static void Send(byte vk, bool ctrl = false)
    {
        const uint keyUp = 0x0002;
        const uint extended = 0x0001;
        // Setas e PgUp/PgDn são teclas "estendidas".
        var flags = vk is >= 0x21 and <= 0x28 ? extended : 0u;
        if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, flags, UIntPtr.Zero);
        keybd_event(vk, 0, flags | keyUp, UIntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, keyUp, UIntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int userIndex, out XInputState state);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
