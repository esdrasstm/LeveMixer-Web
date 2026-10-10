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

namespace Volum;

/// <summary>
/// Janela sem borda que hospeda a interface (HTML/CSS/JS) via WebView2.
/// C# ⇄ JS conversam por mensagens JSON (PostWebMessageAsJson / chrome.webview.postMessage).
/// </summary>
sealed class MixerForm : Form
{
    // Largura da janela em "pixels CSS". Se mudar aqui, mude também em style.css (.card { width })
    const int WidthDip = 360;
    const int MarginDip = 12;      // distância da borda da área de trabalho
    const int CloseMs = 130;       // acompanha --exit-ms do CSS (120ms)
    const string SiteUrl = "https://esdrasstm.github.io/Volum/";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly WebView2 _web = new();
    readonly HashSet<string> _iconsSent = new();
    readonly Timer _sync = new() { Interval = 1000 };
    readonly Timer _meter = new() { Interval = 60 };
    readonly Timer _idle = new() { Interval = 45_000 };   // depois de fechado, descarta o WebView para liberar RAM
    readonly AppSettings _settings = AppSettings.Load();
    AudioService? _audio;

    bool _initStarted, _ready, _wantOpen, _pendingPresent, _shown, _closing, _contentShown;
    // Última altura conhecida: a janela já aparece no tamanho certo, mesmo depois de ser descartada
    static int s_heightDip = 420;
    int _slideOffsetPx;
    Point _anchor;                 // canto inferior direito (em pixels) onde a janela assenta
    DateTime _presentedAt, _contentAt;

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;
    /// <summary>Visível e não está fechando.</summary>
    public bool IsOpen => _shown && !_closing;

    /// <summary>
    /// Um clique na bandeja só fecha depois que o conteúdo apareceu (e não logo em seguida):
    /// assim um segundo clique de impaciência durante o carregamento não fecha a janela.
    /// </summary>
    public bool CanCloseByClick => _contentShown && (DateTime.UtcNow - _contentAt).TotalMilliseconds > 400;

    public MixerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Text = "Volum";
        Size = new Size((int)Math.Round(WidthDip * DeviceDpi / 96f), 300);
        _web.Dock = DockStyle.Fill;
        ApplyBackdrop();
        Controls.Add(_web);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (_settings.LightMode) _meter.Interval = MeterLiteMs;

