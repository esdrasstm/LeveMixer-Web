using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Velopack;

namespace Volum;

static class Program
{
    /// <summary>Volum.exe --dev : habilita F12 (DevTools), abre o mixer ao iniciar e não fecha ao perder o foco.</summary>
    internal static bool DevMode { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // Velopack (instalador/atualizador): precisa ser a primeira coisa do app. Durante a
        // instalação, atualização e desinstalação ele roda o Volum rapidinho e sai daqui mesmo.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AutoStart.Set(false))   // desinstalou: tira do "iniciar com o Windows"
            .Run();

        DevMode = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Uma única instância por vez
        using var mutex = new Mutex(true, "Volum_SingleInstance", out bool created);
        if (!created) return;

        // Só a versão instalada mexe sozinha no "iniciar com o Windows": a cópia de desenvolvimento
        // (pasta bin) não pode trocar o caminho para ela mesma.
        if (Updater.IsInstalled)
        {
            // Primeira execução: já deixa para abrir com o Windows (dá para desligar nas configurações)
            var settings = AppSettings.Load();
            if (!settings.FirstRunDone)
            {
                AutoStart.Set(true);
                settings.FirstRunDone = true;
                settings.Save();
            }
            AutoStart.RefreshPath();
        }

        using var tray = new TrayController();
        MemoryTrim.Run();
        Application.Run();
    }
}
