'use strict';

// ── Состояние ────────────────────────────────────────────────────────────────
let pcs = {};            // pcNumber → объект состояния
let selectedPc = null;   // текущий выбранный pcNumber
let tariff = 3000;
let serviceTypes = [];
let offlinePcNumber = null;  // ПК, по которому ждём решения оффлайн
let connection = null;
let readerCardPrefix = 'FAA';
let sessionFields = { requireReaderId: true, requireUserName: false };
let _readerLookupState = null;  // null | 'not_found' | 'expired' | 'valid'
let _readerLookedUpId = '';
let _readerLookupInFlight = null;  // deduplicate concurrent lookups
let _readerLookupTimer = null;     // debounce timer for auto-lookup on input
let latestClientVersion = '';
let _svcRows = [];

// ── Фильтры и поиск ───────────────────────────────────────────────────────────
let _filterState = 'all';
let _searchQuery = '';

// ── Просмотр экрана ───────────────────────────────────────────────────────────
let _screenPc = null;
let _screenInterval = null;

// ── Инициализация ─────────────────────────────────────────────────────────────
// ── Права оператора ───────────────────────────────────────────────────────────
let opPerms = { canViewReaders: false, canViewFinance: false, canViewStats: false };
let meId = null;

// ── Браузерные уведомления ────────────────────────────────────────────────────
let _notifDuration = parseInt(localStorage.getItem('bibNotifDuration') || '8', 10);

function bibNotify(title, body) {
  if (!('Notification' in window) || Notification.permission !== 'granted') return;
  const n = new Notification(title, { body, icon: '/favicon.ico' });
  n.onclick = () => { window.focus(); n.close(); };
  if (_notifDuration > 0) setTimeout(() => n.close(), _notifDuration * 1000);
}

function opUpdateNotifBtn() {
  const btn = document.getElementById('opNotifBtn');
  if (!btn || !('Notification' in window)) return;
  const p = Notification.permission;
  if (p === 'granted') {
    btn.style.display = 'none'; // уже работает — кнопку прячем
  } else if (p === 'denied') {
    btn.style.display = '';
    btn.title = 'Уведомления заблокированы — разрешите в настройках браузера';
    btn.style.color = '#f87171';
    btn.style.borderColor = '#5D2A2A';
    btn.style.cursor = 'default';
    btn.onclick = null;
  } else {
    btn.style.display = '';
    btn.title = 'Включить браузерные уведомления';
  }
}

async function opRequestNotifications() {
  if (!('Notification' in window) || Notification.permission === 'denied') return;
  await Notification.requestPermission();
  opUpdateNotifBtn();
}

(async function init() {
  // Проверяем авторизацию
  const me = await fetch('/api/op/me').then(r => r.ok ? r.json() : null).catch(() => null);
  if (!me) { window.location.href = '/login.html'; return; }
  document.getElementById('opName').textContent = me.displayName;
  const avaEl = document.getElementById('opAva');
  if (avaEl && me.displayName) avaEl.textContent = me.displayName.charAt(0).toUpperCase();
  meId = me.id;
  initTheme();

  // Применяем права
  opPerms.canViewReaders = !!me.canViewReaders;
  opPerms.canViewFinance = !!me.canViewFinance;
  opPerms.canViewStats   = !!me.canViewStats;
  if (opPerms.canViewReaders) document.getElementById('tabBtnReaders').style.display = '';
  if (opPerms.canViewFinance) document.getElementById('tabBtnFinance').style.display = '';
  if (opPerms.canViewStats)   document.getElementById('tabBtnStats').style.display   = '';

  // Инициализируем дату для аналитики
  const _today = new Date().toISOString().split('T')[0];
  document.getElementById('opAnlDateDay').value     = _today;
  document.getElementById('opAnlDateMonth').value   = _today.substring(0, 7);
  document.getElementById('opAnlYearQuarter').value = _today.substring(0, 4);
  document.getElementById('opAnlDateYear').value    = _today.substring(0, 4);
  opSetQuarter(Math.ceil((new Date().getMonth() + 1) / 3));
  opUpdateNotifBtn();

  // Загружаем настройки полей сессии
  fetch('/api/session-fields')
    .then(r => r.ok ? r.json() : null)
    .then(sf => { if (sf) sessionFields = sf; })
    .catch(() => {});

  // Загружаем последнюю доступную версию BibClient
  fetch('/updates/version.json').then(r => r.ok ? r.json() : null).then(v => {
    if (v?.Version) { latestClientVersion = v.Version; renderGrid(); }
  }).catch(() => {});

  startSignalR();

  // Таймеры сессий
  setInterval(tickTimers, 1000);

  // Часы в шапке
  (function tickClock() {
    const el = document.getElementById('topClock');
    if (el) {
      const n = new Date();
      el.textContent = String(n.getHours()).padStart(2,'0') + ':' + String(n.getMinutes()).padStart(2,'0') + ':' + String(n.getSeconds()).padStart(2,'0');
    }
    setTimeout(tickClock, 1000);
  })();

  // Закрываем меню темы при клике вне него
  document.addEventListener('click', e => {
    const wrap = document.getElementById('themeWrap');
    if (wrap && !wrap.contains(e.target)) {
      const menu = document.getElementById('themeMenu');
      if (menu) menu.style.display = 'none';
    }
  });
})();

// ── SignalR ───────────────────────────────────────────────────────────────────
function startSignalR() {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/webhub')
    .withAutomaticReconnect([2000, 5000, 10000, 30000, 60000, 60000, 60000])
    .build();

  connection.on('stateSnapshot', list => {
    pcs = {};
    list.forEach(pc => { pcs[pc.pcNumber] = pc; });
    renderGrid();
    updateStats();
  });

  connection.on('pcUpdated', pc => {
    pcs[pc.pcNumber] = pc;
    renderCard(pc.pcNumber);
    updateStats();
    if (selectedPc === pc.pcNumber) renderActionBar();
  });

  connection.on('allPcsUpdated', list => {
    pcs = {};
    list.forEach(pc => { pcs[pc.pcNumber] = pc; });
    renderGrid();
    updateStats();
  });

  connection.on('tariff', t => { tariff = t; });
  connection.on('serviceTypes', list => { serviceTypes = list; });
  connection.on('readerCardPrefix', p => { readerCardPrefix = p || 'FAA'; });
  connection.on('sessionFields', sf => {
    sessionFields = sf;
    // Применить к открытому диалогу сессии если он открыт
    const rowReader = document.getElementById('rowReaderId');
    if (rowReader) rowReader.style.display = sf.requireReaderId ? '' : 'none';
    const rowName = document.getElementById('rowUserName');
    if (rowName) rowName.style.display = sf.requireUserName ? '' : 'none';
  });

  connection.on('offlineAlert', data => {
    offlinePcNumber = data.pcNumber;
    const pc = pcs[data.pcNumber] || {};
    document.getElementById('dlgOfflineBody').innerHTML =
      `<div class="summary-row"><span>ПК</span><span class="val">${esc(data.pcNumber)}</span></div>
       <div class="summary-row"><span>Тип</span><span class="val">${esc(data.sessionType)}</span></div>
       <div class="summary-row"><span>Время в сессии</span><span class="val">${fmtTime(data.elapsed)}</span></div>`;
    openDlg('dlgOffline');
    bibNotify(`⚠️ ${data.pcNumber} — потеря связи`, `Сессия ${data.sessionType} · ${fmtTime(data.elapsed)}`);
  });

  connection.on('offlineResolved', data => {
    if (offlinePcNumber === data.pcNumber) {
      offlinePcNumber = null;
      closeDlg('dlgOffline');
      toast(`Решение по ${data.pcNumber}: ${data.decision === 'Pause' ? 'пауза' : 'продолжить'}`, 'good');
    }
  });

  connection.on('serverRestarting', data => {
    showRestartOverlay(data.reason || 'Обновление системы');
    bibNotify('🔄 Обновление сервера', 'Сервер перезапускается. После обновления войдите в систему снова.');
  });

  connection.on('permissionsUpdated', async data => {
    if (data.operatorId !== meId) return;
    const fresh = await fetch('/api/op/me').then(r => r.ok ? r.json() : null).catch(() => null);
    if (!fresh) return;
    opPerms.canViewReaders = !!fresh.canViewReaders;
    opPerms.canViewFinance = !!fresh.canViewFinance;
    opPerms.canViewStats   = !!fresh.canViewStats;
    document.getElementById('tabBtnReaders').style.display = opPerms.canViewReaders ? '' : 'none';
    document.getElementById('tabBtnFinance').style.display = opPerms.canViewFinance ? '' : 'none';
    document.getElementById('tabBtnStats').style.display   = opPerms.canViewStats   ? '' : 'none';
    // Если текущая вкладка стала недоступна — возвращаемся на ПК
    if (_currentOpTab === 'readers' && !opPerms.canViewReaders) switchOpTab('pcs');
    if (_currentOpTab === 'finance' && !opPerms.canViewFinance) switchOpTab('pcs');
    if (_currentOpTab === 'stats'   && !opPerms.canViewStats)   switchOpTab('pcs');
    toast('Права доступа обновлены', 'good');
  });

  connection.on('sessionSummary', s => {
    const isManual = _opManuallyEndedPcs.has(s.pcNumber);
    // Не удаляем из _opManuallyEndedPcs здесь — sessionEndedByStaff обработает это
    showSessionSummary(s);
    if (!isManual) {
      const name = s.userName || s.readerId || 'Анонимный';
      bibNotify(`✅ ${s.pcNumber} — сессия завершена`,
        `${name} · ${fmtTime(s.duration)} · ${fmt(s.earned)} сум`);
    }
  });

  connection.on('sessionEndedByStaff', data => {
    if (_opManuallyEndedPcs.has(data.pcNumber)) {
      _opManuallyEndedPcs.delete(data.pcNumber);
      return; // сами завершили — не уведомляем
    }
    const name = data.userName || 'Анонимный';
    const h = Math.floor(data.durationSeconds / 3600);
    const m = Math.floor((data.durationSeconds % 3600) / 60);
    bibNotify(`✅ ${data.pcNumber} — сессия завершена`,
      `${name} · ${h}ч ${m}м · ${(data.earned || 0).toLocaleString('ru-RU')} сум`);
  });

  connection.on('serviceCreated', s => {
    toast(`Услуга "${s.serviceName}" создана. Сумма: ${fmt(s.total)} сум${s.isPaid ? '' : ' (отложено)'}`, 'good');
  });

  connection.onreconnecting(() => {
    setDot(false);
    toast('Переподключение к серверу...', '');
  });
  connection.onreconnected(async () => {
    setDot(true);
    toast('Связь восстановлена', 'good');
    try { await connection.invoke('RequestSnapshot'); } catch (e) { console.warn('snapshot error', e); }
  });
  connection.onclose(() => {
    setDot(false);
    showRestartOverlay('Сервер недоступен');
    waitForServerAndReload();
  });

  connection.start()
    .then(() => setDot(true))
    .catch(err => { setDot(false); console.error('SignalR error:', err); showRestartOverlay('Сервер недоступен'); waitForServerAndReload(); });
}

function showRestartOverlay(reason) {
  const overlay = document.getElementById('overlayRestart');
  document.getElementById('overlayRestartReason').textContent = reason;
  overlay.style.display = 'flex';
}

function waitForServerAndReload() {
  const interval = setInterval(async () => {
    try {
      // Используем публичный эндпоинт — токен оператора теряется при рестарте
      // сервера (хранится в памяти), поэтому /api/op/me вернёт 401 и цикл
      // никогда не завершится. /api/session-fields не требует авторизации.
      const r = await fetch('/api/session-fields', { cache: 'no-store' });
      if (r.ok) { clearInterval(interval); window.location.reload(); }
    } catch (e) { /* сервер ещё не поднялся */ }
  }, 3000);
}

function setDot(online) {
  const d = document.getElementById('connDot');
  if (!d) return;
  d.classList.toggle('offline', !online);
  d.title = online ? 'Подключено' : 'Нет связи с сервером';
}

