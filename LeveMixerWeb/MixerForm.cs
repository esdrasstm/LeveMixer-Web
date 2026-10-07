using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Timer = System.Windows.Forms.Timer;

namespace LeveMixer;

/// <summary>
/// Janela sem borda que hospeda a interface (HTML/CSS/JS) via WebView2.
/// C# ⇄ JS conversam por mensagens JSON (PostWebMessageAsJson / chrome.webview.postMessage).
/// </summary>
sealed class MixerForm : Form
{
    // Largura da janela em "pixels CSS". Se mudar aqui, mude também em style.css (.card { width })
    const int WidthDip = 360;
    const int MarginDip = 12;      // distância da borda da área de trabalho
    const int SlideDip = 14;       // quanto a janela "sobe" ao abrir
    const int OpenMs = 260;
    const int CloseMs = 130;       // acompanha --exit-ms do CSS (120ms)

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly WebView2 _web = new();
    readonly HashSet<string> _iconsSent = new();
    readonly Timer _sync = new() { Interval = 1000 };
    readonly Timer _meter = new() { Interval = 60 };
    readonly Timer _idle = new() { Interval = 45_000 };   // depois de fechado, descarta o WebView para liberar RAM
    readonly AppSettings _settings = AppSettings.Load();
    AudioService? _audio;

    bool _initStarted, _ready, _wantOpen, _pendingPresent, _shown, _closing;
    int _heightDip = 200;
    int _slideOffsetPx;
    Point _anchor;                 // canto inferior direito (em pixels) onde a janela assenta
    DateTime _presentedAt;

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;
    /// <summary>Visível e não está fechando.</summary>
    public bool IsOpen => _shown && !_closing;

    public MixerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Text = "LeveMixer";
        BackColor = Color.FromArgb(30, 30, 36);
        Size = new Size((int)Math.Round(WidthDip * DeviceDpi / 96f), 300);

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(30, 30, 36);
        Controls.Add(_web);

