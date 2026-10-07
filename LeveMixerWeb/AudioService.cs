using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LeveMixer;

// ---- Mensagens que o C# envia para a interface (viram JSON em camelCase) ----
sealed record AppDto(string Id, string Name, float Volume, bool Muted, float Peak, bool Active, string? Icon);
sealed record MasterDto(float Volume, bool Muted, float Peak);
sealed record StateDto(string Type, MasterDto Master, List<AppDto> Apps);
sealed record PeaksDto(string Type, float Master, Dictionary<string, float> Apps);

/// <summary>Toda a lógica de áudio (Core Audio / WASAPI). Não sabe nada de interface.</summary>
sealed class AudioService : IDisposable
{
    public const string MasterId = "__master";

    sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public string? Path;
        public List<AudioSessionControl> Sessions = new();
    }

    sealed record ProcInfo(string Id, string Name, string? Path);

    readonly MMDeviceEnumerator _enumerator = new();
    readonly HashSet<string> _iconsSent;
    readonly Dictionary<string, Row> _rows = new();
    readonly Dictionary<uint, ProcInfo> _procCache = new();
    MMDevice? _device;
    string? _deviceId;

    /// <param name="iconsSent">Ids de apps cujo ícone a interface já recebeu (cada ícone é enviado uma vez só).</param>
    public AudioService(HashSet<string> iconsSent) => _iconsSent = iconsSent;

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
        return new StateDto("state", new MasterDto(vol.MasterVolumeLevelScalar, vol.Mute, 0f), apps);
    }

    public PeaksDto Peaks()
    {
        var apps = new Dictionary<string, float>();
        foreach (var r in _rows.Values)
            apps[r.Id] = r.Sessions.Count == 0 ? 0f : r.Sessions.Max(s => s.AudioMeterInformation.MasterPeakValue);
        return new PeaksDto("peaks", _device!.AudioMeterInformation.MasterPeakValue, apps);
    }

    // ---------- comandos vindos da interface ----------

    public void SetVolume(string id, float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (id == MasterId) { _device!.AudioEndpointVolume.MasterVolumeLevelScalar = value; return; }
        if (_rows.TryGetValue(id, out var row))
            foreach (var s in row.Sessions) s.SimpleAudioVolume.Volume = value;
    }

    public void SetMute(string id, bool muted)
    {
        if (id == MasterId) { _device!.AudioEndpointVolume.Mute = muted; return; }
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

        var mgr = _device.AudioSessionManager;
        mgr.RefreshSessions();
        var col = mgr.Sessions;

        var groups = new Dictionary<string, List<AudioSessionControl>>();
        var infos = new Dictionary<string, ProcInfo>();

        for (int i = 0; i < col.Count; i++)
        {
            var s = col[i];
            if (s.State == AudioSessionState.AudioSessionStateExpired)
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
            string name = $"Processo {pid}";
            string? path = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                id = p.ProcessName.ToLowerInvariant();
                name = char.ToUpperInvariant(p.ProcessName[0]) + p.ProcessName.Substring(1);
                try
                {
                    var mm = p.MainModule;
                    path = mm?.FileName;
                    var desc = mm?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                }
                catch { /* processo elevado/protegido: fica só com o nome */ }
            }
            catch { /* processo já encerrou */ }

            try
            {
                var dn = s.DisplayName;
                if (!string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("@")) name = dn.Trim();
            }
            catch { }

            pi = new ProcInfo(id, name, path);
        }

        _procCache[pid] = pi;
        return pi;
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
        _enumerator.Dispose();
        _procCache.Clear();
    }
}