// ── Рендер грида ──────────────────────────────────────────────────────────────
function renderGrid() {
  const grid = document.getElementById('grid');
  const keys = Object.keys(pcs).sort((a, b) => pcs[a].pcNumberValue - pcs[b].pcNumberValue);
  grid.querySelectorAll('.pccard').forEach(el => {
    if (!pcs[el.dataset.pc]) el.remove();
  });
  keys.forEach(pcNumber => renderCard(pcNumber));
  _filterCards();
}

function renderCard(pcNumber) {
  const pc = pcs[pcNumber];
  if (!pc) return;
  const grid = document.getElementById('grid');
  let card = grid.querySelector(`[data-pc="${CSS.escape(pcNumber)}"]`);
  if (!card) {
    card = document.createElement('div');
    card.dataset.pc = pcNumber;
    card.addEventListener('click', () => selectPc(pcNumber));
    const keys = Object.keys(pcs).sort((a, b) => pcs[a].pcNumberValue - pcs[b].pcNumberValue);
    const idx = keys.indexOf(pcNumber);
    const cards = grid.querySelectorAll('.pccard');
    if (idx >= cards.length) grid.appendChild(card);
    else grid.insertBefore(card, cards[idx]);
  }
  const isSelected = selectedPc === pcNumber;
  const isLow = pc.sessionType === 'Лимит' && pc.limitSeconds > 0 && Math.max(0, pc.limitSeconds - pc.elapsedSeconds) <= 300;
  card.className = 'pccard' + (isSelected ? ' is-selected' : '') + (!pc.isOnline ? ' is-offline' : '') + (isLow ? ' is-low' : '');
  card.style.setProperty('--st', getStatusColor(pc));
  card.innerHTML = buildCardHtml(pc);
}

function buildCardHtml(pc) {
  const n = pc.pcNumber;
  const stKey = _stKey(pc);

  const badge = `<span class="badge" style="color:var(--${stKey});background:var(--${stKey}-bg);border-color:var(--${stKey}-ring)"><span class="dot" style="background:var(--${stKey})"></span>${esc(getStatusLabel(pc))}</span>`;

  const head = `<div class="pccard-stripe"></div>
  <div class="pccard-head">
    <div class="pccard-title">
      <span class="pccard-name">${esc(n)}</span>
      ${pc.ip ? `<span class="pccard-ip">${esc(pc.ip)}</span>` : ''}
    </div>
    ${badge}
  </div>`;

  if (pc.isSession) {
    const elapsed = pc.elapsedSeconds || 0;
    const limit = pc.limitSeconds || 0;
    const rem = Math.max(0, limit - elapsed);
    const prog = limit > 0 ? Math.min(100, Math.round(elapsed / limit * 100)) : 0;
    const isLow = pc.sessionType === 'Лимит' && limit > 0 && rem <= 300;
    const cost = pc.sessionType === 'VIP' ? Math.floor(elapsed * tariff / 3600) : (pc.paidAmount || 0);
    const nameLabel = pc.userName || pc.readerId || '';
    const tariffChip = pc.sessionType === 'VIP'
      ? `<span class="tariff-chip tariff-vip">VIP</span>`
      : `<span class="tariff-chip tariff-limit">Лимит</span>`;
    const clientBadge = pc.clientVersion && latestClientVersion && pc.clientVersion !== latestClientVersion
      ? `<span title="Обновление v${esc(latestClientVersion)}" style="font-size:10px;color:var(--warn)">⬆v${esc(pc.clientVersion)}</span>` : '';

    const limMetaBlock = pc.sessionType === 'Лимит' && limit > 0
      ? `<div class="sess-progress"><span data-pc-prog="${esc(n)}" style="width:${prog}%;background:${isLow ? 'var(--locked)' : 'var(--limit)'}"></span></div>
         <div class="sess-meta">
           <span class="sess-meta-item" style="${isLow ? 'color:var(--locked)' : ''}">
             <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
             <span data-pc-rem="${esc(n)}">${fmtTime(rem)}</span>
           </span>
           <span class="sess-paid mono">${fmt(pc.paidAmount || 0)} сум</span>
         </div>`
      : `<div class="sess-meta">
           <span class="sess-open-tag"><svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><path d="M18.36 6.64a9 9 0 1 1-12.73 0"/><line x1="12" y1="2" x2="12" y2="12"/></svg>Открытая</span>
           <span class="sess-cost mono" data-pc-cost="${esc(n)}">${fmt(cost)} сум</span>
         </div>`;

    return head + `<div class="pccard-body">
      ${nameLabel ? `<div class="sess-user"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg><span class="sess-user-name">${esc(nameLabel)}</span>${tariffChip}${clientBadge}</div>` : `<div style="margin-bottom:4px">${tariffChip}${clientBadge}</div>`}
      <div class="sess-timer">
        <span class="${isLow ? 'sess-clock low' : 'sess-clock'} mono" data-pc-clock="${esc(n)}">${fmtTime(elapsed)}</span>
        <span class="sess-clock-cap">${pc.isPaused ? 'пауза' : 'прошло'}</span>
      </div>
      ${limMetaBlock}
    </div>`;
  }

  // Свободен / оффлайн / заблокирован
  let stMark = 'state-mark', icon = '';
  if (pc.isFree) {
    stMark += ' free';
    icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>`;
  } else if (!pc.isOnline) {
    icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><line x1="1" y1="1" x2="23" y2="23"/><path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55"/><path d="M5 12.55a10.94 10.94 0 0 1 5.17-2.39"/><path d="M10.71 5.05A16 16 0 0 1 22.56 9"/><path d="M1.42 9a15.91 15.91 0 0 1 4.7-2.88"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><line x1="12" y1="20" x2="12.01" y2="20"/></svg>`;
  } else {
    stMark += ' locked';
    icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>`;
  }

  return head + `<div class="pccard-body pccard-body-state">
    <div class="${stMark}">${icon}</div>
    <span class="state-text">${esc(getStatusLabel(pc))}</span>
  </div>`;
}

// ── Тики таймеров (локальный инкремент) ───────────────────────────────────────
function tickTimers() {
  Object.values(pcs).forEach(pc => {
    if (!pc.isSession || pc.isPaused) return;
    pc.elapsedSeconds += 1;
    const n = CSS.escape(pc.pcNumber);
    const elapsed = pc.elapsedSeconds;

    const clockEl = document.querySelector(`[data-pc-clock="${n}"]`);
    if (clockEl) clockEl.textContent = fmtTime(elapsed);

    if (pc.sessionType === 'VIP') {
      const costEl = document.querySelector(`[data-pc-cost="${n}"]`);
      if (costEl) costEl.textContent = fmt(Math.floor(elapsed * tariff / 3600)) + ' сум';
    }

    if (pc.sessionType === 'Лимит' && pc.limitSeconds > 0) {
      const rem = Math.max(0, pc.limitSeconds - elapsed);
      const prog = Math.min(100, Math.round(elapsed / pc.limitSeconds * 100));
      const isLow = rem <= 300;

      const remEl = document.querySelector(`[data-pc-rem="${n}"]`);
      if (remEl) remEl.textContent = fmtTime(rem);

      const progEl = document.querySelector(`[data-pc-prog="${n}"]`);
      if (progEl) {
        progEl.style.width = prog + '%';
        progEl.style.background = isLow ? 'var(--locked)' : 'var(--limit)';
      }

      // Обновляем класс карточки (is-low)
      const card = document.querySelector(`[data-pc="${CSS.escape(pc.pcNumber)}"]`);
      if (card) card.classList.toggle('is-low', isLow);
    }
  });
}

// ── Статистика ────────────────────────────────────────────────────────────────
function updateStats() {
  const vals = Object.values(pcs);
  const cAll     = vals.length;
  const cFree    = vals.filter(p => p.isFree).length;
  const cSession = vals.filter(p => p.isSession).length;
  const cOffline = vals.filter(p => !p.isOnline).length;
  const setN = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  setN('chipAllN', cAll);
  setN('chipFreeN', cFree);
  setN('chipSessionN', cSession);
  setN('chipOfflineN', cOffline);
  _filterCards();
}

// ── Выбор ПК ──────────────────────────────────────────────────────────────────
function selectPc(pcNumber) {
  if (selectedPc === pcNumber) { deselectPc(); return; }
  selectedPc = pcNumber;
  document.querySelectorAll('.pccard.is-selected').forEach(c => c.classList.remove('is-selected'));
  const card = document.querySelector(`[data-pc="${CSS.escape(pcNumber)}"]`);
  if (card) {
    card.classList.add('is-selected');
    const pc = pcs[pcNumber];
    if (pc) card.style.setProperty('--st', getStatusColor(pc));
  }
  renderActionBar();
}

function deselectPc() {
  selectedPc = null;
  document.querySelectorAll('.pccard.is-selected').forEach(c => c.classList.remove('is-selected'));
  const bar = document.getElementById('bottomBar');
  if (bar) bar.style.display = 'none';
}

function renderActionBar() {
  const bar = document.getElementById('bottomBar');
  const pc = pcs[selectedPc];
  if (!pc) { if (bar) bar.style.display = 'none'; return; }

  const stColor = getStatusColor(pc);
  bar.style.setProperty('--st', stColor);

  const stKey = _stKey(pc);
  const badgeLabel = pc.isSession
    ? (pc.sessionType === 'VIP' ? 'VIP' : 'Лимит')
    : getStatusLabel(pc);

  const ico = (path, w = 14) => `<svg width="${w}" height="${w}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${path}</svg>`;

  let btns = '';
  if (pc.isOnline) {
    btns += `<button class="abtn" onclick="openScreenView('${esc(pc.pcNumber)}')">${ico('<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/>')}Экран</button>`;
  }
  if (pc.isOnline && !pc.isSession) {
    btns += `<button class="abtn abtn-accent" onclick="openSessionDlg()">${ico('<polygon points="5 3 19 12 5 21 5 3"/>')}Начать сессию</button>`;
  }
  if (pc.isSession) {
    btns += `<button class="abtn" onclick="doTogglePause()">${pc.isPaused ? ico('<polygon points="5 3 19 12 5 21 5 3"/>') : ico('<rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/>')}${pc.isPaused ? 'Продолжить' : 'Пауза'}</button>`;
    btns += `<button class="abtn" onclick="openTransferDlg()">${ico('<polyline points="17 1 21 5 17 9"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><polyline points="7 23 3 19 7 15"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/>')}Пересадить</button>`;
    if (pc.sessionType === 'Лимит') {
      btns += `<button class="abtn" onclick="openExtendDlg()">${ico('<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/><line x1="12" y1="22" x2="12" y2="22.01"/>')}+Время</button>`;
      btns += `<button class="abtn" onclick="openSubtractDlg()">−Время</button>`;
    }
    btns += `<button class="abtn" onclick="openPenaltyDlg()">${ico('<path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>')}Штраф</button>`;
    btns += `<button class="abtn abtn-danger" onclick="doEndSession()">${ico('<rect x="3" y="3" width="18" height="18" rx="2" ry="2"/>')}Завершить</button>`;
  }

  bar.innerHTML = `
    <div class="bb-left">
      <div class="bb-stripe"></div>
      <div class="bb-id">
        <span class="bb-name">${esc(pc.pcNumber)}</span>
        ${pc.ip ? `<span class="bb-ip">${esc(pc.ip)}</span>` : ''}
      </div>
      <span class="badge" style="color:var(--${stKey});background:rgba(255,255,255,.08);border-color:rgba(255,255,255,.14)"><span class="dot" style="background:var(--${stKey})"></span>${esc(badgeLabel)}</span>
    </div>
    <div class="bb-actions">${btns}</div>
    <button class="bb-close" onclick="deselectPc()">${ico('<line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>', 15)}</button>`;
  bar.style.display = 'flex';
}

// ── Действия ──────────────────────────────────────────────────────────────────
function parseRegDate(dateStr) {
  if (!dateStr) return null;
  const p = dateStr.split('-');
  if (p.length !== 3) return null;
  const d = new Date(+p[2], +p[1] - 1, +p[0]);
  return isNaN(d) ? null : d;
}

// Вызывается из oninput поля читательского билета — фильтрует цифры + debounce поиск
function onReaderInput() {
  const el = document.getElementById('dlgReaderId');
  el.value = el.value.replace(/\D/g, '').slice(0, 9);
  _readerLookupState = null;
  clearTimeout(_readerLookupTimer);
  const nums = el.value;
  if (nums.length >= 6) {
    _readerLookupTimer = setTimeout(lookupReader, 500);
  } else {
    document.getElementById('dlgReaderInfo').style.display = 'none';
    document.getElementById('dlgUserName').value = '';
  }
}

function onCardTypeChanged(type) {
  const isTemp = type === 'temp';
  const rbReg = document.getElementById('rbCardRegular');
  const rbTmp = document.getElementById('rbCardTemp');
  if (rbReg) rbReg.checked = !isTemp;
  if (rbTmp) rbTmp.checked = isTemp;
  document.getElementById('cardTypeBtnRegular')?.classList.toggle('on', !isTemp);
  document.getElementById('cardTypeBtnTemp')?.classList.toggle('on', isTemp);
  const prefix = document.getElementById('dlgReaderPrefix');
  if (prefix) prefix.textContent = isTemp ? '№' : readerCardPrefix;
  const rowName = document.getElementById('rowUserName');
  if (rowName) rowName.style.display = (isTemp || !sessionFields.requireUserName) ? 'none' : '';
  _readerLookupState = null;
  _readerLookedUpId = '';
  document.getElementById('dlgReaderId').value = '';
  document.getElementById('dlgReaderInfo').style.display = 'none';
  document.getElementById('dlgUserName').value = '';
  document.getElementById('dlgReaderId').placeholder = isTemp ? '842' : '260500456';
}

function openSessionDlg() {
  if (!selectedPc) return;
  document.getElementById('dlgSessionPc').textContent = selectedPc;
  document.getElementById('dlgLimitHours').value = 1;
  document.getElementById('dlgLimitMins').value  = 0;
  document.getElementById('dlgAmount').value = tariff;
  document.getElementById('dlgUserName').value = '';
  document.getElementById('dlgReaderId').value = '';
  document.getElementById('dlgReaderId').placeholder = '260500456';
  document.getElementById('dlgReaderPrefix').textContent = readerCardPrefix;
  const infoEl = document.getElementById('dlgReaderInfo');
  infoEl.style.display = 'none';
  infoEl.textContent = '';
  // Reset card type to regular
  onCardTypeChanged('regular');
  _readerLookupState = null;
  _readerLookedUpId = '';
  // Reset session type seg-opt
  document.getElementById('segLimit')?.classList.add('on');
  document.getElementById('segVip')?.classList.remove('on');
  const limitRadio = document.querySelector('[name="stype"][value="Лимит"]');
  if (limitRadio) limitRadio.checked = true;
  document.getElementById('limitFields').style.display = '';

  // Показываем/скрываем поле читательского билета согласно настройкам
  const reqReader = !!sessionFields.requireReaderId;
  const rowReader = document.getElementById('rowReaderId');
  if (rowReader) rowReader.style.display = reqReader ? '' : 'none';

  // Показываем/скрываем имя согласно настройкам
  const reqName = !!sessionFields.requireUserName;
  const rowName = document.getElementById('rowUserName');
  if (rowName) rowName.style.display = reqName ? '' : 'none';
  const lblName = document.getElementById('lblUserName');
  if (lblName) lblName.innerHTML = reqName ? 'Имя *' : 'Имя читателя <span style="font-weight:500;color:var(--ink-3)">(заполняется автоматически)</span>';

  openDlg('dlgSession');
}

function calcAmount() {
  const h = parseInt(document.getElementById('dlgLimitHours').value) || 0;
  const mins = h * 60 + (parseInt(document.getElementById('dlgLimitMins').value) || 0);
  document.getElementById('dlgAmount').value = Math.round(tariff * mins / 60);
}
function calcTime() {
  const amount = parseInt(document.getElementById('dlgAmount').value) || 0;
  const totalMins = Math.round(amount / tariff * 60);
  document.getElementById('dlgLimitHours').value = Math.floor(totalMins / 60);
  document.getElementById('dlgLimitMins').value  = totalMins % 60;
}

async function confirmStartSession() {
  const sessionType = document.querySelector('[name="stype"]:checked')?.value || 'Лимит';
  const h           = parseInt(document.getElementById('dlgLimitHours').value) || 0;
  const limitMin    = h * 60 + (parseInt(document.getElementById('dlgLimitMins').value) || 0);
  const paidAmount  = parseInt(document.getElementById('dlgAmount').value) || 0;
  const isTemp     = document.querySelector('[name="cardType"]:checked')?.value === 'temp';
  const readerNums = document.getElementById('dlgReaderId').value.trim();

  const readerId = isTemp ? readerNums : (readerCardPrefix + readerNums);

  if (sessionFields.requireReaderId) {
    if (!readerNums) { toast('Введите номер читательского билета', 'warn'); return; }
    if (!isTemp) {
      if (_readerLookupState === null || _readerLookedUpId !== readerId) await lookupReader();
      if (_readerLookupState === 'not_found') { toast('Читатель не найден в базе', 'warn'); return; }
      if (_readerLookupState === 'expired')   { toast('Читательский билет просрочен', 'warn'); return; }
      if (_readerLookupState !== 'valid')     { toast('Проверьте номер читательского билета', 'warn'); return; }
    }
  }

  const userName = document.getElementById('dlgUserName').value.trim();
  if (!!sessionFields.requireUserName && !userName) { toast('Введите имя пользователя', 'warn'); return; }

  closeDlg('dlgSession');
  try {
    await connection.invoke('StartSession', selectedPc, sessionType,
      sessionType === 'Лимит' ? limitMin * 60 : 0,
      sessionType === 'Лимит' ? paidAmount : 0,
      userName, readerId);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

// Deduplication wrapper — prevents two concurrent lookups (blur + button click)
async function opQuickAddReader(cardId) {
  const infoEl = document.getElementById('dlgReaderInfo');
  infoEl.innerHTML = `<span style="color:#aaa">Добавление…</span>`;
  try {
    const r = await fetch('/api/op/readers/quick-add', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cardId })
    });
    if (r.ok) {
      _readerLookupState = 'valid';
      _readerLookedUpId  = cardId;
      infoEl.style.cssText = 'display:block;margin-top:6px;padding:7px 10px;border-radius:6px;font-size:12px;background:#1A2D1A;color:#6EE7B7;border:1px solid #2A5D2A';
      infoEl.textContent = `✓ ${cardId} — добавлен как новый читатель`;
      toast('Читатель добавлен', 'success');
    } else {
      toast('Ошибка добавления', 'warn');
    }
  } catch { toast('Ошибка добавления', 'warn'); }
}