        _sync.Tick += (_, _) => PushState();
        _meter.Tick += (_, _) => PushPeaks();
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!_shown) Dispose();
        };

        Deactivate += (_, _) =>
        {
            if (Program.DevMode || !_shown || _closing) return;
            if ((DateTime.UtcNow - _presentedAt).TotalMilliseconds < 300) return;
            _ = CloseAnimatedAsync();
        };
    }

    // ---------- janela: sem entrada no Alt+Tab, cantos arredondados e sombra do Windows ----------

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int round = 2; // DWMWCP_ROUND (Windows 11; no Windows 10 é ignorado)
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 }; // habilita a sombra
            DwmExtendFrameIntoClientArea(Handle, ref m);
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    // ---------- abrir / fechar ----------

    public async void Open()
    {
        if (IsDisposed) return;
        _idle.Stop();
        _wantOpen = true;

        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        else if (_ready && !_shown)
        {
            BeginOpen();
        }
    }

    async Task InitWebViewAsync()
    {
        try
        {
            _ = Handle; // cria a janela (ainda invisível) para o WebView2 poder inicializar

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LeveMixer", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            var st = core.Settings;
            st.AreDefaultContextMenusEnabled = Program.DevMode;
            st.AreDevToolsEnabled = Program.DevMode;
            st.AreBrowserAcceleratorKeysEnabled = Program.DevMode;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;

            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://app.leve/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.WebMessageReceived += OnWebMessage;

            // https://app.leve/  ->  pasta "wwwroot" ao lado do .exe
            core.SetVirtualHostNameToFolderMapping(
                "app.leve",
                Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Allow);

            // Sem cache: editou o CSS/JS, abriu de novo, já vale
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");

            core.Navigate("https://app.leve/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Não foi possível iniciar a interface (WebView2).\n\n" +
                "Verifique se o \"WebView2 Runtime\" está instalado (vem no Windows 11).\n\n" + ex.Message,
                "LeveMixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Dispose();
        }
    }

    void BeginOpen()
    {
        _wantOpen = false;
        _closing = false;

        _audio?.Dispose();
        _audio = new AudioService(_iconsSent);

        // A interface renderiza e responde com "resize"; só então a janela aparece.
        _pendingPresent = true;
        PostSettings();
        PushState();
        _sync.Start();
        _meter.Start();

        _ = FallbackPresentAsync();
    }

    async Task FallbackPresentAsync()
    {
        await Task.Delay(1500);
        if (_pendingPresent && !IsDisposed)
        {
            _pendingPresent = false;
            await PresentAsync();
        }
    }

    async Task PresentAsync()
    {
        if (IsDisposed) return;
        float k = DeviceDpi / 96f;

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        _anchor = new Point(wa.Right - (int)(MarginDip * k), wa.Bottom - (int)(MarginDip * k));

        _shown = true;
        _slideOffsetPx = (int)(SlideDip * k);
        _presentedAt = DateTime.UtcNow;
        Reposition();
        Show();
        Activate();
        Post(new { type = "open" });   // a interface toca a animação de entrada (CSS)

        int from = _slideOffsetPx;
        await Tween(OpenMs, EaseOutQuint, t => { _slideOffsetPx = (int)Math.Round(from * (1 - t)); Reposition(); });
        _slideOffsetPx = 0;
        Reposition();
    }

    public async Task CloseAnimatedAsync()
    {
        if (!_shown || _closing || IsDisposed) return;
        _closing = true;
        _sync.Stop();
        _meter.Stop();
        Post(new { type = "close" });  // a interface toca a animação de saída (CSS)

        int to = (int)Math.Round(6 * DeviceDpi / 96f);
        await Tween(CloseMs, EaseInCubic, t => { _slideOffsetPx = (int)Math.Round(to * t); Reposition(); });
        HideNow();
    }

    void HideNow()
    {
        if (IsDisposed) return;
        _audio?.Dispose();
        _audio = null;
        Hide();
        _shown = false;
        _closing = false;
        _slideOffsetPx = 0;
        LastHiddenUtc = DateTime.UtcNow;
        _idle.Stop();
        _idle.Start();
        BeginInvoke(new Action(MemoryTrim.Run));
    }

    // ---------- tamanho / posição ----------

    void OnResize(double heightDip)
    {
        _heightDip = Math.Max(60, (int)Math.Ceiling(heightDip));
        Reposition();
        if (_pendingPresent)
        {
            _pendingPresent = false;
            _ = PresentAsync();
        }
    }

    // A janela cresce/encolhe sempre ancorada no canto inferior direito
    void Reposition()
    {
        if (IsDisposed) return;
        float k = DeviceDpi / 96f;
        int w = (int)Math.Round(WidthDip * k);
        int h = (int)Math.Round(_heightDip * k);
        SetBounds(_anchor.X - w, _anchor.Y - h + _slideOffsetPx, w, h);
    }

    static async Task Tween(int ms, Func<double, double> ease, Action<double> step)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)ms);
            step(ease(t));
            if (t >= 1.0) break;
            await Task.Delay(10);
        }
    }

    static double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);
    static double EaseInCubic(double t) => t * t * t;

    // ---------- conversa com a interface ----------

    void Post(object message)
    {
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    void PostSettings() => Post(new
    {
        type = "settings",
        theme = _settings.Theme.ToString().ToLowerInvariant(),
        autostart = AutoStart.IsEnabled
    });

    void PushState()
    {
        if (_audio == null) return;
        try
        {
            Post(_audio.Snapshot());
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            // Sem dispositivo de áudio, por exemplo: manda estado vazio para a janela abrir mesmo assim
            Post(new StateDto("state", new MasterDto(0, false, 0), new List<AppDto>()));
        }
    }

    void PushPeaks()
    {
        try
        {
            if (_audio != null) Post(_audio.Peaks());
        }
        catch { /* sessão pode sumir entre um tick e outro */ }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    // Página (re)carregada, por exemplo com F5 no modo --dev: a interface perdeu
                    // os ícones e o estado, então tudo precisa ser enviado de novo.
                    _iconsSent.Clear();
                    if (_wantOpen) BeginOpen();
                    else if (_shown)
                    {
                        PostSettings();
                        PushState();
                        Post(new { type = "open" });
                    }
                    break;

                case "resize":
                    OnResize(root.GetProperty("height").GetDouble());
                    break;

                case "setVolume":
                    _audio?.SetVolume(
                        root.GetProperty("id").GetString()!,
                        (float)root.GetProperty("value").GetDouble());
                    break;

                case "setMute":
                    _audio?.SetMute(
                        root.GetProperty("id").GetString()!,
                        root.GetProperty("muted").GetBoolean());
                    break;

                case "setTheme":
                    if (Enum.TryParse<ThemeMode>(root.GetProperty("mode").GetString(), true, out var mode))
                    {
                        _settings.Theme = mode;
                        _settings.Save();
                    }
                    break;

                case "setAutostart":
                    AutoStart.Set(root.GetProperty("enabled").GetBoolean());
                    break;

                case "hide":   // botão minimizar: fecha com animação (também no modo --dev)
                    _ = CloseAnimatedAsync();
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sync.Dispose();
            _meter.Dispose();
            _idle.Dispose();
            _audio?.Dispose();
            _audio = null;
        }
        base.Dispose(disposing);
    }
}
