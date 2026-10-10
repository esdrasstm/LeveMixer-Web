/* =========================================================
   Volum — ponte de demonstração (só para a landing page)
   No app de verdade, o C# conversa com a interface por window.chrome.webview.
   Aqui, este arquivo faz o papel do C#: guarda volumes, simula o som dos apps,
   os medidores e o microfone, e responde às mesmas mensagens:
     interface -> "C#": ready | resize | setVolume | setMute | setTheme | setAutostart
                        | setLightMode | setLanguage | setMicDevice | hide | tick
     "C#" -> interface: settings | state | peaks | open | close
   Avisa a página de fora (a LP) por postMessage: resize, hide, lite.
   Precisa ser carregado ANTES do app.js.
   ========================================================= */
(() => {
  'use strict';

  const listeners = [];
  const send = (msg) => setTimeout(() => listeners.forEach((fn) => fn({ data: msg })), 0);
  const toPage = (msg) => { try { window.parent.postMessage(Object.assign({ volumDemo: true }, msg), '*'); } catch (e) { /* sem página de fora */ } };

  // ---------- Ícones dos apps de exemplo (genéricos, sem marcas) ----------
  const icon = (c1, c2, glyph) => 'data:image/svg+xml;utf8,' + encodeURIComponent(
    "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 26 26'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>" +
    "<stop offset='0' stop-color='" + c1 + "'/><stop offset='1' stop-color='" + c2 + "'/></linearGradient></defs>" +
    "<rect width='26' height='26' rx='7' fill='url(#g)'/>" + glyph + '</svg>');
  const W = "fill='none' stroke='#fff' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'";
  const ICONS = {
    musica: icon('#34d399', '#059669', "<path d='M10.5 17V8.5l7-1.5V15.5' " + W + "/><circle cx='8.8' cy='17' r='1.9' fill='#fff'/><circle cx='15.8' cy='15.5' r='1.9' fill='#fff'/>"),
    chamada: icon('#818cf8', '#4f46e5', "<path d='M7 14v-1.5a6 6 0 0112 0V14' " + W + "/><rect x='6' y='13.5' width='3.2' height='5' rx='1.4' fill='#fff'/><rect x='16.8' y='13.5' width='3.2' height='5' rx='1.4' fill='#fff'/>"),
    jogo: icon('#fb923c', '#ea580c', "<rect x='5.5' y='9.5' width='15' height='8' rx='4' " + W + "/><path d='M9.5 12v3M8 13.5h3' " + W + "/><circle cx='16.3' cy='12.6' r='1' fill='#fff'/><circle cx='17.8' cy='14.6' r='1' fill='#fff'/>"),
    navegador: icon('#38bdf8', '#0369a1', "<circle cx='13' cy='13' r='6.5' " + W + "/><path d='M6.5 13h13M13 6.5a10 10 0 010 13M13 6.5a10 10 0 000 13' " + W + "/>"),
  };

  // ---------- Estado "do Windows" ----------
  const settings = { theme: 'dark', autostart: true, glass: true, lite: false, language: 'pt' };
  const model = {
    master: { volume: 0.72, muted: false },
    mic: {
      volume: 0.85, muted: false, id: 'mic-usb',
      devices: [
        { id: 'mic-usb', name: 'Microfone (USB Audio)' },
        { id: 'mic-headset', name: 'Headset (Bluetooth)' },
        { id: 'mic-array', name: 'Microphone Array (Realtek Audio)' },
      ],
    },
    apps: [
      { id: 'chamada', name: 'Chamada', volume: 1, muted: false, active: true, icon: ICONS.chamada },
      { id: 'jogo', name: 'Jogo', volume: 0.35, muted: false, active: true, icon: ICONS.jogo },
      { id: 'musica', name: 'Música', volume: 0.6, muted: false, active: true, icon: ICONS.musica },
      { id: 'navegador', name: 'Navegador', volume: 0.8, muted: false, active: false, icon: ICONS.navegador },
      { id: '__system', name: 'Sons do sistema', volume: 0.5, muted: false, active: false, icon: null },
    ],
  };
  const findApp = (id) => model.apps.find((a) => a.id === id);

  function state() {
    const mic = model.mic;
    return {
      type: 'state',
      master: { volume: model.master.volume, muted: model.master.muted, peak: 0 },
      apps: model.apps.map((a) => ({ id: a.id, name: a.name, volume: a.volume, muted: a.muted, peak: 0, active: a.active, icon: a.icon })),
      mic: {
        volume: mic.volume, muted: mic.muted, id: mic.id, devices: mic.devices,
        device: (mic.devices.find((d) => d.id === mic.id) || mic.devices[0]).name,
      },
    };
  }

  // ---------- Som de mentirinha (medidores) ----------
  const t0 = performance.now();
  const wave = {
    // batida de música: pulso a cada ~0,46 s
    musica: (t) => 0.35 + 0.55 * Math.pow(Math.max(0, Math.sin(t * 6.8)), 6) + 0.1 * Math.random(),
    // voz numa chamada: frases de ~2 s com pausas
    chamada: (t) => (Math.sin(t * 0.9) > -0.2 ? 0.25 + 0.45 * Math.abs(Math.sin(t * 9.3) * Math.sin(t * 3.1)) : 0.02),
    // jogo: ruído contínuo com explosões de vez em quando
    jogo: (t) => 0.2 + 0.25 * Math.random() + (Math.sin(t * 0.7) > 0.93 ? 0.4 : 0),
  };
  // "Você falando" no microfone: fala por alguns segundos, cala por outros
  const micVoice = (t) => (Math.sin(t * 0.55) > 0.1 ? 0.04 + 0.22 * Math.abs(Math.sin(t * 8.1) * Math.sin(t * 2.3)) : 0.005);

  function peaks() {
    const t = (performance.now() - t0) / 1000;
    const apps = {};
    let loudest = 0;
    for (const a of model.apps) {
      const raw = a.active && wave[a.id] ? wave[a.id](t) : 0;
      const p = a.muted ? 0 : Math.min(1, raw * a.volume);
      apps[a.id] = p;
      loudest = Math.max(loudest, p);
    }
    const master = model.master.muted ? 0 : loudest * model.master.volume;
    const mic = model.mic.muted ? 0 : micVoice(t) * (0.4 + 0.6 * model.mic.volume);
    return { type: 'peaks', master, apps, mic };
  }

  // ---------- Tick (o mesmo "tic" baixinho do app, gerado na hora) ----------
  let audio;
  function tick() {
    try {
      audio = audio || new (window.AudioContext || window.webkitAudioContext)();
      const now = audio.currentTime;
      const gain = audio.createGain();
      gain.gain.setValueAtTime(0, now);
      gain.gain.linearRampToValueAtTime(0.05, now + 0.001);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.045);
      gain.connect(audio.destination);
      [1500, 3000].forEach((f, i) => {
        const osc = audio.createOscillator();
        osc.frequency.value = f;
        const g = audio.createGain();
        g.gain.value = i ? 0.3 : 1;
        osc.connect(g).connect(gain);
        osc.start(now);
        osc.stop(now + 0.05);
      });
    } catch (e) { /* navegador sem áudio: fica em silêncio */ }
  }

  // ---------- Abrir / fechar (como o clique na bandeja) ----------
  let timers = [];
  function open() {
    send(Object.assign({ type: 'settings' }, settings));
    send(state());
    send({ type: 'open' });
    timers.forEach(clearInterval);
    timers = [
      setInterval(() => send(state()), 1000),
      setInterval(() => send(peaks()), settings.lite ? 150 : 60),
    ];
  }
  function close() {
    timers.forEach(clearInterval);
    timers = [];
    send({ type: 'close' });
    toPage({ type: 'hide' });
  }

  // A página de fora (ícone da bandeja desenhado na LP) pede para abrir ou fechar
  window.addEventListener('message', (e) => {
    const m = e.data || {};
    if (!m.volumDemo) return;
    if (m.action === 'open') open();
    if (m.action === 'close') close();
    // A página pode começar a escutar depois da primeira medida: repete a última altura
    if (m.action === 'sync' && lastHeight) toPage({ type: 'resize', height: lastHeight });
  });
  let lastHeight = 0;

  // ---------- Mensagens vindas da interface (o que o C# receberia) ----------
  function handle(m) {
    switch (m.type) {
      case 'ready': open(); break;
      case 'resize': lastHeight = m.height; toPage({ type: 'resize', height: m.height }); break;
      case 'setVolume': {
        if (m.id === '__master') model.master.volume = m.value;
        else if (m.id === '__mic') model.mic.volume = m.value;
        else { const a = findApp(m.id); if (a) a.volume = m.value; }
        break;
      }
      case 'setMute': {
        if (m.id === '__master') model.master.muted = m.muted;
        else if (m.id === '__mic') model.mic.muted = m.muted;
        else { const a = findApp(m.id); if (a) a.muted = m.muted; }
        break;
      }
      case 'setTheme': settings.theme = m.mode; break;
      case 'setLanguage': settings.language = m.lang; break;
      case 'setAutostart': settings.autostart = m.enabled; break;
      case 'setLightMode':
        settings.lite = m.enabled;
        toPage({ type: 'lite', on: m.enabled });
        if (timers.length) { clearInterval(timers[1]); timers[1] = setInterval(() => send(peaks()), m.enabled ? 150 : 60); }
        break;
      case 'setMicDevice': model.mic.id = m.id; send(state()); break;
      case 'hide': close(); break;
      case 'tick': tick(); break;
      case 'openSite': toPage({ type: 'top' }); break;   // já estamos no site: volta para o topo
    }
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: handle,
    addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
  };
})();
