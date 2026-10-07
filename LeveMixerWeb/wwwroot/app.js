/* =========================================================
   Volum — comportamento da interface
   Fala com o C# por mensagens JSON:
     C# -> JS : settings | state | peaks | open | close
     JS -> C# : ready | resize | setVolume | setMute | setTheme | setAutostart
                | setLightMode | setLanguage | hide | tick
   ========================================================= */
(() => {
  'use strict';

  const MASTER = '__master';
  const MIC = '__mic';
  const isFixed = (id) => id === MASTER || id === MIC;   // linhas fixas do painel de cima
  const $ = (id) => document.getElementById(id);

  /* ---------------- Idiomas ----------------
     Português é o padrão. Para traduzir um texto novo: coloque data-i18n="chave" no HTML
     (ou data-i18n-title para a dica do mouse) e a chave nos três idiomas abaixo. */
  const I18N = {
    pt: {
      settings: 'Configurações', minimize: 'Minimizar', theme: 'Tema', language: 'Idioma',
      system: 'Sistema', dark: 'Escuro', light: 'Claro',
      autostart: 'Iniciar com o Windows', lite: 'Modo leve', liteHint: 'Desliga vidro, brilhos e animações',
      apps: 'Aplicativos', empty: 'Nenhum app usando áudio agora',
      master: 'Volume geral', mic: 'Microfone', systemSounds: 'Sons do sistema',
      mute: 'Silenciar', unmute: 'Ativar som', micMute: 'Desligar microfone', micUnmute: 'Ligar microfone',
      micPick: 'Escolher microfone',
    },
    en: {
      settings: 'Settings', minimize: 'Minimize', theme: 'Theme', language: 'Language',
      system: 'System', dark: 'Dark', light: 'Light',
      autostart: 'Start with Windows', lite: 'Lite mode', liteHint: 'Turns off glass, glow and animations',
      apps: 'Apps', empty: 'No apps playing audio right now',
      master: 'Master volume', mic: 'Microphone', systemSounds: 'System sounds',
      mute: 'Mute', unmute: 'Unmute', micMute: 'Mute microphone', micUnmute: 'Unmute microphone',
      micPick: 'Choose microphone',
    },
    es: {
      settings: 'Configuración', minimize: 'Minimizar', theme: 'Tema', language: 'Idioma',
      system: 'Sistema', dark: 'Oscuro', light: 'Claro',
      autostart: 'Iniciar con Windows', lite: 'Modo ligero', liteHint: 'Desactiva vidrio, brillos y animaciones',
      apps: 'Aplicaciones', empty: 'Ninguna app está reproduciendo audio',
      master: 'Volumen general', mic: 'Micrófono', systemSounds: 'Sonidos del sistema',
      mute: 'Silenciar', unmute: 'Activar sonido', micMute: 'Silenciar micrófono', micUnmute: 'Activar micrófono',
      micPick: 'Elegir micrófono',
    },
  };
  let lang = 'pt';
  const t = (key) => (I18N[lang] && I18N[lang][key]) || I18N.pt[key] || key;

  // Linhas do sistema: o nome vem da tradução, não do C#
  const SYSTEM_NAMES = { __master: 'master', __mic: 'mic', __system: 'systemSounds' };
  const displayName = (id, name) => (SYSTEM_NAMES[id] ? t(SYSTEM_NAMES[id]) : name);
  const post = (msg) => window.chrome.webview.postMessage(msg);

  const card = $('card');
  const appsEl = $('apps');
  const masterEl = $('master');
  const micEl = $('mic');
  const micRowEl = $('micRow');
  const micPickBtn = $('micPickBtn');
  const micCurrentEl = $('micCurrent');
  const micListEl = $('micList');
  const micListInner = micListEl.firstElementChild;
  const emptyEl = $('empty');
  const settingsEl = $('settings');
  const settingsBtn = $('settingsBtn');
  const autostartEl = $('autostart');
  const liteEl = $('lite');
  const appCountEl = $('appCount');
  const themeSeg = $('themeSeg');
  const langSeg = $('langSeg');
  const root = document.documentElement;
  const prefersLight = window.matchMedia('(prefers-color-scheme: light)');

  const ICON_VOL =
    '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9H4z" fill="currentColor"/>' +
    '<path d="M16 9a4 4 0 010 6M18.5 6.5a8 8 0 010 11" fill="none" stroke="currentColor" ' +
    'stroke-width="1.8" stroke-linecap="round"/></svg>';
  const ICON_MUTE =
    '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9H4z" fill="currentColor"/>' +
    '<path d="M16.5 9.5l5 5M21.5 9.5l-5 5" fill="none" stroke="currentColor" ' +
    'stroke-width="1.8" stroke-linecap="round"/></svg>';
  const ICON_MIC =
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">' +
    '<rect x="9" y="3" width="6" height="11" rx="3" fill="currentColor" stroke="none"/>' +
    '<path d="M5.5 11a6.5 6.5 0 0013 0M12 17.5V21"/></svg>';
  const ICON_MIC_OFF =
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">' +
    '<rect x="9" y="3" width="6" height="11" rx="3" fill="currentColor" stroke="none" opacity=".45"/>' +
    '<path d="M5.5 11a6.5 6.5 0 0010.6 5M18.5 11a6.4 6.4 0 01-.5 2.5M12 17.5V21M4 4l16 16"/></svg>';

  // Ícones das linhas do sistema (não têm .exe de onde tirar o ícone)
  const SYSTEM_ICONS = {
    // Volume geral: caixa de som
    __master:
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" ' +
      'stroke-linecap="round" stroke-linejoin="round">' +
      '<rect x="5" y="2.5" width="14" height="19" rx="3"/>' +
      '<circle cx="12" cy="14.5" r="3.6"/><circle cx="12" cy="7" r="1.2" fill="currentColor"/></svg>',
    // Sons do sistema: sino de notificação
    __system:
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" ' +
      'stroke-linecap="round" stroke-linejoin="round">' +
      '<path d="M6 16.5V11a6 6 0 0112 0v5.5l1.5 1.5h-15z"/><path d="M10 20.5a2.2 2.2 0 004 0"/></svg>',
    // Microfone: o microfone em traço
    __mic:
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" ' +
      'stroke-linecap="round" stroke-linejoin="round">' +
      '<rect x="8.5" y="2.5" width="7" height="12" rx="3.5"/><path d="M5 11a7 7 0 0014 0M12 18v3.5M8.5 21.5h7"/></svg>',
  };

  const rows = new Map();       // id -> { el, slider, ... }
  const iconCache = new Map();  // id -> data URI (o C# envia cada ícone uma única vez)

  /* ---------------- Linhas ---------------- */

  function createRow(id, name) {
    const el = document.createElement('div');
    el.className = 'row';
    el.dataset.id = id;
    el.innerHTML =
      '<div class="row-icon"></div>' +
      '<div class="row-main">' +
        '<div class="row-top"><span class="row-name"></span><span class="row-pct"></span></div>' +
        '<div class="slider">' +
          '<div class="slider-track"><div class="slider-fill"></div></div>' +
          '<div class="slider-thumb"></div>' +
          '<input class="slider-input" type="range" min="0" max="100" step="1">' +
        '</div>' +
        '<div class="meter"><div class="meter-fill"></div></div>' +
      '</div>' +
      '<button class="icon-btn mute"></button>';

    const r = {
      id, el, name,
      iconEl: el.querySelector('.row-icon'),
      nameEl: el.querySelector('.row-name'),
      pct: el.querySelector('.row-pct'),
      sliderBox: el.querySelector('.slider'),        // desenho (trilho, preenchimento, bolinha)
      slider: el.querySelector('.slider-input'),     // input invisível que recebe o mouse
      meter: el.querySelector('.meter-fill'),
      mute: el.querySelector('.mute'),
      muted: false,
      dragging: false,
      peak: 0,
      iconSrc: undefined,                // diferente de null: o primeiro setIcon sempre desenha
    };

    r.nameEl.textContent = displayName(id, name);
    setIcon(r, iconCache.get(id));
    updateMuteUi(r);

    r.slider.addEventListener('pointerdown', () => {
      r.dragging = true;
      el.classList.add('dragging');                // bolinha cresce, número em destaque
    });
    r.slider.addEventListener('input', () => {
      const v = Number(r.slider.value);
      tick(r, v);
      setFill(r, v);
      post({ type: 'setVolume', id, value: v / 100 });
      if (r.muted && v > 0) {            // mexeu no slider -> tira o mute
        r.muted = false;
        updateMuteUi(r);
        post({ type: 'setMute', id, muted: false });
      }
    });
    r.mute.addEventListener('click', () => {
      r.muted = !r.muted;
      updateMuteUi(r);
      post({ type: 'setMute', id, muted: r.muted });
    });

    rows.set(id, r);
    return r;
  }

  function setIcon(r, src) {
    if (r.iconSrc === (src || null)) return;
    r.iconSrc = src || null;
    if (src) {
      r.iconEl.textContent = '';
      const img = document.createElement('img');
      img.src = src;
      img.alt = '';
      r.iconEl.appendChild(img);
    } else if (SYSTEM_ICONS[r.id]) {
      r.iconEl.innerHTML = SYSTEM_ICONS[r.id];
    } else {
      r.iconEl.textContent = (r.name[0] || '?').toUpperCase();
    }
  }

  // Som sutil: um "tic" a cada 5% (como os dentes de um botão giratório), no máximo um a cada 45 ms
  const TICK_STEP = 5;
  let lastTickAt = 0;
  function tick(r, v) {
    const step = Math.floor(v / TICK_STEP);
    const prev = Math.floor(Number(r.pct.textContent) / TICK_STEP);
    const now = performance.now();
    if (step !== prev && now - lastTickAt > 45) {
      lastTickAt = now;
      post({ type: 'tick' });
    }
  }

  // --fill vai de 0 a 100; o CSS anima a mudança (o slider desliza até o valor novo)
  function setFill(r, v) {
    r.sliderBox.style.setProperty('--fill', v);
    r.pct.textContent = String(v);
  }

  function updateMuteUi(r) {
    const mic = r.id === MIC;
    const html = r.muted ? (mic ? ICON_MIC_OFF : ICON_MUTE) : (mic ? ICON_MIC : ICON_VOL);
    if (r.muteHtml !== html) { r.mute.innerHTML = html; r.muteHtml = html; }   // só troca se mudou
    r.mute.title = t(mic ? (r.muted ? 'micUnmute' : 'micMute') : (r.muted ? 'unmute' : 'mute'));
    r.el.classList.toggle('muted', r.muted);
  }

  function updateRow(r, d, dim) {
    if (d.name && d.name !== r.name) { r.name = d.name; r.nameEl.textContent = displayName(r.id, d.name); }
    if (d.icon) { iconCache.set(r.id, d.icon); setIcon(r, d.icon); }
    r.muted = !!d.muted;
    updateMuteUi(r);
    if (!r.dragging) {                    // não mexe no slider enquanto o usuário arrasta
      const v = Math.round(d.volume * 100);
      r.slider.value = v;
      setFill(r, v);
    }
    r.el.classList.toggle('dim', !!dim);
  }

  /* ---------------- Mensagens do C# ---------------- */

  function applyState(s) {
    // Volume geral
    let master = rows.get(MASTER);
    if (!master) {
      master = createRow(MASTER, '');
      masterEl.appendChild(master.el);
    }
    updateRow(master, { volume: s.master.volume, muted: s.master.muted }, false);

    // Microfone padrão do Windows (o slider é o ganho). Sem microfone, a linha some.
    let mic = rows.get(MIC);
    if (s.mic) {
      if (!mic) {
        mic = createRow(MIC, '');
        micRowEl.appendChild(mic.el);
      }
      updateRow(mic, { volume: s.mic.volume, muted: s.mic.muted }, false);
      updateMicPicker(s.mic);
    }
    micEl.hidden = !s.mic;

    // Apps
    const ids = s.apps.map((a) => a.id);
    for (const [id, r] of [...rows]) {
      if (!isFixed(id) && !ids.includes(id)) { r.el.remove(); rows.delete(id); }
    }
    for (const a of s.apps) {
      const r = rows.get(a.id) || createRow(a.id, a.name);
      updateRow(r, a, !a.active);
    }

    // Reordena só se a ordem mudou (mexer no DOM durante o arrasto cancelaria o slider)
    const current = [...appsEl.children].map((c) => c.dataset.id);
    if (ids.join('|') !== current.join('|')) {
      ids.forEach((id, i) => {
        const r = rows.get(id);
        r.el.style.setProperty('--i', i + 1);
        appsEl.appendChild(r.el);
      });
    }

    emptyEl.hidden = ids.length > 0;
    appCountEl.textContent = String(ids.length);
    reportHeight();
  }

  /* ---------------- Escolha do microfone ---------------- */

  const ICON_CHECK =
    '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 8.5l3 3 6-7" fill="none" ' +
    'stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  let micListSig = '';

  // Mostra o microfone atual e (re)monta a lista só quando os microfones conectados mudam
  function updateMicPicker(m) {
    micCurrentEl.textContent = m.device;
    const sig = m.id + '|' + m.devices.map((d) => d.id + '=' + d.name).join('|');
    if (sig === micListSig) return;
    micListSig = sig;

    micListInner.textContent = '';
    for (const d of m.devices) {
      const b = document.createElement('button');
      b.className = 'mic-option';
      b.setAttribute('role', 'option');
      b.setAttribute('aria-selected', String(d.id === m.id));
      b.innerHTML = '<span></span>' + ICON_CHECK;
      b.firstChild.textContent = d.name;
      b.title = d.name;
      b.addEventListener('click', () => {
        // Resposta imediata na tela; o C# troca o padrão do Windows e manda o estado novo
        micListInner.querySelectorAll('.mic-option').forEach((o) => o.setAttribute('aria-selected', String(o === b)));
        micCurrentEl.textContent = d.name;
        setMicListOpen(false);
        post({ type: 'setMicDevice', id: d.id });
      });
      micListInner.appendChild(b);
    }
  }

  function setMicListOpen(open) {
    micListEl.classList.toggle('open', open);
    micListEl.inert = !open;
    micPickBtn.setAttribute('aria-expanded', String(open));
  }
  micPickBtn.addEventListener('click', () => setMicListOpen(!micListEl.classList.contains('open')));

  // Medidor do microfone: ignora o chiado de fundo (silêncio = barra parada) e,
  // como a voz tem nível baixo, usa a raiz para a barra crescer bem visível ao falar.
  const MIC_GATE = 0.01;   // aumente se a barra se mexer sem você falar
  const micLevel = (raw) => (raw > MIC_GATE ? Math.sqrt((raw - MIC_GATE) / (1 - MIC_GATE)) : 0);

  function applyPeaks(p) {
    const smooth = (r, raw) => {
      r.peak = raw > r.peak ? raw : r.peak * 0.85;   // sobe rápido, desce suave
      r.meter.style.transform = 'scaleX(' + Math.min(1, r.peak) + ')';
    };
    const m = rows.get(MASTER);
    if (m) smooth(m, p.master);
    const mic = rows.get(MIC);
    if (mic) smooth(mic, micLevel(p.mic || 0));
    for (const [id, r] of rows) if (!isFixed(id)) smooth(r, p.apps[id] || 0);
  }

  function applySettings(s) {
    setLanguage(s.language || 'pt');
    setTheme(s.theme);
    glassSupported = !!s.glass;
    setLite(!!s.lite);
    autostartEl.checked = !!s.autostart;
    settingsEl.classList.add('instant');            // recolhe sem animação (a janela ainda vai aparecer)
    setSettingsOpen(false);                         // sempre abre com as configurações recolhidas
    void settingsEl.offsetHeight;
    settingsEl.classList.remove('instant');
    setMicListOpen(false);                          // e com a lista de microfones fechada
    forceResize = true;                             // o C# espera um "resize" para mostrar a janela
  }

  function replay(cls) {
    card.classList.remove('enter', 'leave');
    void card.offsetWidth;                          // reinicia a animação
    card.classList.add(cls);
  }

  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    switch (m.type) {
      case 'settings': applySettings(m); break;
      case 'state':    applyState(m);    break;
      case 'peaks':    applyPeaks(m);    break;
      case 'open':     replay('enter');  break;
      case 'close':    replay('leave');  break;
    }
  });

  /* ---------------- Configurações ---------------- */

  // Marca a opção escolhida num seletor (tema ou idioma); a pílula desliza até ela
  function selectSegment(seg, value) {
    [...seg.querySelectorAll('button')].forEach((b, i) => {
      const on = b.dataset.value === value;
      b.setAttribute('aria-checked', String(on));
      if (on) seg.style.setProperty('--seg-i', i);
    });
  }

  function setTheme(mode) {
    root.dataset.theme = mode;
    selectSegment(themeSeg, mode);
    applyScheme();
  }

  // Troca todos os textos da interface para o idioma escolhido
  function setLanguage(value) {
    lang = I18N[value] ? value : 'pt';
    root.lang = { pt: 'pt-BR', en: 'en', es: 'es' }[lang];
    selectSegment(langSeg, lang);
    document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
    document.querySelectorAll('[data-i18n-title]').forEach((el) => { el.title = t(el.dataset.i18nTitle); });
    document.querySelectorAll('[data-i18n-aria]').forEach((el) => { el.setAttribute('aria-label', t(el.dataset.i18nAria)); });
    rows.forEach((r) => {
      if (SYSTEM_NAMES[r.id]) r.nameEl.textContent = displayName(r.id, r.name);
      updateMuteUi(r);
    });
  }

  // Tema efetivo (claro/escuro): resolve o "Sistema" e acompanha a troca do Windows ao vivo
  function applyScheme() {
    const mode = root.dataset.theme;
    const light = mode === 'light' || (mode === 'system' && prefersLight.matches);
    root.dataset.scheme = light ? 'light' : 'dark';
  }
  prefersLight.addEventListener('change', applyScheme);

  // Vidro só quando o Windows suporta e o modo leve está desligado
  let glassSupported = false;
  function setLite(on) {
    liteEl.checked = on;
    root.classList.toggle('lite', on);
    root.classList.toggle('glass', glassSupported && !on);
  }

  liteEl.addEventListener('change', () => {
    setLite(liteEl.checked);
    post({ type: 'setLightMode', enabled: liteEl.checked });
  });

  // A animação é do CSS (.settings.open); o ResizeObserver acompanha a altura e a janela cresce junto.
  function setSettingsOpen(open) {
    settingsEl.classList.toggle('open', open);
    settingsEl.inert = !open;                       // fechada: sem foco por teclado nos controles
    settingsBtn.setAttribute('aria-expanded', String(open));
  }

  settingsBtn.addEventListener('click', () => {
    setSettingsOpen(!settingsEl.classList.contains('open'));
  });

  themeSeg.querySelectorAll('button').forEach((b) => b.addEventListener('click', () => {
    setTheme(b.dataset.value);
    post({ type: 'setTheme', mode: b.dataset.value });
  }));

  langSeg.querySelectorAll('button').forEach((b) => b.addEventListener('click', () => {
    setLanguage(b.dataset.value);
    post({ type: 'setLanguage', lang: b.dataset.value });
  }));

  $('minimizeBtn').addEventListener('click', () => post({ type: 'hide' }));

  autostartEl.addEventListener('change', () => {
    post({ type: 'setAutostart', enabled: autostartEl.checked });
  });

  const endDrag = () => rows.forEach((r) => { r.dragging = false; r.el.classList.remove('dragging'); });
  window.addEventListener('pointerup', endDrag);
  window.addEventListener('pointercancel', endDrag);

  /* ---------------- Tamanho da janela ---------------- */
  // O C# redimensiona a janela para caber exatamente no conteúdo.

  let lastH = 0;
  let forceResize = true;

  function reportHeight() {
    const h = card.offsetHeight;                    // offsetHeight ignora transform (animações)
    if (h !== lastH || forceResize) {
      lastH = h;
      forceResize = false;
      post({ type: 'resize', height: h });
    }
  }
  new ResizeObserver(reportHeight).observe(card);

  document.addEventListener('contextmenu', (e) => e.preventDefault());

  post({ type: 'ready' });
})();