async function lookupReader() {
  if (_readerLookupInFlight) { await _readerLookupInFlight; return; }
  // Не перезапускать поиск если результат уже известен для этого ID
  // (иначе onblur перезаписывает кнопку «Добавить» и первый клик промахивается)
  const nums = document.getElementById('dlgReaderId').value.trim();
  const prefix = readerCardPrefix || 'FAA';
  const isTemp = document.querySelector('[name="cardType"]:checked')?.value === 'temp';
  const currentId = isTemp ? nums : (prefix + nums);
  if (_readerLookupState !== null && _readerLookedUpId === currentId) return;
  _readerLookupInFlight = _lookupReaderImpl();
  try { await _readerLookupInFlight; } finally { _readerLookupInFlight = null; }
}

async function _lookupReaderImpl() {
  const nums   = document.getElementById('dlgReaderId').value.trim();
  const infoEl = document.getElementById('dlgReaderInfo');
  if (!nums) { infoEl.style.display = 'none'; _readerLookupState = null; return; }

  const isTemp = document.querySelector('[name="cardType"]:checked')?.value === 'temp';

  if (isTemp) {
    _readerLookupState = 'valid';
    _readerLookedUpId = nums;
    infoEl.className = 'reader-info valid';
    infoEl.style.display = '';
    infoEl.textContent = `✓ Временный билет №${nums} — посещение будет зафиксировано`;
    return;
  }

  const cardId = readerCardPrefix + nums;
  _readerLookedUpId = cardId;

  try {
    const r = await fetch(`/api/readers/lookup/${encodeURIComponent(cardId)}`);
    if (!r.ok) {
      _readerLookupState = 'not_found';
      document.getElementById('dlgUserName').value = '';
      infoEl.className = 'reader-info invalid';
      infoEl.style.display = '';
      infoEl.style.cssText = '';
      Object.assign(infoEl.style, { display:'flex', alignItems:'center', gap:'10px', marginTop:'6px', padding:'7px 10px', borderRadius:'8px', fontSize:'12px', background:'var(--locked-bg)', color:'var(--locked)', border:'1px solid var(--locked-ring)' });
      infoEl.innerHTML = `<span style="flex:1">✗ Читатель ${esc(cardId)} не найден в базе</span>
        <button data-quick-add="${esc(cardId)}"
          style="padding:3px 10px;font-size:11px;border-radius:6px;cursor:pointer;background:var(--free-bg);color:var(--free);border:1px solid var(--free-ring);white-space:nowrap">
          + Добавить
        </button>`;
      infoEl.querySelector('[data-quick-add]').addEventListener('click', function() {
        opQuickAddReader(this.dataset.quickAdd);
      });
      return;
    }
    const data = await r.json();

    // Check expiry
    const regDate = parseRegDate(data.registeredAt);
    if (regDate) {
      let expired = false;
      let expiredMsg = '';
      if (isTemp) {
        // Временный билет действителен только в день выдачи
        const today = new Date();
        const isToday = regDate.getFullYear() === today.getFullYear()
                     && regDate.getMonth()    === today.getMonth()
                     && regDate.getDate()     === today.getDate();
        if (!isToday) {
          expired = true;
          expiredMsg = `⚠ ${data.fullName} · Временный билет выдан ${regDate.toLocaleDateString('ru-RU')}, действителен только в день выдачи`;
        }
      } else {
        const updDate = parseRegDate(data.updatedAt);
        const baseDate = (updDate && updDate > regDate) ? updDate : regDate;
        const daysSince = (Date.now() - baseDate) / 86400000;
        if (daysSince > 3 * 365 + 1) {
          const expDate = new Date(baseDate);
          expDate.setFullYear(expDate.getFullYear() + 3);
          expired = true;
          expiredMsg = `⚠ ${data.fullName} · Билет просрочен с ${expDate.toLocaleDateString('ru-RU')}`;
        }
      }
      if (expired) {
        _readerLookupState = 'expired';
        document.getElementById('dlgUserName').value = data.fullName || '';
        infoEl.className = 'reader-info expired';
        infoEl.style.display = '';
        infoEl.textContent = expiredMsg;
        return;
      }
    }

    _readerLookupState = 'valid';
    document.getElementById('dlgUserName').value = data.fullName || '';

    const expDate = regDate ? new Date(regDate) : null;
    if (expDate) {
      if (isTemp) expDate.setDate(expDate.getDate() + 3);
      else        expDate.setFullYear(expDate.getFullYear() + 3);
    }
    const parts = [
      data.fullName,
      data.category,
      data.gender,
      data.age ? `${data.age} лет` : null,
      expDate ? `до ${expDate.toLocaleDateString('ru-RU')}` : null
    ].filter(Boolean);
    infoEl.style.cssText = 'display:block;margin-top:6px;padding:7px 10px;border-radius:6px;font-size:12px;background:#1A2D1A;color:#1D9E75;border:1px solid #1D5D1D';
    infoEl.textContent = '✓ ' + parts.join(' · ');
  } catch {
    infoEl.style.display = 'none';
    _readerLookupState = null;
  }
}

const _opManuallyEndedPcs = new Set();

