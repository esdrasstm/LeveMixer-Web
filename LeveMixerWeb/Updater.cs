using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Velopack;
using Velopack.Sources;
using Timer = System.Windows.Forms.Timer;

namespace Volum;

/// <summary>
/// Atualização automática (Velopack + GitHub Releases).
/// Procura versão nova 1 minuto depois de abrir e depois a cada 6 horas; baixa em segundo plano
/// e aplica (o Volum reinicia sozinho em ~2 s) quando o mixer não está aberto.
/// Só funciona na versão instalada; na pasta bin / modo --dev não faz nada.
/// </summary>
static class Updater
{
    const string RepoUrl = "https://github.com/esdrasstm/Volum";

    static readonly UpdateManager Manager = new(new GithubSource(RepoUrl, null, false));
    static UpdateInfo? _ready;     // já baixada, esperando o mixer fechar
    static bool _busy;

    public static bool IsInstalled
    {
        get { try { return Manager.IsInstalled; } catch { return false; } }
    }

    /// <summary>Versão atual (instalada) ou a do projeto, na cópia de desenvolvimento.</summary>
    public static string Version =>
        (IsInstalled ? Manager.CurrentVersion?.ToString() : null) ?? Application.ProductVersion.Split('+')[0];

    /// <param name="canRestart">Diz se dá para reiniciar agora (o mixer não está aberto).</param>
    /// <param name="beforeRestart">Esconde o ícone da bandeja antes de o app fechar.</param>
    public static void Start(Func<bool> canRestart, Action beforeRestart)
    {
        if (!IsInstalled || Program.DevMode) return;

        var timer = new Timer { Interval = 60_000 };          // primeira busca: 1 min depois de abrir
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_ready == null) await CheckAsync();
            if (_ready != null && canRestart())
            {
                beforeRestart();
                Manager.ApplyUpdatesAndRestart(_ready);   // fecha, atualiza e abre de novo
                return;
            }
            // Baixada mas o mixer está aberto: tenta de novo em 1 min. Sem novidade: volta em 6 h.
            timer.Interval = _ready != null ? 60_000 : 6 * 60 * 60_000;
            timer.Start();
        };
        timer.Start();
    }

    static async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var info = await Manager.CheckForUpdatesAsync();
            if (info == null) return;                          // já está na última versão
            await Manager.DownloadUpdatesAsync(info);
            _ready = info;
        }
        catch { /* sem internet ou GitHub fora do ar: tenta de novo na próxima */ }
        finally { _busy = false; }
    }
}