        _sync.Tick += (_, _) => PushState();
        _meter.Tick += (_, _) => PushPeaks();
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (_shown) return;
            if (KeepWarm) _ = SleepAsync();   // fica "dormindo": abre na hora da próxima vez
            else Dispose();                  // modo leve: descarta tudo e libera a memória
        };

        Deactivate += (_, _) =>
        {
            if (Program.DevMode || !_shown || _closing) return;
            if ((DateTime.UtcNow - _presentedAt).TotalMilliseconds < 300) return;
            DevLog.Write("perdeu o foco: fechando"); _ = CloseAnimatedAsync();
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
        }
        catch { }
        ApplyBackdrop();
    }

    // Acrílico nativo só existe a partir do Windows 11 22H2 (build 22621); antes disso, visual sólido.
    static readonly bool GlassSupported = Environment.OSVersion.Version.Build >= 22621;
    bool UseGlass => GlassSupported && !_settings.LightMode;

    const int MeterMs = 60, MeterLiteMs = 150;   // medidores: normal / modo leve

    /// <summary>
    /// Vidro (acrílico do Windows 11 atrás da interface) ou fundo sólido (modo leve / Windows antigo).
    /// Pode ser chamado a qualquer momento: troca na hora quando a chave "Modo leve" muda.
    /// </summary>
    void ApplyBackdrop()
    {
        bool dark = IsDarkTheme();
        var solid = dark ? Color.FromArgb(27, 22, 19) : Color.FromArgb(248, 245, 243);

        // Preto = transparente para o DWM: aparece o acrílico. O WebView também fica transparente.
        BackColor = UseGlass ? Color.Black : solid;
        _web.DefaultBackgroundColor = UseGlass ? Color.Transparent : solid;

        if (!IsHandleCreated) return;
        try
        {
            int d = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref d, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE (tom do vidro)

            int n = UseGlass ? -1 : 1;                                        // -1: janela inteira de vidro; 1: só a sombra
            var m = new MARGINS { Left = n, Right = n, Top = n, Bottom = n };
            DwmExtendFrameIntoClientArea(Handle, ref m);

            if (GlassSupported)
            {
                int backdrop = UseGlass ? 3 : 1;                              // DWMSBT_TRANSIENTWINDOW (acrílico) / DWMSBT_NONE
                DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE
            }
        }
        catch { }
    }

    bool IsDarkTheme() =>
        _settings.Theme == ThemeMode.System ? WindowsIsDark() : _settings.Theme == ThemeMode.Dark;

    /// <summary>
    /// Tema "Sistema": o modo do Windows (o da barra de tarefas e do Iniciar, onde o mixer aparece).
    /// É a única fonte: o C# manda o resultado para a interface (settings.systemDark).
    /// </summary>
    static bool WindowsIsDark()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var v = k?.GetValue("SystemUsesLightTheme") ?? k?.GetValue("AppsUseLightTheme");
        return v is int i && i == 0;
    }

    // Trocou o tema do Windows com o mixer aberto: atualiza vidro e interface na hora
    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            ApplyBackdrop();
            Post(new { type = "systemTheme", dark = WindowsIsDark() });
        }));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    // ---------- abrir / fechar ----------

    // ---------- WebView "dormindo" ----------
    // Iniciar o WebView2 do zero leva ~1 s (era o que deixava a abertura lenta). Fora do modo leve,
    // ele não é descartado: fica suspenso (0% de CPU) e com a memória no mínimo, e acorda no clique.
    bool KeepWarm => !_settings.LightMode && !Program.DevMode;
    bool _asleep;

    async Task SleepAsync()
    {
        var core = _web.CoreWebView2;
        if (core == null || _shown || _asleep) return;
        try
        {
            _web.Visible = false;   // o WebView2 só aceita suspender quando está marcado como invisível
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            _asleep = await core.TrySuspendAsync();
            DevLog.Write($"WebView2 dormindo: {_asleep}");
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        BeginInvoke(new Action(MemoryTrim.Run));
    }

    void Wake()
    {
        var core = _web.CoreWebView2;
        if (core == null) return;
        try
        {
            if (_asleep) core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _web.Visible = true;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        _asleep = false;
    }

    /// <summary>Inicia o WebView2 escondido logo depois que o app abre, para o 1º clique já ser rápido.</summary>
    public void Prewarm()
    {
        if (IsDisposed || _initStarted || !KeepWarm) return;
        _initStarted = true;
        DevLog.Write("pré-aquecendo o WebView2");
        _ = InitWebViewAsync();
    }

    async Task SleepAfterPrewarmAsync()
    {
        await Task.Delay(1500);   // deixa a página terminar de pintar antes de suspender
        if (!_shown && !_wantOpen && !IsDisposed) await SleepAsync();
    }

    public async void Open()
    {
        if (IsDisposed) return;
        _idle.Stop();
        Wake();
        _wantOpen = true;

        // A janela (vidro vazio) aparece na hora do clique; o conteúdo surge quando estiver pronto.
        // A frio (WebView2 descartado), iniciar o WebView2 leva ~1 s.
        bool wasShown = _shown;
        if (!wasShown) ShowShell();

        if (!_initStarted)
        {
            _initStarted = true;
            await InitWebViewAsync();
        }
        else if (_ready && !wasShown)
        {
            BeginOpen();
        }
    }

    void ShowShell()
    {
        float k = DeviceDpi / 96f;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        _anchor = new Point(wa.Right - (int)(MarginDip * k), wa.Bottom - (int)(MarginDip * k));

        _shown = true;
        _closing = false;
        _contentShown = false;
        _slideOffsetPx = 0;
        _presentedAt = DateTime.UtcNow;
        Reposition();
        DevLog.Write("janela visível (vazia)");
        Show();
        Activate();
    }

    async Task InitWebViewAsync()
    {
        try
        {
            _ = Handle; // cria a janela (ainda invisível) para o WebView2 poder inicializar

            // Cache do WebView2. A cópia de teste (preview / --dev / pasta bin) usa outra pasta:
            // se usasse %LocalAppData%\Volum, o instalador acharia que o Volum já está instalado.
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Updater.IsInstalled ? "Volum" : "VolumDev", "WebView2");
            DevLog.Write("WebView2: criando ambiente");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            DevLog.Write("WebView2: ambiente pronto");
            await _web.EnsureCoreWebView2Async(env);
            DevLog.Write("WebView2: controle pronto");

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
                if (!e.Uri.StartsWith("https://app.volum/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.WebMessageReceived += OnWebMessage;

            // https://app.volum/  ->  pasta "wwwroot" ao lado do .exe
            // No --dev usa a wwwroot do projeto (a que você edita no VS Code) e recarrega ao salvar.
            var sourceRoot = Program.DevMode ? FindSourceWwwroot() : null;
            core.SetVirtualHostNameToFolderMapping(
                "app.volum",
                sourceRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Allow);
            if (sourceRoot != null) WatchForChanges(sourceRoot);

            // Sem cache só no --dev (editou o CSS/JS, recarregou, já vale). Na versão instalada
            // a interface não muda entre aberturas, e essas duas chamadas atrasavam a abertura.
            if (Program.DevMode)
            {
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            }

            DevLog.Write("carregando a interface");
            core.Navigate("https://app.volum/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Get(_settings.Language, "webviewError") + "\n\n" + ex.Message,
                "Volum", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Dispose();
        }
    }

    // ---------- --dev: recarregar a interface ao salvar no VS Code ----------

    FileSystemWatcher? _watcher;
    Timer? _reloadDebounce;

    // Sobe a partir de bin\Release\net8.0-windows\ até achar a pasta do projeto (onde está o Volum.csproj)
    static string? FindSourceWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var www = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(dir.FullName, "Volum.csproj")) && Directory.Exists(www)) return www;
        }
        return null;
    }

    void WatchForChanges(string folder)
    {
        // Salvar costuma gerar vários eventos seguidos: espera 200 ms de calma e recarrega uma vez
        _reloadDebounce = new Timer { Interval = 200 };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            DevLog.Write("arquivo salvo: recarregando a interface");
            _web.CoreWebView2?.Reload();
        };

        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true };
        FileSystemEventHandler changed = (_, _) =>
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
        };
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (s, e) => changed(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    void BeginOpen()
    {
        _wantOpen = false;
        _closing = false;

        _audio?.Dispose();
        DevLog.Write("abrindo: lendo o áudio");
        _audio = new AudioService(_iconsSent, listenMic: !_settings.LightMode);

        // A interface renderiza e responde com "resize"; só então a janela aparece.
        _pendingPresent = true;
        PostSettings();
        PushState();
        _sync.Start();
        _meter.Start();

        DevLog.Write("abrindo: estado enviado, esperando o tamanho");
        _ = FallbackPresentAsync();
    }

    async Task FallbackPresentAsync()
    {
        await Task.Delay(1500);
        if (_pendingPresent && !IsDisposed)
        {
            _pendingPresent = false;
            Present();
        }
    }

    // O conteúdo está pronto: a interface toca a animação de entrada (CSS) dentro da janela já visível.
    // A janela não desliza mais (mover a janela com o WebView a cada 10 ms engasgava a animação).
    void Present()
    {
        if (IsDisposed) return;
        if (!_shown) ShowShell();
        _contentShown = true;
        _contentAt = DateTime.UtcNow;
        DevLog.Write("conteúdo visível");
        Post(new { type = "open" });
    }

    public async Task CloseAnimatedAsync()
    {
        if (!_shown || _closing || IsDisposed) return;
        _closing = true;
        _wantOpen = false;            // fechou durante o carregamento: não reabre quando terminar
        _pendingPresent = false;
        _sync.Stop();
        _meter.Stop();
        Post(new { type = "close" });  // a interface toca a animação de saída (CSS)
        if (_settings.LightMode) { HideNow(); return; }

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
        _contentShown = false;
        _slideOffsetPx = 0;
        LastHiddenUtc = DateTime.UtcNow;
        _idle.Stop();
        _idle.Start();
        BeginInvoke(new Action(MemoryTrim.Run));
    }

    // ---------- tamanho / posição ----------

    void OnResize(double heightDip)
    {
        s_heightDip = Math.Max(60, (int)Math.Ceiling(heightDip));
        Reposition();
        if (_pendingPresent)
        {
            _pendingPresent = false;
            Present();
        }
    }

    // A janela cresce/encolhe sempre ancorada no canto inferior direito
    void Reposition()
    {
        if (IsDisposed) return;
        float k = DeviceDpi / 96f;
        int w = (int)Math.Round(WidthDip * k);
        int h = (int)Math.Round(s_heightDip * k);
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

    void PostSettings()
    {
        ApplyBackdrop();     // o tema do Windows pode ter mudado desde a última abertura
        Post(new
        {
            type = "settings",
            theme = _settings.Theme.ToString().ToLowerInvariant(),
            autostart = AutoStart.IsEnabled,
            glass = GlassSupported,
            lite = _settings.LightMode,
            language = Strings.Normalize(_settings.Language),
            systemDark = WindowsIsDark()
        });
    }

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
                    DevLog.Write("interface pronta (ready)");
                    _ready = true;
                    // Página (re)carregada, por exemplo com F5 no modo --dev: a interface perdeu
                    // os ícones e o estado, então tudo precisa ser enviado de novo.
                    _iconsSent.Clear();
                    if (_wantOpen) BeginOpen();
                    else if (_shown)
                    {
                        PostSettings();
                        PushState();
                        Present();
                    }
                    else _ = SleepAfterPrewarmAsync();   // pré-aquecido e ninguém abriu: vai dormir
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
                        ApplyBackdrop();
                    }
                    break;

                case "setMicDevice":   // escolheu outro microfone na lista: vira o padrão do Windows
                    try
                    {
                        _audio?.SetDefaultMic(root.GetProperty("id").GetString()!);
                    }
                    catch (Exception ex) { Debug.WriteLine(ex); }
                    PushState();
                    break;

                case "setLanguage":    // idioma nas configurações (o menu da bandeja lê na próxima vez que abrir)
                    _settings.Language = Strings.Normalize(root.GetProperty("lang").GetString());
                    _settings.Save();
                    break;

                case "setLightMode":   // chave "Modo leve" nas configurações
                    _settings.LightMode = root.GetProperty("enabled").GetBoolean();
                    _settings.Save();
                    _meter.Interval = _settings.LightMode ? MeterLiteMs : MeterMs;
                    ApplyBackdrop();
                    break;

                case "setAutostart":
                    AutoStart.Set(root.GetProperty("enabled").GetBoolean());
                    break;

                case "openSite":   // link "Site do Volum": abre no navegador padrão e fecha o mixer
                    try { Process.Start(new ProcessStartInfo(SiteUrl) { UseShellExecute = true }); }
                    catch (Exception ex) { Debug.WriteLine(ex); }
                    _ = CloseAnimatedAsync();
                    break;

                case "hide":   // botão minimizar: fecha com animação (também no modo --dev)
                    _ = CloseAnimatedAsync();
                    break;

                case "tick":   // som sutil ao mudar um volume
                    TickSound.Play();
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _sync.Dispose();
            _meter.Dispose();
            _idle.Dispose();
            _watcher?.Dispose();
            _reloadDebounce?.Dispose();
            _audio?.Dispose();
            _audio = null;
        }
        base.Dispose(disposing);
    }
}
