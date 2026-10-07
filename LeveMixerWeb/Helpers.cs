using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LeveMixer;

static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "LeveMixer";

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
        var current = k?.GetValue(AppName) as string;
        if (k == null || current == null || !CanRegister) return;
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
