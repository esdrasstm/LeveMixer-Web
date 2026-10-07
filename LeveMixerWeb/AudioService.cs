using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Volum;

// ---- Mensagens que o C# envia para a interface (viram JSON em camelCase) ----
sealed record AppDto(string Id, string Name, float Volume, bool Muted, float Peak, bool Active, string? Icon);
sealed record MasterDto(float Volume, bool Muted, float Peak);
sealed record DeviceDto(string Id, string Name);
// Volume = ganho do microfone; Devices = microfones conectados (para escolher o padrão)
sealed record MicDto(float Volume, bool Muted, string Device, string Id, List<DeviceDto> Devices);
sealed record StateDto(string Type, MasterDto Master, List<AppDto> Apps, MicDto? Mic = null);
sealed record PeaksDto(string Type, float Master, Dictionary<string, float> Apps, float Mic = 0f);

/// <summary>Toda a lógica de áudio (Core Audio / WASAPI). Não sabe nada de interface.</summary>
sealed class AudioService : IDisposable
{
    public const string MasterId = "__master";
    public const string MicId = "__mic";

    sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public string? Path;
        public List<AudioSessionControl> Sessions = new();
    }

    sealed record ProcInfo(string Id, string Name, string? Path);

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    readonly MMDeviceEnumerator _enumerator = new();
    readonly HashSet<string> _iconsSent;
    readonly Dictionary<string, Row> _rows = new();
    readonly Dictionary<uint, ProcInfo> _procCache = new();
    MMDevice? _device;
    string? _deviceId;
    MMDevice? _mic;                // microfone padrão do Windows (null = nenhum)

    // O Windows só mede o nível do microfone enquanto algum app o está ouvindo. Para o medidor
    // reagir à voz, o Volum ouve o microfone (sem gravar nada) só enquanto a janela está aberta.
    readonly bool _listenMic;
    WasapiCapture? _micListen;

    /// <param name="iconsSent">Ids de apps cujo ícone a interface já recebeu (cada ícone é enviado uma vez só).</param>
    /// <param name="listenMic">Liga o medidor do microfone (desligado no modo leve).</param>
    public AudioService(HashSet<string> iconsSent, bool listenMic)
    {
        _iconsSent = iconsSent;
        _listenMic = listenMic;
    }

    // ---------- leitura ----------

    public StateDto Snapshot()
    {
        Sync();

        var apps = _rows.Values
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r =>
            {
                string? icon = null;
                // Sem caminho do .exe ainda (processo protegido ou recém-aberto): tenta de novo no próximo ciclo
                if (r.Path != null && _iconsSent.Add(r.Id)) icon = IconDataUri(r.Path);
                return new AppDto(
                    r.Id, r.Name,
                    r.Sessions.Count == 0 ? 0f : r.Sessions.Max(s => s.SimpleAudioVolume.Volume),
                    r.Sessions.Count > 0 && r.Sessions.All(s => s.SimpleAudioVolume.Mute),
                    0f,
                    r.Sessions.Any(s => s.State == AudioSessionState.AudioSessionStateActive),
                    icon);
            })
            .ToList();

        var vol = _device!.AudioEndpointVolume;

        MicDto? mic = null;
        if (_mic != null)
        {
            try
            {
                var mv = _mic.AudioEndpointVolume;
                mic = new MicDto(mv.MasterVolumeLevelScalar, mv.Mute, MicName(_mic), _mic.ID, MicDevices());
            }
            catch { /* microfone desconectado entre um ciclo e outro */ }
        }

        return new StateDto("state", new MasterDto(vol.MasterVolumeLevelScalar, vol.Mute, 0f), apps, mic);
    }

    public PeaksDto Peaks()
    {
        var apps = new Dictionary<string, float>();
        foreach (var r in _rows.Values)
            apps[r.Id] = r.Sessions.Count == 0 ? 0f : r.Sessions.Max(s => s.AudioMeterInformation.MasterPeakValue);

        // O medidor do microfone só se mexe quando algum app está gravando (Discord, OBS...)
        float micPeak = 0f;
        try { if (_mic != null) micPeak = _mic.AudioMeterInformation.MasterPeakValue; } catch { }

        return new PeaksDto("peaks", _device!.AudioMeterInformation.MasterPeakValue, apps, micPeak);
    }

    // ---------- comandos vindos da interface ----------

    public void SetVolume(string id, float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (id == MasterId) { _device!.AudioEndpointVolume.MasterVolumeLevelScalar = value; return; }
        if (id == MicId) { if (_mic != null) _mic.AudioEndpointVolume.MasterVolumeLevelScalar = value; return; }
        if (_rows.TryGetValue(id, out var row))
            foreach (var s in row.Sessions) s.SimpleAudioVolume.Volume = value;
    }

    public void SetMute(string id, bool muted)
    {
        if (id == MasterId) { _device!.AudioEndpointVolume.Mute = muted; return; }
        if (id == MicId) { if (_mic != null) _mic.AudioEndpointVolume.Mute = muted; return; }
        if (_rows.TryGetValue(id, out var row))
            foreach (var s in row.Sessions) s.SimpleAudioVolume.Mute = muted;
    }

    // ---------- sincronização com o Windows ----------

    void Sync()
    {
        // Dispositivo de saída padrão (se mudou, reconstrói tudo)
        var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (_device == null || dev.ID != _deviceId)
        {
            ClearRows();
            _device?.Dispose();
            _device = dev;
            _deviceId = dev.ID;
        }
        else
        {
            dev.Dispose();
        }

        SyncMic();

        var mgr = _device.AudioSessionManager;
        mgr.RefreshSessions();
        var col = mgr.Sessions;

        var groups = new Dictionary<string, List<AudioSessionControl>>();
        var infos = new Dictionary<string, ProcInfo>();

        for (int i = 0; i < col.Count; i++)
        {
            var s = col[i];
            // Sessões expiradas e a do próprio Volum (o som de "tick") não entram na lista
            if (s.State == AudioSessionState.AudioSessionStateExpired || s.GetProcessID == OwnPid)
            {
                s.Dispose();
                continue;
            }
            var pi = Resolve(s);
            if (!groups.TryGetValue(pi.Id, out var list))
            {
                groups[pi.Id] = list = new List<AudioSessionControl>();
                infos[pi.Id] = pi;
            }
            list.Add(s);
        }

        // Apps que fecharam o áudio
        foreach (var gone in _rows.Keys.Where(k => !groups.ContainsKey(k)).ToList())
        {
            foreach (var s in _rows[gone].Sessions) s.Dispose();
            _rows.Remove(gone);
        }

        // Apps novos / existentes
        foreach (var kv in groups)
        {
            if (!_rows.TryGetValue(kv.Key, out var row))
            {
                var pi = infos[kv.Key];
                row = new Row { Id = pi.Id, Name = pi.Name, Path = pi.Path };
                _rows[kv.Key] = row;
            }
            foreach (var old in row.Sessions) old.Dispose();
            row.Sessions = kv.Value;
        }
    }

    // Ler os nomes dos dispositivos é lento (~130 ms): a lista fica guardada e só é refeita
    // quando o Windows avisa que algum microfone foi conectado, desconectado ou trocado.
    List<DeviceDto>? _micDevices;
    readonly DeviceWatcher _deviceWatcher = new();
    bool _watcherRegistered;

    // Microfones conectados (ativos), em ordem alfabética
    List<DeviceDto> MicDevices()
    {
        if (!_watcherRegistered)
        {
            try { _enumerator.RegisterEndpointNotificationCallback(_deviceWatcher); } catch { }
            _watcherRegistered = true;
        }
        if (_micDevices != null && !_deviceWatcher.TakeChanged()) return _micDevices;

        var list = new List<DeviceDto>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add(new DeviceDto(d.ID, CleanName(d.FriendlyName)));
            }
        }
        catch { }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return _micDevices = list;
    }

    // Nome do microfone atual, tirado da lista guardada (sem ler as propriedades do dispositivo de novo)
    string MicName(MMDevice mic) =>
        MicDevices().Find(d => d.Id == mic.ID)?.Name ?? CleanName(mic.FriendlyName);

    /// <summary>Recebe os avisos do Windows sobre dispositivos de áudio (chegam em outra thread).</summary>
    sealed class DeviceWatcher : IMMNotificationClient
    {
        volatile bool _changed;
        public bool TakeChanged() { bool c = _changed; _changed = false; return c; }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed = true;
        public void OnDeviceAdded(string pwstrDeviceId) => _changed = true;
        public void OnDeviceRemoved(string deviceId) => _changed = true;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed = true;
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }   // volume etc.: ignora
    }

    static string CleanName(string name) => name.Replace("(R)", "").Replace("®", "").Replace("  ", " ").Trim();

    /// <summary>Torna este microfone o padrão do Windows (para todos os usos: geral, multimídia e chamadas).</summary>
    public void SetDefaultMic(string id)
    {
        DefaultDevice.Set(id);
        SyncMic();
    }

    // Microfone padrão (o mesmo que aparece como padrão nas configurações de som do Windows)
    void SyncMic()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                StopMicListen();
                _mic?.Dispose();
                _mic = null;
                return;
            }
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            if (_mic != null && dev.ID == _mic.ID) { dev.Dispose(); return; }
            StopMicListen();
            _mic?.Dispose();
            _mic = dev;
            StartMicListen();
        }
        catch
        {
            StopMicListen();
            _mic?.Dispose();
            _mic = null;
        }
    }

    // Ligar a escuta leva ~0,5 s: roda em segundo plano (com objetos próprios) para não atrasar a abertura.
    readonly object _listenLock = new();
    int _listenGen;                // muda a cada start/stop: uma escuta atrasada sabe que já não vale

    void StartMicListen()
    {
        if (!_listenMic || _mic == null) return;
        string id = _mic.ID;
        int gen;
        lock (_listenLock) gen = ++_listenGen;

        System.Threading.Tasks.Task.Run(() =>
        {
            WasapiCapture? cap = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                cap = new WasapiCapture(en.GetDevice(id));
                cap.DataAvailable += (_, _) => { };      // o áudio é descartado: só o nível importa
                cap.StartRecording();
            }
            catch
            {
                // Acesso ao microfone bloqueado na privacidade do Windows, por exemplo: medidor fica parado
                cap?.Dispose();
                return;
            }
            lock (_listenLock)
            {
                if (gen == _listenGen) { _micListen = cap; return; }
            }
            cap.Dispose();                               // já mandaram parar enquanto ligava
        });
    }

    void StopMicListen()
    {
        WasapiCapture? cap;
        lock (_listenLock)
        {
            _listenGen++;
            cap = _micListen;
            _micListen = null;
        }
        // Desligar também espera a thread de captura: em segundo plano, a janela fecha sem travar
        if (cap != null) System.Threading.Tasks.Task.Run(() => { try { cap.Dispose(); } catch { } });
    }

    void ClearRows()
    {
        foreach (var r in _rows.Values)
            foreach (var s in r.Sessions) s.Dispose();
        _rows.Clear();
    }

    // ---------- identificação do app (nome + ícone), com cache por PID ----------

    ProcInfo Resolve(AudioSessionControl s)
    {
        uint pid = s.GetProcessID;
        if (_procCache.TryGetValue(pid, out var cached)) return cached;

        ProcInfo pi;
        if (s.IsSystemSoundsSession || pid == 0)
        {
            pi = new ProcInfo("__system", "Sons do sistema", null);
        }
        else
        {
            string id = $"pid{pid}";
            string fallback = $"Processo {pid}";
            string? path = null;
            // Nomes que o app informa (sessão de áudio, descrição e produto do .exe)
            var names = new List<string?>();
            try
            {
                using var p = Process.GetProcessById((int)pid);
                id = p.ProcessName.ToLowerInvariant();
                fallback = char.ToUpperInvariant(p.ProcessName[0]) + p.ProcessName.Substring(1);
                try
                {
                    var mm = p.MainModule;
                    path = mm?.FileName;
                    names.Add(mm?.FileVersionInfo.FileDescription);
                    names.Add(mm?.FileVersionInfo.ProductName);
                }
                catch { /* processo elevado/protegido: fica só com o nome */ }
            }
            catch { /* processo já encerrou */ }

            try { names.Add(s.DisplayName); } catch { }

            pi = new ProcInfo(id, PickName(names, fallback), path);
        }

        _procCache[pid] = pi;
        return pi;
    }

    // Nomes genéricos que não dizem qual é o app
    static readonly string[] GenericNames = { "Electron", "Chromium", "Java", "Node.js", "Host" };

    /// <summary>
    /// Regra do nome: entre os nomes que o app informa, fica o mais curto
    /// ("Mozilla Firefox" / "Firefox" → "Firefox"; "A native Spotify client" / "Spotify" → "Spotify").
    /// Limpa ®, ™ e ©; ignora nomes de recurso ("@...") e genéricos; sem nenhum, usa o nome do processo.
    /// </summary>
    static string PickName(IEnumerable<string?> candidates, string fallback)
    {
        string? best = null;
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("@")) continue;
            var n = string.Join(' ', raw.Replace("®", "").Replace("™", "").Replace("©", "")
                                        .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (n.Length < 2 || GenericNames.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
            if (best == null || n.Length < best.Length) best = n;
        }
        return best ?? fallback;
    }

    static string? IconDataUri(string? path)
    {
        if (path == null) return null;
        try
        {
            using var ic = Icon.ExtractAssociatedIcon(path);
            if (ic == null) return null;
            using var bmp = ic.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    public void Dispose()
    {
        ClearRows();
        _device?.Dispose();
        _device = null;
        StopMicListen();
        _mic?.Dispose();
        _mic = null;
        if (_watcherRegistered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceWatcher); } catch { }
        }
        _enumerator.Dispose();
        _procCache.Clear();
    }
}

/// <summary>
/// Troca o dispositivo padrão do Windows. O Windows não tem API pública para isso:
/// usa a interface interna IPolicyConfig (a mesma do EarTrumpet e do SoundSwitch), estável desde o Windows 7.
/// </summary>
static class DefaultDevice
{
    public static void Set(string deviceId)
    {
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                policy.SetDefaultEndpoint(deviceId, role);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(policy);
        }
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    // Só SetDefaultEndpoint é usado; os outros métodos ficam aqui para manter a ordem da interface.
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [System.Runtime.InteropServices.PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [System.Runtime.InteropServices.PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [System.Runtime.InteropServices.PreserveSig] int ResetDeviceFormat(IntPtr a);
        [System.Runtime.InteropServices.PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [System.Runtime.InteropServices.PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [System.Runtime.InteropServices.PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [System.Runtime.InteropServices.PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [System.Runtime.InteropServices.PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [System.Runtime.InteropServices.PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [System.Runtime.InteropServices.PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [System.Runtime.InteropServices.PreserveSig]
        int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string deviceId, Role role);
        [System.Runtime.InteropServices.PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
