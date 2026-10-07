using System;
using System.Windows.Forms;

namespace LeveMixer;

/// <summary>Ícone da bandeja + abre/fecha o mixer.</summary>
sealed class TrayController : IDisposable
{
    readonly NotifyIcon _icon;
    MixerForm? _form;
    bool _syncingMenu;

    public TrayController()
    {
        var menu = new ContextMenuStrip();

        var autoStart = new ToolStripMenuItem("Iniciar com o Windows")
        {
            Checked = AutoStart.IsEnabled,
            CheckOnClick = true
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (!_syncingMenu) AutoStart.Set(autoStart.Checked);
        };
        // O botão nas configurações do mixer também mexe nisso: mantém o menu sincronizado
        menu.Opening += (_, _) =>
        {
            _syncingMenu = true;
            autoStart.Checked = AutoStart.IsEnabled;
            _syncingMenu = false;
        };

        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) =>
        {
            _icon!.Visible = false;
            _form?.Dispose();
            Application.Exit();
        });

        _icon = new NotifyIcon
        {
            Icon = IconFactory.Create(),
            Text = "Mixer de volume",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };
    }

    void Toggle()
    {
        if (_form != null && !_form.IsDisposed)
        {
            if (_form.IsOpen) { _ = _form.CloseAnimatedAsync(); return; }

            // Clicar no ícone tira o foco do mixer (ele já começa a fechar sozinho);
            // sem esse intervalo ele fecharia e reabriria na hora.
            if ((DateTime.UtcNow - _form.LastHiddenUtc).TotalMilliseconds < 300) return;
        }
        else
        {
            _form = new MixerForm();
            _form.Disposed += (_, _) =>
            {
                _form = null;
                MemoryTrim.Run();
            };
        }
        _form.Open();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