async function doEndSession() {
  if (!selectedPc) return;
  _opManuallyEndedPcs.add(selectedPc);
  try {
    await connection.invoke('EndSession', selectedPc);
  } catch (e) { _opManuallyEndedPcs.delete(selectedPc); toast('Ошибка: ' + e, 'warn'); }
}

async function doTogglePause() {
  if (!selectedPc) return;
  try {
    await connection.invoke('TogglePause', selectedPc);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

// ── Extend session ────────────────────────────────────────────────────────────
let _extSyncing = false;

function openExtendDlg() {
  if (!selectedPc) return;
  document.getElementById('dlgExtPc').textContent = selectedPc;
  document.getElementById('dlgExtHours').value = 0;
  document.getElementById('dlgExtMins').value  = 30;
  document.getElementById('dlgExtAmount').value = tariff ? Math.round(tariff * 30 / 60) : 0;
  openDlg('dlgExtend');
}

function calcExtAmount() {
  if (_extSyncing || !tariff) return;
  _extSyncing = true;
  const h = parseInt(document.getElementById('dlgExtHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgExtMins').value) || 0);
  document.getElementById('dlgExtAmount').value = Math.round(tariff * min / 60);
  _extSyncing = false;
}

function calcExtTime() {
  if (_extSyncing || !tariff) return;
  _extSyncing = true;
  const amount = parseInt(document.getElementById('dlgExtAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / tariff) || 0;
  document.getElementById('dlgExtHours').value = Math.floor(totalMins / 60);
  document.getElementById('dlgExtMins').value  = totalMins % 60;
  _extSyncing = false;
}

async function confirmExtend() {
  const h = parseInt(document.getElementById('dlgExtHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgExtMins').value) || 0);
  const amount = parseInt(document.getElementById('dlgExtAmount').value) || 0;
  if (min <= 0) { toast('Укажите время', 'warn'); return; }
  closeDlg('dlgExtend');
  try {
    await connection.invoke('ExtendSession', selectedPc, min * 60, amount);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

// ── Subtract time ─────────────────────────────────────────────────────────────
let _subSyncing = false;

function openSubtractDlg() {
  if (!selectedPc) return;
  document.getElementById('dlgSubPc').textContent = selectedPc;
  document.getElementById('dlgSubHours').value = 0;
  document.getElementById('dlgSubMins').value  = 10;
  document.getElementById('dlgSubAmount').value = tariff ? Math.round(tariff * 10 / 60) : 0;
  openDlg('dlgSubtract');
}

function calcSubAmount() {
  if (_subSyncing || !tariff) return;
  _subSyncing = true;
  const h = parseInt(document.getElementById('dlgSubHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgSubMins').value) || 0);
  document.getElementById('dlgSubAmount').value = Math.round(tariff * min / 60);
  _subSyncing = false;
}

function calcSubTime() {
  if (_subSyncing || !tariff) return;
  _subSyncing = true;
  const amount = parseInt(document.getElementById('dlgSubAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / tariff) || 0;
  document.getElementById('dlgSubHours').value = Math.floor(totalMins / 60);
  document.getElementById('dlgSubMins').value  = totalMins % 60;
  _subSyncing = false;
}

async function confirmSubtract() {
  const h = parseInt(document.getElementById('dlgSubHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgSubMins').value) || 0);
  const amount = parseInt(document.getElementById('dlgSubAmount').value) || 0;
  if (min <= 0) { toast('Укажите время', 'warn'); return; }
  closeDlg('dlgSubtract');
  try {
    await connection.invoke('SubtractTime', selectedPc, min * 60, amount);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

let _penSyncing = false;

function openPenaltyDlg() {
  if (!selectedPc) return;
  const pc = pcs[selectedPc];
  const isVip = pc?.sessionType === 'VIP';
  document.getElementById('dlgPenPc').textContent = selectedPc;
  document.getElementById('penTimeRow').style.display = isVip ? 'none' : '';
  document.getElementById('dlgPenHours').value = 0;
  document.getElementById('dlgPenMins').value = 10;
  document.getElementById('dlgPenAmount').value = (!isVip && tariff) ? Math.round(tariff * 10 / 60) : 0;
  openDlg('dlgPenalty');
}

function calcPenAmount() {
  if (_penSyncing || !tariff) return;
  const pc = pcs[selectedPc];
  if (pc?.sessionType === 'VIP') return;
  _penSyncing = true;
  const h = parseInt(document.getElementById('dlgPenHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgPenMins').value) || 0);
  document.getElementById('dlgPenAmount').value = Math.round(tariff * min / 60);
  _penSyncing = false;
}

function calcPenTime() {
  if (_penSyncing || !tariff) return;
  const pc = pcs[selectedPc];
  if (pc?.sessionType === 'VIP') return;
  _penSyncing = true;
  const amount = parseInt(document.getElementById('dlgPenAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / tariff) || 0;
  document.getElementById('dlgPenHours').value = Math.floor(totalMins / 60);
  document.getElementById('dlgPenMins').value = totalMins % 60;
  _penSyncing = false;
}

async function confirmPenalty() {
  const pc = pcs[selectedPc];
  const isVip = pc?.sessionType === 'VIP';
  const h = isVip ? 0 : (parseInt(document.getElementById('dlgPenHours').value) || 0);
  const min = isVip ? 0 : (h * 60 + (parseInt(document.getElementById('dlgPenMins').value) || 0));
  const amount = parseInt(document.getElementById('dlgPenAmount').value) || 0;
  if (!isVip && min <= 0) { toast('Укажите время штрафа', 'warn'); return; }
  if (isVip && amount <= 0) { toast('Укажите сумму штрафа', 'warn'); return; }
  closeDlg('dlgPenalty');
  try {
    await connection.invoke('ApplyPenalty', selectedPc, min * 60, amount);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

// ── Управление всеми ПК ───────────────────────────────────────────────────────
async function shutdownAll() {
  if (!confirm('Выключить все ПК?')) return;
  try {
    await connection.invoke('ShutdownAll');
    toast('Команда выключения отправлена всем ПК');
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

async function restartAll() {
  if (!confirm('Перезагрузить все ПК?')) return;
  try {
    await connection.invoke('RestartAll');
    toast('Команда перезагрузки отправлена всем ПК');
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

async function resolveOffline(decision) {
  if (!offlinePcNumber) return;
  closeDlg('dlgOffline');
  try {
    await connection.invoke('ResolveOffline', offlinePcNumber, decision);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
  offlinePcNumber = null;
}

async function openTransferDlg() {
  if (!selectedPc) return;
  const errEl = document.getElementById('dlgTransferError');
  errEl.style.display = 'none';

  let targets;
  try {
    targets = await connection.invoke('GetTransferTargets', selectedPc);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); return; }

  if (!targets || targets.length === 0) {
    toast('Нет доступных ПК для пересадки (нужен свободный онлайн-ПК)', 'warn');
    return;
  }

  document.getElementById('dlgTransferFrom').textContent = `Сессия с: ${selectedPc}`;
  const sortedTargets = targets.sort((a, b) => a.pcNumberValue - b.pcNumberValue);
  const sel = document.getElementById('dlgTransferTarget');
  sel.innerHTML = sortedTargets.map(t => `<option value="${esc(t.pcNumber)}">${esc(t.pcNumber)}</option>`).join('');
  const grid = document.getElementById('dlgTransferGrid');
  if (grid) {
    grid.innerHTML = sortedTargets.map(t =>
      `<button class="move-opt" onclick="selectTransferTarget('${esc(t.pcNumber)}')" data-target="${esc(t.pcNumber)}">
        <span class="move-dot"></span>
        <span class="move-name">${esc(t.pcNumber)}</span>
        <span class="move-ip">${esc(t.ip || '')}</span>
      </button>`).join('');
    if (sortedTargets.length) selectTransferTarget(sortedTargets[0].pcNumber);
  }
  openDlg('dlgTransfer');
}

async function confirmTransfer() {
  const toPc = document.getElementById('dlgTransferTarget').value;
  const errEl = document.getElementById('dlgTransferError');
  errEl.style.display = 'none';
  try {
    const result = await connection.invoke('TransferSession', selectedPc, toPc);
    if (result === 'OK') {
      closeDlg('dlgTransfer');
      toast(`Сессия перенесена на ${toPc}`, 'good');
      deselectPc();
    } else {
      errEl.textContent = result;
      errEl.style.display = 'block';
    }
  } catch (e) { errEl.textContent = String(e); errEl.style.display = 'block'; }
}

function openServiceDlg() {
  if (serviceTypes.length === 0) { toast('Нет доступных услуг', 'warn'); return; }

  // Заполняем список активных сессий
  const pcSel = document.getElementById('dlgSvcPc');
  pcSel.innerHTML = '<option value="">— Без привязки —</option>';
  Object.values(pcs)
    .filter(pc => pc.isSession)
    .sort((a, b) => (a.pcNumberValue || 0) - (b.pcNumberValue || 0))
    .forEach(pc => {
      const reader = pc.userName || pc.readerId || '(анонимный)';
      pcSel.innerHTML += `<option value="${esc(pc.pcNumber)}">${esc(pc.pcNumber)}  —  ${esc(reader)}</option>`;
    });

  // Если есть выбранный ПК с сессией — предвыбираем его
  if (selectedPc && pcs[selectedPc]?.isSession) pcSel.value = selectedPc;
  else pcSel.value = '';

  // Инициализируем строки услуг
  _svcRows = [{ id: Date.now(), typeId: serviceTypes[0]?.id || '', qty: 1 }];

  // Сброс поля читателя и оплаты
  const readerInput = document.getElementById('dlgSvcReaderId');
  if (readerInput) readerInput.value = '';
  const payNowRadio = document.querySelector('[name="svcPay"][value="now"]');
  if (payNowRadio) payNowRadio.checked = true;

  renderSvcRows();
  updateSvcTotal();
  onSvcPcChanged();
  openDlg('dlgService');
}

function addSvcRow() {
  const usedTypes = new Set(_svcRows.map(r => r.typeId));
  const nextType = serviceTypes.find(s => !usedTypes.has(s.id));
  if (!nextType) { toast('Все доступные услуги уже добавлены', 'warn'); return; }
  _svcRows.push({ id: Date.now(), typeId: nextType.id, qty: 1 });
  renderSvcRows();
  updateSvcTotal();
}

function removeSvcRow(rowId) {
  _svcRows = _svcRows.filter(r => r.id !== rowId);
  if (_svcRows.length === 0)
    _svcRows = [{ id: Date.now(), typeId: serviceTypes[0]?.id || '', qty: 1 }];
  renderSvcRows();
  updateSvcTotal();
}

function onSvcRowTypeChange(rowId, typeId) {
  const row = _svcRows.find(r => r.id === rowId);
  if (row) row.typeId = typeId;
  renderSvcRows();
  updateSvcTotal();
}

function onSvcRowQtyChange(rowId, qty) {
  const row = _svcRows.find(r => r.id === rowId);
  if (row) row.qty = Math.max(1, parseInt(qty) || 1);
  updateSvcTotal();
}

function renderSvcRows() {
  const container = document.getElementById('dlgSvcList');
  const usedTypes = new Set(_svcRows.map(r => r.typeId));

  container.innerHTML = _svcRows.map(row => {
    const opts = serviceTypes.map(s => {
      const disabled = s.id !== row.typeId && usedTypes.has(s.id) ? 'disabled' : '';
      const selected = s.id === row.typeId ? 'selected' : '';
      return `<option value="${esc(s.id)}" ${disabled} ${selected}>${esc(s.name)} — ${fmt(s.price)} сум/${esc(s.unit)}</option>`;
    }).join('');
    const canRemove = _svcRows.length > 1;
    return `<div style="display:flex;gap:6px;align-items:center;margin-bottom:8px;min-width:0">
      <select class="field-select" style="flex:1;min-width:0"
        onchange="onSvcRowTypeChange(${row.id}, this.value)">${opts}</select>
      <input type="number" class="field-input" min="1" max="999" value="${row.qty}"
        style="width:60px;flex-shrink:0;text-align:center"
        oninput="onSvcRowQtyChange(${row.id}, this.value)">
      ${canRemove
        ? `<button class="mbtn mbtn-danger" onclick="removeSvcRow(${row.id})" style="flex-shrink:0;width:28px;height:28px;padding:0;font-size:13px">✕</button>`
        : '<div style="width:28px;flex-shrink:0"></div>'}
    </div>`;
  }).join('');

  // Прячем кнопку «Добавить», если все типы уже выбраны
  const btnAdd = document.getElementById('btnAddSvcRow');
  if (btnAdd) btnAdd.style.display = usedTypes.size >= serviceTypes.length ? 'none' : '';
}

function updateSvcTotal() {
  let total = 0;
  _svcRows.forEach(row => {
    const svc = serviceTypes.find(s => s.id === row.typeId);
    if (svc) total += svc.price * row.qty;
  });
  document.getElementById('dlgSvcTotal').textContent = total > 0 ? fmt(total) + ' сум' : '—';
}

function onSvcPcChanged() {
  const pcVal = document.getElementById('dlgSvcPc').value;
  const info = document.getElementById('dlgSvcSessionInfo');
  const readerRow = document.getElementById('dlgSvcReaderRow');

  if (pcVal && pcs[pcVal]) {
    const pc = pcs[pcVal];
    const reader = pc.userName || pc.readerId || '';
    info.textContent = reader
      ? `✓ Сессия на ${pcVal}: ${reader}`
      : `✓ Сессия на ${pcVal} (анонимный пользователь)`;
    info.style.display = 'block';
    if (readerRow) readerRow.style.display = 'none';
  } else {
    info.style.display = 'none';
    if (readerRow) readerRow.style.display = '';
  }
  updateDeferNote();
}

function onSvcPayChanged() { updateDeferNote(); }

function updateDeferNote() {
  const pcVal = document.getElementById('dlgSvcPc').value;
  const wantLater = document.getElementById('rbSvcLater')?.checked;
  document.getElementById('dlgSvcDeferNote').style.display =
    (wantLater && !pcVal) ? 'block' : 'none';
}

async function confirmService() {
  if (_svcRows.length === 0) { toast('Добавьте хотя бы одну услугу', 'warn'); return; }

  const typeIds    = _svcRows.map(r => r.typeId).filter(Boolean);
  const quantities = _svcRows.map(r => r.qty);
  const pcNumber   = document.getElementById('dlgSvcPc').value;
  const payNow     = document.querySelector('[name="svcPay"]:checked')?.value === 'now';

  let readerId = '', readerName = '';
  const pc = pcNumber ? pcs[pcNumber] : null;
  if (pc) {
    readerId   = pc.readerId || '';
    readerName = pc.userName || '';
  } else {
    readerId = (document.getElementById('dlgSvcReaderId')?.value || '').trim();
  }

  if (typeIds.length === 0) { toast('Выберите услугу', 'warn'); return; }

  closeDlg('dlgService');
  try {
    await connection.invoke('CreateServiceBatch', typeIds, quantities, pcNumber, readerId, readerName, payNow);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

let _summaryReaderId = '';
let _summaryPcNumber = '';

function showSessionSummary(s) {
  _summaryReaderId = s.readerId || '';
  _summaryPcNumber = s.pcNumber || '';

  let html = `
    <div class="summary-row"><span>ПК</span><span class="val">${esc(s.pcNumber)}</span></div>
    <div class="summary-row"><span>Тип</span><span class="val">${esc(s.sessionType)}</span></div>
    <div class="summary-row"><span>Время</span><span class="val">${fmtTime(s.duration)}</span></div>
    <div class="summary-row"><span>Оплачено</span><span class="val">${fmt(s.paidAmount)} сум</span></div>
    <div class="summary-row"><span>Начислено</span><span class="val">${fmt(s.earned)} сум</span></div>`;
  if (s.refund > 0)
    html += `<div class="refund-highlight">💵 Возврат: ${fmt(s.refund)} сум</div>`;

  const debts = s.serviceDebts || [];
  if (debts.length > 0) {
    html += `<div style="margin-top:14px;padding-top:12px;border-top:1px solid #eee">
      <div style="font-weight:600;color:#854F0B;margin-bottom:8px">Неоплаченные услуги</div>`;
    debts.forEach(d => {
      html += `<div class="summary-row" style="font-size:13px">
        <span>${esc(d.name)} × ${d.qty} ${esc(d.unit)}</span>
        <span class="val" style="color:#854F0B">${fmt(d.debt)} сум</span>
      </div>`;
    });
    const totalDebt = s.totalServiceDebt || debts.reduce((a, d) => a + d.debt, 0);
    html += `<div class="summary-row" style="font-weight:700;color:#854F0B;margin-top:6px">
      <span>Итого долгов</span>
      <span class="val">${fmt(totalDebt)} сум</span>
    </div>
    <div style="margin-top:10px">
      <button class="btn-primary" style="background:#854F0B;border-color:#854F0B;width:100%"
        onclick="paySessionDebts()">Оплатить долги по услугам</button>
    </div>`;
    html += `</div>`;
  }

  document.getElementById('dlgSummaryBody').innerHTML = html;
  openDlg('dlgSummary');
}

async function paySessionDebts() {
  try {
    await connection.invoke('PaySessionDebts', _summaryPcNumber, _summaryReaderId);
    toast('Долги оплачены', 'good');
    closeDlg('dlgSummary');
  } catch (e) { toast('Ошибка оплаты: ' + e, 'warn'); }
}

async function openDebtsDlg() {
  try {
    const debts = await connection.invoke('GetAllDebts');
    renderDebtsDlg(debts);
    openDlg('dlgDebts');
  } catch (e) { toast('Ошибка загрузки долгов: ' + e, 'warn'); }
}

function renderDebtsDlg(debts) {
  const body = document.getElementById('dlgDebtsBody');
  if (!debts || !debts.length) {
    body.innerHTML = '<p style="text-align:center;color:#888;padding:24px">Нет непогашенных долгов</p>';
    return;
  }
  let html = '';
  debts.forEach(d => {
    const reader = d.readerName || d.readerId || '—';
    const pc = d.pcNumber || '—';
    html += `<div class="summary-row" style="border-bottom:1px solid #f0f0f0;padding:10px 0;align-items:center">
      <span style="flex:1">
        <strong>${esc(d.serviceName)}</strong>
        <span style="color:#888;font-size:12px"> × ${d.quantity} ${esc(d.unit)}</span><br>
        <span style="color:#888;font-size:12px">ПК: ${esc(pc)} · Читатель: ${esc(reader)}</span>
      </span>
      <span style="color:#854F0B;font-weight:700;margin:0 16px">${fmt(d.debtAmount)} сум</span>
      <button class="btn-primary" style="padding:4px 12px;font-size:12px"
        onclick="payDebt('${esc(d.id)}', this)">Оплатить</button>
    </div>`;
  });
  const total = debts.reduce((a, d) => a + d.debtAmount, 0);
  html += `<div style="padding:12px 0;font-weight:700;color:#854F0B;text-align:right">
    Итого: ${fmt(total)} сум
  </div>`;
  body.innerHTML = html;
}

async function payDebt(id, btn) {
  btn.disabled = true;
  btn.textContent = '...';
  try {
    await connection.invoke('PayDebt', id);
    const debts = await connection.invoke('GetAllDebts');
    renderDebtsDlg(debts);
    toast('Долг оплачен', 'good');
  } catch (e) { toast('Ошибка: ' + e, 'warn'); btn.disabled = false; btn.textContent = 'Оплатить'; }
}

async function doLogout() {
  await fetch('/api/op/logout', { method: 'POST' }).catch(() => {});
  window.location.href = '/login.html';
}

// ── Просмотр экрана ───────────────────────────────────────────────────────────
async function openScreenView(pcNumber) {
  if (_screenPc) await closeScreenView();
  _screenPc = pcNumber;
  document.getElementById('dlgScreenViewTitle').textContent = `Экран: ${pcNumber}`;
  document.getElementById('screenViewImg').src = '';
  document.getElementById('screenViewStatus').textContent = 'Подключение...';
  openDlg('dlgScreenView');
  try { await fetch(`/api/screenshot/${encodeURIComponent(pcNumber)}/watch`, { method: 'POST' }); }
  catch (e) { /* ignore */ }
  _screenInterval = setInterval(pollScreen, 500);
}

async function closeScreenView() {
  const pc = _screenPc;
  _screenPc = null;
  clearInterval(_screenInterval);
  _screenInterval = null;
  closeDlg('dlgScreenView');
  if (pc) {
    try { await fetch(`/api/screenshot/${encodeURIComponent(pc)}/unwatch`, { method: 'POST' }); }
    catch (e) { /* ignore */ }
  }
}

async function pollScreen() {
  if (!_screenPc) return;
  try {
    const r = await fetch(`/api/screenshot/${encodeURIComponent(_screenPc)}`, { cache: 'no-store' });
    if (r.status === 204) {
      document.getElementById('screenViewStatus').textContent = 'Ожидание кадра...';
      return;
    }
    if (!r.ok) return;
    const blob = await r.blob();
    const url = URL.createObjectURL(blob);
    const img = document.getElementById('screenViewImg');
    const old = img.src;
    img.src = url;
    if (old.startsWith('blob:')) URL.revokeObjectURL(old);
    document.getElementById('screenViewStatus').textContent = `Обновлено: ${new Date().toLocaleTimeString('ru-RU')}`;
  } catch (e) { /* ignore */ }
}

// ── Диалоги ───────────────────────────────────────────────────────────────────
function openDlg(id) {
  document.getElementById(id).classList.add('open');
}
function closeDlg(id) {
  document.getElementById(id).classList.remove('open');
}
let _dlgMousedownTarget = null;
document.addEventListener('mousedown', e => { _dlgMousedownTarget = e.target; });

function closeDlgIfOverlay(e, id) {
  const overlay = document.getElementById(id);
  if (e.target === overlay && _dlgMousedownTarget === overlay) closeDlg(id);
}

// ── Тосты ─────────────────────────────────────────────────────────────────────
function toast(msg, type) {
  const el = document.createElement('div');
  el.className = 'toast ' + (type || '');
  el.textContent = msg;
  document.getElementById('toasts').appendChild(el);
  setTimeout(() => el.remove(), 4000);
}

// ── Вспомогательные ──────────────────────────────────────────────────────────
function getStatusClass(pc) {
  if (!pc.isOnline && pc.isSession) return 'status-offline-session';
  if (!pc.isOnline) return 'status-offline';
  if (pc.isPaused) return 'status-paused';
  if (pc.sessionType === 'VIP') return 'status-vip';
  if (pc.isSession) return 'status-session';
  if (pc.isFree) return 'status-free';
  return 'status-locked';
}

function getStatusLabel(pc) {
  if (!pc.isOnline && pc.isSession) return '🔴 Оффлайн (сессия)';
  if (!pc.isOnline) return 'Оффлайн';
  if (pc.isPaused) return '⏸ Пауза';
  if (pc.sessionType === 'VIP') return '⭐ VIP';
  if (pc.isSession) return '⏱ Лимит';
  if (pc.isFree) return '🔓 Свободен';
  return '🔒 Заблокирован';
}

function getDisplayTime(pc) {
  if (pc.isSession || pc.isPaused) return fmtTime(pc.elapsedSeconds);
  return '—';
}

function fmtTime(secs) {
  const h = Math.floor(secs / 3600);
  const m = Math.floor((secs % 3600) / 60);
  const s = secs % 60;
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
}
function pad(n) { return String(n).padStart(2, '0'); }
function fmt(n) { return Number(n).toLocaleString('ru-RU'); }
function esc(s) {
  return String(s ?? '')
    .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')
    .replace(/"/g,'&quot;');
}

// ══════════════════════════════════════════════════════════════════════════════
// Вкладки оператора
// ══════════════════════════════════════════════════════════════════════════════
let _currentOpTab = 'pcs';
let _financeLoaded = false;

function switchOpTab(tab) {
  _currentOpTab = tab;
  const panelIds = { pcs: 'boardPcs', readers: 'panelReaders', finance: 'panelFinance', stats: 'panelStats' };
  Object.entries(panelIds).forEach(([t, pid]) => {
    const panel = document.getElementById(pid);
    const btn   = document.getElementById('tabBtn' + t.charAt(0).toUpperCase() + t.slice(1));
    if (panel) panel.classList.toggle('active', t === tab);
    if (btn)   btn.classList.toggle('on', t === tab);
  });
  const toolbar = document.getElementById('toolbar');
  if (toolbar) toolbar.style.display = tab === 'pcs' ? '' : 'none';
  if (tab === 'finance' && !_financeLoaded) { _financeLoaded = true; loadFinanceHistory(); }
}

// ── Статистика (аналитика посещений) ─────────────────────────────────────────
let _opAnlPeriod  = 'day';
let _opAnlQuarter = 1;

function opSetAnalyticsPeriod(period) {
  _opAnlPeriod = period;
  ['day','month','quarter','year'].forEach(p => {
    document.getElementById('opAnlBtn' + p.charAt(0).toUpperCase() + p.slice(1))
      .classList.toggle('active', p === period);
  });
  document.getElementById('opAnlDateDay').style.display     = period === 'day'     ? ''     : 'none';
  document.getElementById('opAnlDateMonth').style.display   = period === 'month'   ? ''     : 'none';
  document.getElementById('opAnlDateQuarter').style.display = period === 'quarter' ? 'flex' : 'none';
  document.getElementById('opAnlDateYear').style.display    = period === 'year'    ? ''     : 'none';
}

function opSetQuarter(q) {
  _opAnlQuarter = q;
  [1,2,3,4].forEach(i => document.getElementById('opAnlQ' + i).classList.toggle('active', i === q));
}

function opGetAnalyticsDateStr() {
  switch (_opAnlPeriod) {
    case 'day':     return document.getElementById('opAnlDateDay').value;
    case 'month':   return document.getElementById('opAnlDateMonth').value;
    case 'quarter': return (document.getElementById('opAnlYearQuarter').value || new Date().getFullYear()) + '-Q' + _opAnlQuarter;
    case 'year':    return String(document.getElementById('opAnlDateYear').value || new Date().getFullYear());
  }
  return '';
}

async function opLoadAnalytics() {
  const dateStr = opGetAnalyticsDateStr();
  if (!dateStr) { toast('Выберите дату', 'warn'); return; }

  const emptyEl = document.getElementById('opAnlEmpty');
  emptyEl.style.display = '';
  emptyEl.textContent = 'Загрузка…';
  document.getElementById('opAnlSummary').style.display = 'none';
  document.getElementById('opAnlContent').style.display = 'none';

  try {
    const r = await fetch(`/api/op/readers/analytics?period=${_opAnlPeriod}&date=${encodeURIComponent(dateStr)}`);
    const data = await r.json();
    if (!r.ok) { emptyEl.textContent = data.error || 'Ошибка'; return; }
    opRenderAnalytics(data);
  } catch {
    emptyEl.textContent = 'Ошибка загрузки';
  }
}

function opRenderAnalytics(data) {
  const sumEl = document.getElementById('opAnlSummary');
  sumEl.style.display = '';
  sumEl.innerHTML = `<div class="kpi-grid">
    <div class="kpi"><div class="kpi-lbl">Визитов всего</div><div class="kpi-val">${data.totalVisits}</div></div>
    <div class="kpi"><div class="kpi-lbl">Анонимных</div><div class="kpi-val amber">${data.anonymousVisits}</div></div>
    <div class="kpi"><div class="kpi-lbl">Уникальных читателей</div><div class="kpi-val">${data.totalUniqueReaders}</div></div>
    <div class="kpi"><div class="kpi-lbl">Выручка (сум)</div><div class="kpi-val green">${data.totalRevenue.toLocaleString('ru-RU')}</div></div>
    <div class="kpi" style="flex:2;min-width:160px"><div class="kpi-lbl">Период</div><div class="kpi-val" style="font-size:14px;font-weight:500;color:var(--ink-2)">${opEsc(data.periodLabel)}</div></div>
  </div>`;

  const emptyEl = document.getElementById('opAnlEmpty');
  if (!data.totalVisits) {
    emptyEl.style.display = '';
    emptyEl.textContent = 'Нет данных о посещениях за выбранный период';
    document.getElementById('opAnlContent').style.display = 'none';
    return;
  }
  emptyEl.style.display = 'none';
  document.getElementById('opAnlContent').style.display = '';

  document.getElementById('opAnlGenderTable').innerHTML = opBuildTable(
    ['Пол', 'Визиты', 'Уникальных'],
    data.gender.map(g => [g.name, g.visits, g.uniqueReaders])
  );
  document.getElementById('opAnlCategoryTable').innerHTML = opBuildTable(
    ['Категория', 'Визиты', 'Уникальных'],
    data.categories.map(c => [c.name, c.visits, c.uniqueReaders])
  );

  // Age groups: auto-hide all-zero gender columns
  const knownGenders = [...new Set(data.ageGroups.flatMap(g => Object.keys(g.byGender || {})))].sort();
  const activeGenders = knownGenders.filter(gn =>
    data.ageGroups.some(ag => (ag.byGender[gn]?.visits ?? 0) > 0 || (ag.byGender[gn]?.uniqueReaders ?? 0) > 0));
  const ageHeaders = ['Группа', 'Визиты', 'Уникальных', ...activeGenders.flatMap(g => [`${g} визиты`, `${g} уник.`])];
  const ageRows = data.ageGroups.map(g => {
    const byG = g.byGender || {};
    return [g.group, g.visits, g.uniqueReaders, ...activeGenders.flatMap(gn => [byG[gn]?.visits ?? 0, byG[gn]?.uniqueReaders ?? 0])];
  });
  document.getElementById('opAnlAgeTable').innerHTML = opBuildTable(ageHeaders, ageRows);

  // Services table with «Компьютер» row and «Итого» footer
  document.getElementById('opAnlServicesTable').innerHTML = opBuildServicesTable(data.services, data.pcStats);

  // PC stats block
  opRenderPcStats(data.pcStats);
}

function opBuildServicesTable(services, pc) {
  pc = pc || {};
  let html = '<div class="dtable-wrap"><table class="dtable"><thead><tr><th>Услуга</th><th>Кол-во</th><th>Сумма (сум)</th></tr></thead><tbody>';

  if ((pc.totalSessions ?? 0) > 0) {
    html += `<tr><td style="color:#7799cc;font-weight:500">🖥 Компьютер (сессии)</td>
      <td>${(pc.totalSessions||0).toLocaleString('ru-RU')}</td>
      <td>${(pc.totalRevenue||0).toLocaleString('ru-RU')}</td></tr>`;
  }
  services.forEach(s => {
    const zQ = s.quantity === 0 ? ' class="anl-zero"' : '';
    const zA = s.totalAmount === 0 ? ' class="anl-zero"' : '';
    html += `<tr><td>${opEsc(s.name)}</td><td${zQ}>${s.quantity.toLocaleString('ru-RU')}</td><td${zA}>${s.totalAmount.toLocaleString('ru-RU')}</td></tr>`;
  });
  const totalQty = (pc.totalSessions||0) + services.reduce((s,r)=>s+r.quantity,0);
  const totalAmt = (pc.totalRevenue||0)  + services.reduce((s,r)=>s+r.totalAmount,0);
  if (totalQty > 0 || totalAmt > 0) {
    html += `<tr style="border-top:2px solid #2D2D5B"><td style="font-weight:700;color:#D8D8F0">Итого</td>
      <td style="font-weight:700;color:#D8D8F0">${totalQty.toLocaleString('ru-RU')}</td>
      <td style="font-weight:700;color:#1D9E75">${totalAmt.toLocaleString('ru-RU')}</td></tr>`;
  }
  if (!services.length && !(pc.totalSessions > 0)) {
    html += '<tr><td colspan="3" style="text-align:center;color:#444;padding:16px">Услуги не использовались</td></tr>';
  }
  html += '</tbody></table></div>';
  return html;
}

function opRenderPcStats(pc) {
  if (!pc) return;
  document.getElementById('opAnlPcSummary').innerHTML = `
    <div class="kpi"><div class="kpi-lbl">Сессий за ПК</div><div class="kpi-val">${pc.totalSessions}</div></div>
    <div class="kpi"><div class="kpi-lbl">Анонимных</div><div class="kpi-val amber">${pc.anonSessions}</div></div>
    <div class="kpi"><div class="kpi-lbl">Уникальных читателей</div><div class="kpi-val">${pc.uniqueReaders}</div></div>
    <div class="kpi"><div class="kpi-lbl">Выручка ПК (сум)</div><div class="kpi-val green">${pc.totalRevenue.toLocaleString('ru-RU')}</div></div>`;

  document.getElementById('opAnlPcGenderTable').innerHTML = opBuildTable(['Пол','Сессий','Уникальных'], pc.gender.map(g=>[g.name,g.sessions,g.uniqueReaders]));
  document.getElementById('opAnlPcCategoryTable').innerHTML = opBuildTable(['Категория','Сессий','Уникальных'], pc.categories.map(c=>[c.name,c.sessions,c.uniqueReaders]));

  const pcG = [...new Set(pc.ageGroups.flatMap(g => Object.keys(g.byGender||{})))].sort();
  const pcAG = pcG.filter(gn => pc.ageGroups.some(ag=>(ag.byGender[gn]?.sessions??0)>0||(ag.byGender[gn]?.uniqueReaders??0)>0));
  const pcAgeHdr = ['Группа','Сессий','Уникальных',...pcAG.flatMap(g=>[`${g} сессий`,`${g} уник.`])];
  const pcAgeRows = pc.ageGroups.map(g=>{const b=g.byGender||{};return[g.group,g.sessions,g.uniqueReaders,...pcAG.flatMap(gn=>[b[gn]?.sessions??0,b[gn]?.uniqueReaders??0])];});
  document.getElementById('opAnlPcAgeTable').innerHTML = opBuildTable(pcAgeHdr, pcAgeRows);

  const topHdr = ['Читатель','Категория','Визитов','Часов'];
  document.getElementById('opAnlPcTopVisits').innerHTML = pc.topByVisits.length
    ? opBuildTable(topHdr, pc.topByVisits.map(u=>[u.readerName,u.category,u.visits,+(u.totalMinutes/60).toFixed(1)]))
    : '<div class="op-empty" style="text-align:left;padding:8px 0">Нет данных</div>';
  document.getElementById('opAnlPcTopHours').innerHTML = pc.topByHours.length
    ? opBuildTable(topHdr, pc.topByHours.map(u=>[u.readerName,u.category,u.visits,+(u.totalMinutes/60).toFixed(1)]))
    : '<div class="op-empty" style="text-align:left;padding:8px 0">Нет данных</div>';
}

function opBuildTable(headers, rows) {
  if (!rows.length) return '<div class="op-empty" style="text-align:left;padding:10px 0">Нет данных</div>';
  // Hide columns where every numeric value is 0
  const keep = headers.map((_, ci) =>
    ci === 0 || rows.some(r => { const v = r[ci]; return typeof v === 'number' ? v !== 0 : v !== '0'; })
  );
  const hdr2  = headers.filter((_, ci) => keep[ci]);
  const rows2 = rows.map(r => r.filter((_, ci) => keep[ci]));
  let html = '<div class="dtable-wrap"><table class="dtable"><thead><tr>';
  hdr2.forEach(h => { html += `<th>${opEsc(String(h))}</th>`; });
  html += '</tr></thead><tbody>';
  rows2.forEach(row => {
    html += '<tr>';
    row.forEach((v, i) => {
      const val = typeof v === 'number' ? v.toLocaleString('ru-RU') : opEsc(String(v));
      const cls = (typeof v === 'number' && v === 0 && i > 0) ? ' class="anl-zero"' : '';
      html += `<td${cls}>${val}</td>`;
    });
    html += '</tr>';
  });
  html += '</tbody></table></div>';
  return html;
}

function opEsc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function opExportAnalytics() {
  const dateStr = opGetAnalyticsDateStr();
  if (!dateStr) { toast('Выберите дату', 'warn'); return; }
  window.open(`/api/op/readers/analytics/export?period=${_opAnlPeriod}&date=${encodeURIComponent(dateStr)}`, '_blank');
}

// ── Читатели ──────────────────────────────────────────────────────────────────
async function searchReaders() {
  const q = document.getElementById('readersSearchInput').value.trim();
  const res = document.getElementById('readersResult');
  res.innerHTML = '<div class="op-empty">Поиск…</div>';
  try {
    const r = await fetch('/api/op/readers?search=' + encodeURIComponent(q));
    if (!r.ok) { res.innerHTML = '<div class="op-empty" style="color:#E24B4A">Ошибка: ' + r.status + '</div>'; return; }
    const list = await r.json();
    if (!list.length) { res.innerHTML = '<div class="op-empty">Ничего не найдено</div>'; return; }
    res.innerHTML = `
      <div class="dtable-wrap">
        <table class="dtable">
          <thead><tr>
            <th>№ билета</th><th>ФИО</th><th>Категория</th>
            <th>Дата рождения</th><th>Пол</th><th>Зарегистрирован</th>
          </tr></thead>
          <tbody>
            ${list.map(rd => `<tr>
              <td><code style="font-size:11px">${esc(rd.cardId)}</code></td>
              <td>${esc(rd.fullName)}</td>
              <td>${esc(rd.category)}</td>
              <td>${esc(rd.birthDate)}</td>
              <td>${esc(rd.gender)}</td>
              <td>${esc(rd.registeredAt)}</td>
            </tr>`).join('')}
          </tbody>
        </table>
      </div>
      <div style="font-size:11px;color:#555;margin-top:6px">Найдено: ${list.length}</div>`;
  } catch(e) {
    res.innerHTML = '<div class="op-empty" style="color:#E24B4A">Ошибка соединения</div>';
  }
}

// ── История финансов ──────────────────────────────────────────────────────────
let _finSessions = [];
let _finServices = [];
let _finTab = 'sessions';

async function loadFinanceHistory() {
  const [rS, rSvc] = await Promise.all([
    fetch('/api/op/finance/sessions').then(r => r.ok ? r.json() : []),
    fetch('/api/op/finance/services').then(r => r.ok ? r.json() : [])
  ]);
  _finSessions = Array.isArray(rS) ? rS : [];
  _finServices = Array.isArray(rSvc) ? rSvc : [];
  renderFinanceSessions();
  renderFinanceServices();
}

function switchFinanceTab(tab) {
  _finTab = tab;
  document.getElementById('finTabSessions').classList.toggle('active', tab === 'sessions');
  document.getElementById('finTabServices').classList.toggle('active', tab === 'services');
  document.getElementById('financeSessionsPanel').style.display  = tab === 'sessions' ? '' : 'none';
  document.getElementById('financeServicesPanel').style.display  = tab === 'services' ? '' : 'none';
}

function fmtDur(secs) {
  const h = Math.floor(secs / 3600), m = Math.floor((secs % 3600) / 60), s = secs % 60;
  return `${pad(h)}:${pad(m)}:${pad(s)}`;
}

function renderFinanceSessions() {
  const el = document.getElementById('financeSessionsResult');
  if (!_finSessions.length) { el.innerHTML = '<div class="op-empty">Нет данных</div>'; return; }
  el.innerHTML = `
    <table class="dtable">
      <thead><tr>
        <th>ПК</th><th>Тип</th><th>Читатель</th><th>Пользователь</th>
        <th>Длительность</th><th>Сумма</th><th>Оплачено</th><th>Возврат</th>
        <th>Оператор</th><th>Начало</th><th>Конец</th>
      </tr></thead>
      <tbody>
        ${_finSessions.map(s => `<tr>
          <td>${esc(s.pcNumber)}</td>
          <td>${esc(s.sessionType)}</td>
          <td><code style="font-size:11px">${esc(s.readerId||'—')}</code></td>
          <td>${esc(s.userName||'—')}</td>
          <td>${fmtDur(s.durationSeconds||0)}</td>
          <td>${fmt(s.earnedAmount)}</td>
          <td>${fmt(s.paidAmount)}</td>
          <td>${s.refundAmount ? fmt(s.refundAmount) : '—'}</td>
          <td>${esc(s.operatorName||'—')}</td>
          <td>${fmtLocal(s.startTime)}</td>
          <td>${fmtLocal(s.endTime)}</td>
        </tr>`).join('')}
      </tbody>
    </table>`;
}

function renderFinanceServices() {
  const el = document.getElementById('financeServicesResult');
  if (!_finServices.length) { el.innerHTML = '<div class="op-empty">Нет данных</div>'; return; }
  el.innerHTML = `
    <table class="dtable">
      <thead><tr>
        <th>Услуга</th><th>Единица</th><th>Кол-во</th><th>Цена/ед</th>
        <th>Итого</th><th>Оплачено</th><th>Читатель</th><th>ПК</th><th>Дата</th>
      </tr></thead>
      <tbody>
        ${_finServices.map(t => `<tr>
          <td>${esc(t.serviceName)}</td>
          <td>${esc(t.unit)}</td>
          <td>${t.quantity}</td>
          <td>${fmt(t.pricePerUnit)}</td>
          <td>${fmt(t.totalAmount)}</td>
          <td>${fmt(t.paidAmount)}</td>
          <td>${esc(t.readerName||'—')}</td>
          <td>${esc(t.pcNumber||'—')}</td>
          <td>${fmtLocal(t.createdAt)}</td>
        </tr>`).join('')}
      </tbody>
    </table>`;
}

function fmtLocal(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return iso;
  return d.toLocaleString('ru-RU', { day:'2-digit', month:'2-digit', year:'numeric', hour:'2-digit', minute:'2-digit' });
}

function exportFinanceXlsx() {
  window.location.href = '/api/op/finance/export';
}

// ── Статусные цвета ──────────────────────────────────────────────────────────

function _stKey(pc) {
  if (!pc.isOnline) return 'offline';
  if (pc.isLocked) return 'locked';
  if (!pc.isSession && !pc.isFree) return 'offline';
  if (!pc.isSession) return 'free';
  if (pc.sessionType === 'VIP' || pc.sessionType === 'vip') return 'vip';
  return 'limit';
}

function getStatusColor(pc) {
  return `var(--${_stKey(pc)})`;
}

// ── Фильтрация карточек ──────────────────────────────────────────────────────

function setFilterState(filter) {
  _filterState = filter;
  document.querySelectorAll('#toolbar .fchip').forEach(ch => {
    ch.classList.toggle('on', ch.dataset.filter === filter);
  });
  _filterCards();
}

function setSearchQuery(q) {
  _searchQuery = q.toLowerCase().trim();
  _filterCards();
}

function _filterCards() {
  const cards = document.querySelectorAll('#grid .pccard');
  cards.forEach(card => {
    const pcNum = card.dataset.pc;
    const pc = pcs[pcNum];
    if (!pc) { card.style.display = 'none'; return; }

    let show = true;
    if (_filterState === 'free') show = pc.isFree && pc.isOnline;
    else if (_filterState === 'session') show = pc.isSession;
    else if (_filterState === 'offline') show = !pc.isOnline;

    if (show && _searchQuery) {
      const haystack = [pcNum, pc.userName, pc.readerId, pc.ip].filter(Boolean).join(' ').toLowerCase();
      show = haystack.includes(_searchQuery);
    }
    card.style.display = show ? '' : 'none';
  });
}

// ── Поля длительности ────────────────────────────────────────────────────────

function stepDur(id, delta, min, max) {
  const el = document.getElementById(id);
  if (!el) return;
  let val = parseInt(el.value, 10) || 0;
  val = Math.max(min, Math.min(max, val + delta));
  el.value = val;
  el.dispatchEvent(new Event('input'));
}

function applyTimePreset(mins) {
  const el = document.getElementById('dlgDuration');
  if (!el) return;
  el.value = mins;
  el.dispatchEvent(new Event('input'));
  const extEl = document.getElementById('dlgExtDuration');
  if (extEl) {
    extEl.value = mins;
    extEl.dispatchEvent(new Event('input'));
  }
}

// ── Переключатели seg-opt ────────────────────────────────────────────────────

function onSegStypeClick(el, val) {
  const seg = el.closest('.seg');
  if (!seg) return;
  seg.querySelectorAll('.seg-opt').forEach(o => o.classList.remove('on'));
  el.classList.add('on');
  const radio = el.querySelector('input[type=radio]');
  if (radio) radio.checked = true;
  // Обновить подсказку стоимости в диалоге сессии
  if (typeof updateSessionAmountHint === 'function') updateSessionAmountHint();
}

function onSegSvcPayClick(el, val) {
  const seg = el.closest('.seg');
  if (!seg) return;
  seg.querySelectorAll('.seg-opt').forEach(o => o.classList.remove('on'));
  el.classList.add('on');
  const radio = el.querySelector('input[type=radio]');
  if (radio) radio.checked = true;
}

// ── Диалог переноса ──────────────────────────────────────────────────────────

function selectTransferTarget(pcNum) {
  // Синхронизируем скрытый select
  const sel = document.getElementById('dlgTransferTarget');
  if (sel) sel.value = pcNum;
  // Обновляем визуальную сетку
  document.querySelectorAll('#dlgTransferGrid .move-opt').forEach(opt => {
    opt.classList.toggle('on', opt.dataset.pc === String(pcNum));
  });
}

// ── Тема ─────────────────────────────────────────────────────────────────────

const BASE_PALETTES = {
  light:    { bg:'#eef1f6', topbar:'#0f1623', card:'#ffffff', cardMuted:'#f7f9fc', line:'#e3e8f0', ink:'#161c26', ink2:'#4a5566', accent:'#2563eb', free:'#16a34a', limit:'#2563eb', vip:'#b8860b', locked:'#e0413b' },
  dark:     { bg:'#0b0f17', topbar:'#0a0e15', card:'#161d2b', cardMuted:'#1c2433', line:'#2a3344', ink:'#e9eef7', ink2:'#aab6c8', accent:'#3b82f6', free:'#34d399', limit:'#60a5fa', vip:'#e6b13e', locked:'#f87171' },
  tiffany:  { bg:'#aaded6', topbar:'#0a2725', card:'#ffffff', cardMuted:'#e7f6f3', line:'#c4e6e0', ink:'#102f2c', ink2:'#3c5d58', accent:'#0aa89f', free:'#0f9d77', limit:'#0aa89f', vip:'#b8860b', locked:'#e0413b' },
  night:    { bg:'#08120f', topbar:'#071512', card:'#102019', cardMuted:'#15271f', line:'#233a31', ink:'#e7f3ef', ink2:'#a2bbb3', accent:'#2dd4bf', free:'#34d399', limit:'#2dd4bf', vip:'#e6b13e', locked:'#f87171' },
  sepia:    { bg:'#e7dac2', topbar:'#2a2114', card:'#fffdf7', cardMuted:'#f6efe0', line:'#e4d8bf', ink:'#2e2616', ink2:'#5f5440', accent:'#c2691f', free:'#5d8a2c', limit:'#2f6f9e', vip:'#b07d12', locked:'#bb4430' },
  amethyst: { bg:'#130f1e', topbar:'#100b1b', card:'#1d1730', cardMuted:'#241d3a', line:'#332a4d', ink:'#efeaf8', ink2:'#b9afce', accent:'#a78bfa', free:'#34d399', limit:'#b794f6', vip:'#e6b13e', locked:'#f87171' },
};
const DEFAULT_CUSTOM = { ...BASE_PALETTES.light };

const CUSTOM_VARS = [
  '--bg','--bg-grad-a','--bg-grad-b','--topbar','--topbar-2','--card','--card-muted','--surface','--surface-hover',
  '--line','--line-strong','--ink','--ink-2','--ink-3','--ink-on-dark','--ink-on-dark-2','--toolbar-bg',
  '--accent','--accent-soft','--accent-ink','--free','--free-bg','--free-ring','--limit','--limit-bg','--limit-ring',
  '--vip','--vip-bg','--vip-ring','--locked','--locked-bg','--locked-ring','--warn','--warn-bg',
  '--offline','--offline-bg','--offline-ring','--shadow-card','--shadow-card-hover','--shadow-pop',
];

let _customPalette = { ...DEFAULT_CUSTOM };

function _lum(hex) {
  const c = (hex || '#000').replace('#', '');
  const n = c.length === 3 ? c.split('').map(x => x + x).join('') : c;
  const r = parseInt(n.substr(0,2),16)/255, g = parseInt(n.substr(2,2),16)/255, b = parseInt(n.substr(4,2),16)/255;
  const f = x => x <= 0.03928 ? x/12.92 : Math.pow((x+0.055)/1.055, 2.4);
  return 0.2126*f(r) + 0.7152*f(g) + 0.0722*f(b);
}

function _mix(a, pct, b) { return `color-mix(in srgb, ${a} ${pct}%, ${b})`; }

function applyCustomTheme(p0) {
  const p = { ...DEFAULT_CUSTOM, ...(p0 || {}) };
  const { bg, topbar, card, cardMuted, line, ink, ink2, accent, free, limit, vip, locked } = p;
  const root = document.documentElement.style;
  const darkBg = _lum(bg) < 0.32;
  const topDark = _lum(topbar) < 0.45;
  const accDark = _lum(accent) < 0.6;
  const st = (color, name) => ({
    [name]: color,
    [name+'-bg']: _mix(color, 15, card),
    [name+'-ring']: _mix(color, 34, card),
  });
  const m = {
    '--bg': bg,
    '--bg-grad-a': _mix(bg, 86, '#ffffff'),
    '--bg-grad-b': _mix(bg, 92, '#000000'),
    '--topbar': topbar,
    '--topbar-2': _mix(topbar, 86, '#ffffff'),
    '--card': card,
    '--card-muted': cardMuted,
    '--surface': card,
    '--surface-hover': _mix(cardMuted, 86, ink),
    '--line': line,
    '--line-strong': _mix(line, 76, ink),
    '--ink': ink,
    '--ink-2': ink2,
    '--ink-3': _mix(ink2, 58, bg),
    '--ink-on-dark': topDark ? '#eef2f8' : '#10202e',
    '--ink-on-dark-2': topDark ? '#97a3b6' : 'rgba(16,28,38,.62)',
    '--toolbar-bg': _mix(bg, 86, 'transparent'),
    '--accent': accent,
    '--accent-soft': _mix(accent, 16, card),
    '--accent-ink': accDark ? '#ffffff' : '#16181d',
    ...st(free, '--free'),
    ...st(limit, '--limit'),
    ...st(vip, '--vip'),
    ...st(locked, '--locked'),
    '--warn': vip,
    '--warn-bg': _mix(vip, 16, card),
    '--offline': _mix(ink2, 72, bg),
    '--offline-bg': _mix(ink2, 14, card),
    '--offline-ring': _mix(ink2, 30, card),
    '--shadow-card': darkBg ? '0 1px 2px rgba(0,0,0,.4), 0 6px 18px rgba(0,0,0,.45)' : '0 1px 2px rgba(20,28,45,.05), 0 4px 14px rgba(20,28,45,.07)',
    '--shadow-card-hover': darkBg ? '0 2px 8px rgba(0,0,0,.5), 0 16px 36px rgba(0,0,0,.55)' : '0 2px 6px rgba(20,28,45,.08), 0 12px 30px rgba(20,28,45,.13)',
    '--shadow-pop': darkBg ? '0 24px 70px rgba(0,0,0,.65)' : '0 18px 60px rgba(15,22,35,.28)',
  };
  for (const k in m) root.setProperty(k, m[k]);
}

function clearCustomTheme() {
  const root = document.documentElement.style;
  for (const v of CUSTOM_VARS) root.removeProperty(v);
}

const THEME_LIST = [
  { id:'light',    name:'Светлая',  sub:'Классическая',  bg:'#eef1f6', top:'#0f1623', acc:'#2563eb' },
  { id:'dark',     name:'Тёмная',   sub:'Тёмная',        bg:'#0b0f17', top:'#0a0e15', acc:'#3b82f6' },
  { id:'tiffany',  name:'Тиффани',  sub:'Бирюзовая',     bg:'#aaded6', top:'#0a2725', acc:'#0aa89f' },
  { id:'night',    name:'Ночь',     sub:'Зелёная тёмная', bg:'#08120f', top:'#071512', acc:'#2dd4bf' },
  { id:'sepia',    name:'Сепия',    sub:'Тёплая',        bg:'#e7dac2', top:'#2a2114', acc:'#c2691f' },
  { id:'amethyst', name:'Аметист',  sub:'Фиолетовая',    bg:'#130f1e', top:'#100b1b', acc:'#a78bfa' },
];

function _renderThemeMenu() {
  const menu = document.getElementById('themeMenu');
  if (!menu) return;
  const saved = localStorage.getItem('bibTheme') || 'light';
  const opts = THEME_LIST.map(t => `
    <button class="theme-opt${saved === t.id ? ' on' : ''}" data-theme="${t.id}" onclick="setTheme('${t.id}')">
      <div class="theme-prev">
        <div class="pv-top" style="background:${t.top}"></div>
        <div class="pv-body" style="background:${t.bg}">
          <div class="pv-card" style="background:#ffffff22"></div>
          <div class="pv-dot" style="background:${t.acc}"></div>
        </div>
      </div>
      <div class="theme-opt-text">
        <span class="theme-opt-name">${t.name}</span>
        <span class="theme-opt-sub">${t.sub}</span>
      </div>
      <svg class="theme-check" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3"><polyline points="20 6 9 17 4 12"/></svg>
    </button>`).join('');
  menu.innerHTML = `
    <div class="theme-menu-cap">Тема</div>
    ${opts}
    <div class="theme-divider"></div>
    <button class="theme-opt${saved === 'custom' ? ' on' : ''}" data-theme="custom" onclick="openThemeEditor()">
      <div class="theme-prev theme-prev-custom">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.22 4.22l2.12 2.12M17.66 17.66l2.12 2.12M2 12h3M19 12h3M4.22 19.78l2.12-2.12M17.66 6.34l2.12-2.12"/></svg>
      </div>
      <div class="theme-opt-text">
        <span class="theme-opt-name">Свои цвета</span>
        <span class="theme-opt-sub">Конструктор</span>
      </div>
      <svg class="theme-check" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3"><polyline points="20 6 9 17 4 12"/></svg>
    </button>`;
}

function initTheme() {
  const saved = localStorage.getItem('bibTheme') || 'light';
  if (saved === 'custom') {
    const raw = localStorage.getItem('bibCustomPalette');
    if (raw) { try { _customPalette = JSON.parse(raw); } catch(e) {} }
    document.documentElement.setAttribute('data-theme', 'custom');
    applyCustomTheme(_customPalette);
  } else {
    document.documentElement.setAttribute('data-theme', saved);
  }
  _renderThemeMenu();
}

function setTheme(id) {
  clearCustomTheme();
  if (id === 'custom') {
    document.documentElement.setAttribute('data-theme', 'custom');
    applyCustomTheme(_customPalette);
  } else {
    document.documentElement.setAttribute('data-theme', id);
  }
  localStorage.setItem('bibTheme', id);
  _renderThemeMenu();
  const menu = document.getElementById('themeMenu');
  if (menu) menu.style.display = 'none';
}

function _syncThemeMenu(active) {
  document.querySelectorAll('#themeMenu .theme-opt').forEach(opt => {
    opt.classList.toggle('on', opt.dataset.theme === active);
  });
}

function toggleThemeMenu() {
  const menu = document.getElementById('themeMenu');
  if (!menu) return;
  const visible = menu.style.display !== 'none' && menu.style.display !== '';
  menu.style.display = visible ? 'none' : 'block';
}

// ── Конструктор темы ─────────────────────────────────────────────────────────

const CE_GROUPS = [
  { title: 'Фон и шапка', fields: [['bg', 'Фон страницы'], ['topbar', 'Шапка панели']] },
  { title: 'Поверхности и окна', fields: [['card', 'Карточки и окна'], ['cardMuted', 'Заливки и поля'], ['line', 'Границы']] },
  { title: 'Текст', fields: [['ink', 'Основной текст'], ['ink2', 'Вторичный текст']] },
  { title: 'Акцент и кнопки', fields: [['accent', 'Акцент']] },
  { title: 'Статусы', fields: [['free', 'Свободен · деньги'], ['limit', 'В сессии'], ['vip', 'VIP'], ['locked', 'Опасные действия']] },
];

function openThemeEditor() {
  const editor = document.getElementById('themeEditor');
  if (!editor) return;
  const menu = document.getElementById('themeMenu');
  if (menu) menu.style.display = 'none';
  _renderThemeEditor();
  editor.style.display = '';
}

function closeThemeEditor() {
  const editor = document.getElementById('themeEditor');
  if (editor) editor.style.display = 'none';
}

function _renderThemeEditor() {
  const body = document.getElementById('ceBody');
  if (!body) return;
  const p = { ...DEFAULT_CUSTOM, ..._customPalette };

  const basesHtml = Object.keys(BASE_PALETTES).map(id => {
    const labels = { light:'Светлая', dark:'Тёмная', tiffany:'Тиффани', night:'Ночь', sepia:'Сепия', amethyst:'Аметист' };
    return `<button class="ce-base" onclick="seedTheme('${id}')">
      <span class="d" style="background:${BASE_PALETTES[id].bg}"></span>
      <span class="d" style="background:${BASE_PALETTES[id].accent}"></span>
      ${labels[id] || id}
    </button>`;
  }).join('');

  const groupsHtml = CE_GROUPS.map(g => {
    const rows = g.fields.map(([key, label]) => `
      <div class="ce-row">
        <input type="color" class="ce-sw" value="${p[key] || '#000000'}"
          oninput="onThemeColorChange('${key}', this.value)" onchange="onThemeColorChange('${key}', this.value)">
        <div class="ce-rtext">
          <span class="ce-rname">${label}</span>
          <span class="ce-rhex mono" id="ce-hex-${key}">${p[key] || '#000000'}</span>
        </div>
      </div>`).join('');
    return `<div class="ce-sec">${g.title}</div>${rows}`;
  }).join('');

  body.innerHTML = `
    <div class="ce-sec">Начать с базы</div>
    <div class="ce-bases">${basesHtml}</div>
    ${groupsHtml}`;
}

function onThemeColorChange(key, val) {
  _customPalette[key] = val;
  const hex = document.getElementById(`ce-hex-${key}`);
  if (hex) hex.textContent = val;
  applyCustomTheme(_customPalette);
  document.documentElement.setAttribute('data-theme', 'custom');
  localStorage.setItem('bibTheme', 'custom');
  _syncThemeMenu('custom');
}

function seedTheme(id) {
  _customPalette = { ...BASE_PALETTES[id] };
  applyCustomTheme(_customPalette);
  document.documentElement.setAttribute('data-theme', 'custom');
  localStorage.setItem('bibTheme', 'custom');
  _syncThemeMenu('custom');
  _renderThemeEditor();
}

function saveCustomTheme() {
  localStorage.setItem('bibCustomPalette', JSON.stringify(_customPalette));
  localStorage.setItem('bibTheme', 'custom');
  _renderThemeMenu();
  closeThemeEditor();
}

function resetCustomTheme() {
  _customPalette = { ...DEFAULT_CUSTOM };
  applyCustomTheme(_customPalette);
  _renderThemeEditor();
}
