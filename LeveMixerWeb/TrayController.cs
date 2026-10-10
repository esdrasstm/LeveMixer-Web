using System;
using System.Windows.Forms;

namespace Volum;

/// <summary>Ícone da bandeja + abre/fecha o mixer.</summary>
sealed class TrayController : IDisposable
{
    readonly NotifyIcon _icon;
    MixerForm? _form;
    bool _syncingMenu;

    public TrayController()
    {
        var menu = new ContextMenuStrip();

        var autoStart = new ToolStripMenuItem
        {
            Checked = AutoStart.IsEnabled,
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (!_syncingMenu) AutoStart.Set(autoStart.Checked);
        };
        var exit = new ToolStripMenuItem();
        exit.Click += (_, _) =>
        {
            _icon!.Visible = false;
            _form?.Dispose();
            Application.Exit();
        };

        // O idioma e o auto-início podem mudar pelo mixer: o menu se atualiza toda vez que abre
        void SyncMenu()
        {
            var lang = AppSettings.Load().Language;
            autoStart.Text = Strings.Get(lang, "autostart");
            exit.Text = Strings.Get(lang, "exit");
            _syncingMenu = true;
            autoStart.Checked = AutoStart.IsEnabled;
            _syncingMenu = false;
        }
        SyncMenu();
        menu.Opening += (_, _) => SyncMenu();

        // Versão no topo do menu (ajuda quem está testando a dizer qual versão tem)
        menu.Items.Add(new ToolStripMenuItem($"Volum {Updater.Version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = IconFactory.Create(),
            Text = "Volum",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        // Atualização automática: só reinicia para atualizar quando o mixer não está aberto
        Updater.Start(
            canRestart: () => _form == null || _form.IsDisposed || !_form.IsOpen,
            beforeRestart: () => { _icon.Visible = false; _form?.Dispose(); });

        // --dev: abre o mixer assim que o app inicia (agiliza os testes do visual)
        if (Program.DevMode)
        {
            var t = new Timer { Interval = 300 };
            t.Tick += (_, _) => { t.Dispose(); Toggle(); };
            t.Start();
        }
        else
        {
            // Pré-aquece o WebView2 alguns segundos depois de abrir (sem atrasar o início do Windows):
            // assim até o primeiro clique abre na hora. No modo leve não faz nada.
            var warm = new Timer { Interval = 4000 };
            warm.Tick += (_, _) =>
            {
                warm.Dispose();
                if (_form == null || _form.IsDisposed) EnsureForm().Prewarm();
            };
            warm.Start();
        }
    }

    MixerForm EnsureForm()
    {
        if (_form != null && !_form.IsDisposed) return _form;
        _form = new MixerForm();
        _form.Disposed += (_, _) =>
        {
            _form = null;
            MemoryTrim.Run();
        };
        return _form;
    }

    void Toggle()
    {
        DevLog.Write($"clique na bandeja (form={(_form == null || _form.IsDisposed ? "nenhum" : _form.IsOpen ? "aberto" : "escondido")})");
        if (_form != null && !_form.IsDisposed)
        {
            if (_form.IsOpen)
            {
                if (_form.CanCloseByClick) _ = _form.CloseAnimatedAsync();
                return;
            }

            // Clicar no ícone tira o foco do mixer (ele já começa a fechar sozinho);
            // sem esse intervalo ele fecharia e reabriria na hora.
            if ((DateTime.UtcNow - _form.LastHiddenUtc).TotalMilliseconds < 300) return;
        }
        EnsureForm().Open();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
