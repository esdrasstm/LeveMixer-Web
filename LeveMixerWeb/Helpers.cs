using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Volum;

static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "Volum";
    const string OldAppName = "LeveMixer";   // nome antigo do app

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) != null;
        }
    }

    // Rodando via "dotnet run" o ProcessPath é o dotnet.exe: não faz sentido registrar isso.
    public static bool CanRegister =>
        !string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                       "dotnet", StringComparison.OrdinalIgnoreCase);

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on)
        {
            if (CanRegister) k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        }
        else k.DeleteValue(AppName, false);
    }

    // Se o usuário moveu o .exe de pasta, atualiza o caminho registrado (só se estiver ativado).
    public static void RefreshPath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null || !CanRegister) return;

        // Estava registrado com o nome antigo (LeveMixer): troca pelo novo
        if (k.GetValue(OldAppName) != null)
        {
            k.DeleteValue(OldAppName, false);
            k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            return;
        }

        var current = k.GetValue(AppName) as string;
        if (current == null) return;
        var wanted = $"\"{Environment.ProcessPath}\"";
        if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            k.SetValue(AppName, wanted);
    }
}

/// <summary>Força o .NET a devolver memória ao Windows (mantém o app leve na bandeja).</summary>
static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    public static void Run()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
    }
}

/// <summary>Só no modo --dev: anota tempos em %TEMP%\Volum-dev.log (para medir a abertura do mixer).</summary>
static class DevLog
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly string FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Volum-dev.log");

    public static void Write(string message)
    {
        if (!Program.DevMode) return;
        try { System.IO.File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} +{Clock.ElapsedMilliseconds,6} ms  {message}\r\n"); }
        catch { }
    }
}

/// <summary>
/// "Tick" bem baixinho ao mexer no volume. O som é gerado por código (sem arquivo .wav)
/// e só é criado no primeiro uso. Para mudar: Volume (0 a 1), FreqHz e DecayMs.
/// </summary>
static class TickSound
{
    const double Volume = 0.06;    // bem sutil (1.0 = volume máximo do arquivo)
    const double FreqHz = 1500;
    const double DecayMs = 7;      // quanto tempo o "tic" demora para sumir

    static System.Media.SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            _player ??= Create();
            _player.Play();        // assíncrono; um tick novo interrompe o anterior
        }
        catch { /* sem dispositivo de áudio: fica em silêncio */ }
    }

    static System.Media.SoundPlayer Create()
    {
        const int rate = 44100;
        int samples = rate * 45 / 1000;                 // 45 ms
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);

        // Cabeçalho WAV (PCM 16 bits, mono)
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate;
            double attack = Math.Min(1, t / 0.001);     // 1 ms de subida (evita estalo)
            double env = attack * Math.Exp(-t * 1000 / DecayMs);
            double s = Math.Sin(2 * Math.PI * FreqHz * t) + 0.3 * Math.Sin(2 * Math.PI * FreqHz * 2 * t);
            w.Write((short)(s / 1.3 * env * Volume * short.MaxValue));
        }

        w.Flush();
        ms.Position = 0;
        var player = new System.Media.SoundPlayer(ms);
        player.Load();
        return player;
    }
}

/// <summary>Gera o ícone da bandeja por código (sem precisar de arquivo .ico).</summary>
static class IconFactory
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public static System.Drawing.Icon Create()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 124, 92, 255));
            g.FillEllipse(bg, 0, 0, 31, 31);

            using var line = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            using var knob = new System.Drawing.SolidBrush(System.Drawing.Color.White);

            int[] xs = { 9, 16, 23 };
            int[] ys = { 20, 11, 17 };
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(line, xs[i], 7, xs[i], 25);
                g.FillEllipse(knob, xs[i] - 3.5f, ys[i] - 3.5f, 7, 7);
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
