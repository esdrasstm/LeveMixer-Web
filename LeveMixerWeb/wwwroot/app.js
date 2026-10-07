/* =========================================================
   Volum — comportamento da interface
   Fala com o C# por mensagens JSON:
     C# -> JS : settings | state | peaks | open | close
     JS -> C# : ready | resize | setVolume | setMute | setTheme | setAutostart
                | setLightMode | hide | tick
   ========================================================= */
(() => {
  'use strict';

  const MASTER = '__master';
  const $ = (id) => document.getElementById(id);
  const post = (msg) => window.chrome.webview.postMessage(msg);

  const card = $('card');
  const appsEl = $('apps');
  const masterEl = $('master');
  const emptyEl = $('empty');
  const settingsEl = $('settings');
  const settingsBtn = $('settingsBtn');
  const autostartEl = $('autostart');
  const liteEl = $('lite');
  const appCountEl = $('appCount');
  const segmentedEl = document.querySelector('.segmented');
  const segButtons = [...segmentedEl.querySelectorAll('button')];
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
      '<button class="icon-btn mute" title="Silenciar"></button>';

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

    r.nameEl.textContent = name;
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
    r.mute.innerHTML = r.muted ? ICON_MUTE : ICON_VOL;
    r.el.classList.toggle('muted', r.muted);
  }

  function updateRow(r, d, dim) {
    if (d.name && d.name !== r.name) { r.name = d.name; r.nameEl.textContent = d.name; }
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
      master = createRow(MASTER, 'Volume geral');
      masterEl.appendChild(master.el);
    }
    updateRow(master, { volume: s.master.volume, muted: s.master.muted }, false);

    // Apps
    const ids = s.apps.map((a) => a.id);
    for (const [id, r] of [...rows]) {
      if (id !== MASTER && !ids.includes(id)) { r.el.remove(); rows.delete(id); }
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

  function applyPeaks(p) {
    const smooth = (r, raw) => {
      r.peak = raw > r.peak ? raw : r.peak * 0.85;   // sobe rápido, desce suave
      r.meter.style.transform = 'scaleX(' + Math.min(1, r.peak) + ')';
    };
    const m = rows.get(MASTER);
    if (m) smooth(m, p.master);
    for (const [id, r] of rows) if (id !== MASTER) smooth(r, p.apps[id] || 0);
  }

  function applySettings(s) {
    setTheme(s.theme);
    glassSupported = !!s.glass;
    setLite(!!s.lite);
    autostartEl.checked = !!s.autostart;
    settingsEl.classList.add('instant');            // recolhe sem animação (a janela ainda vai aparecer)
    setSettingsOpen(false);                         // sempre abre com as configurações recolhidas
    void settingsEl.offsetHeight;
    settingsEl.classList.remove('instant');
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

  function setTheme(mode) {
    root.dataset.theme = mode;
    segButtons.forEach((b, i) => {
      const on = b.dataset.mode === mode;
      b.setAttribute('aria-checked', String(on));
      if (on) segmentedEl.style.setProperty('--seg-i', i);   // a pílula desliza até aqui
    });
    applyScheme();
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

  segButtons.forEach((b) => b.addEventListener('click', () => {
    setTheme(b.dataset.mode);
    post({ type: 'setTheme', mode: b.dataset.mode });
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
