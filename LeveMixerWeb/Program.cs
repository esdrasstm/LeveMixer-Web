using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LeveMixer;

static class Program
{
    /// <summary>LeveMixer.exe --dev : habilita F12 (DevTools) e não fecha ao perder o foco.</summary>
    internal static bool DevMode { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        DevMode = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Uma única instância por vez
        using var mutex = new Mutex(true, "LeveMixer_SingleInstance", out bool created);
        if (!created) return;

        // Primeira execução: já deixa para abrir com o Windows (dá para desligar nas configurações)
        var settings = AppSettings.Load();
        if (!settings.FirstRunDone)
        {
            AutoStart.Set(true);
            settings.FirstRunDone = true;
            settings.Save();
        }
        AutoStart.RefreshPath();

        using var tray = new TrayController();
        MemoryTrim.Run();
        Application.Run();
    }
}
