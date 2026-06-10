'use strict';

// ─── State ─────────────────────────────────────────────────────────────────
let pcs = {};           // pcNumber → state
let finSessions = [];
let finServices = [];
let settings = {};
let finTab = 'sessions';
let activePc = null;         // selected pc for dialogs
let selectedAdminPc = null;  // pc highlighted in grid → bottom action bar
let pendingOfflinePc = null;
let pendingConflict = null;
let renamePcVal = null;
let _screenPc = null;
let _screenInterval = null;
let latestClientVersion = '';   // Последняя доступная версия BibClient (из /updates/bibclient-version.json)
let updatePanelDismissed = false;
let pvBgUrl = null;             // URL фона для предпросмотра (blob: после выбора файла, /files/... после загрузки)

// ─── Init ───────────────────────────────────────────────────────────────────
(async function init() {
  const r = await fetch('/api/admin/check');
  if (!r.ok) { window.location.href = '/admin-login.html'; return; }

  document.getElementById('finDate').textContent =
    new Date().toLocaleDateString('ru-RU', { weekday:'long', day:'numeric', month:'long', year:'numeric' });

  detectLocalIp();
  connectHub();
  loadFinance();
  loadSettings();
  loadOperators();
  setInterval(tickTimers, 1000);
  updateNotifBtn();

  // Инициализируем дату для аналитики
  const _today = new Date().toISOString().split('T')[0];
  document.getElementById('anlDateDay').value     = _today;
  document.getElementById('anlDateMonth').value   = _today.substring(0, 7);
  document.getElementById('anlYearQuarter').value = _today.substring(0, 4);
  document.getElementById('anlDateYear').value    = _today.substring(0, 4);
  // Выделяем текущий квартал
  const _curQ = Math.ceil((new Date().getMonth() + 1) / 3);
  setQuarter(_curQ);

  // Загружаем последнюю доступную версию BibClient для сравнения на карточках
  fetch('/updates/bibclient-version.json').then(r => r.ok ? r.json() : null).then(v => {
    if (v?.Version) { latestClientVersion = v.Version; renderPcGrid(); }
  }).catch(() => {});
})();

// ─── SignalR ─────────────────────────────────────────────────────────────────
let conn;

function connectHub() {
  conn = new signalR.HubConnectionBuilder()
    .withUrl('/adminhub')
    .withAutomaticReconnect([2000, 5000, 10000, 30000, 60000])
    .build();

  conn.on('stateSnapshot', all => {
    pcs = {};
    all.forEach(c => pcs[c.pcNumber] = c);
    renderPcGrid();
  });
  conn.on('pcUpdated', c => {
    pcs[c.pcNumber] = c;
    renderPcGrid();
    loadFinance(); // refresh finance when a session ends
  });
  conn.on('allPcsUpdated', all => {
    pcs = {};
    all.forEach(c => pcs[c.pcNumber] = c);
    renderPcGrid();
  });
  conn.on('sessionSummary', d => {
    const isManual = _manuallyEndedPcs.has(d.pcNumber);
    _manuallyEndedPcs.delete(d.pcNumber);
    showSummary(d, isManual);
  });

  conn.on('sessionEndedByStaff', d => {
    if (_manuallyEndedPcs.has(d.pcNumber)) return; // я завершил сам
    const h = Math.floor(d.durationSeconds / 3600), m = Math.floor((d.durationSeconds % 3600) / 60);
    const name = d.userName && d.userName !== '—' ? d.userName : 'Анонимный';
    bibNotify(`✅ ${d.pcNumber} — сессия завершена`, `${name} · ${h}ч ${m}м · ${(d.earned||0).toLocaleString('ru-RU')} сум`);
  });

  conn.on('serverRestarting', d => {
    bibNotify('🔄 Обновление сервера', 'Страница обновится автоматически. После перезагрузки войдите снова.');
  });
  conn.on('offlineAlert', d => showOfflineAlert(d));
  conn.on('offlineResolved', d => {
    toast(`${d.pcNumber}: решение — ${d.decision === 'Pause' ? 'пауза' : 'продолжить'}`);
    closeDlg('dlgOffline');
  });
  conn.on('clockDriftAlert', d =>
    toast(`⏱ ${d.pcNumber}: расхождение часов ${Math.abs(d.offsetSeconds).toFixed(0)}с`, 'warn'));
  conn.on('timeMismatchAlert', d =>
    toast(`⚠️ ${d.pcNumber}: расхождение оффлайн-времени (клиент ${d.clientSecs}с, сервер ${d.serverSecs}с)`, 'warn'));
  conn.on('nameConflictAlert', d => showNameConflict(d));
  conn.on('numberConflictAlert', d => showNumberConflict(d));
  conn.on('settingsUpdated', s => { settings = s; fillSettingsForm(); });
  conn.on('clientLogs', d => showClientLogs(d.pcNumber, d.logContent));

  conn.onreconnected(() => {
    setConnStatus(true);
    conn.invoke('RequestSnapshot');
    loadSettings();
    loadOperators();
  });
  conn.onclose(() => {
    setConnStatus(false);
    waitForServerAndReload();
  });

  conn.start().then(() => setConnStatus(true)).catch(() => { setConnStatus(false); waitForServerAndReload(); });
}

function waitForServerAndReload() {
  const interval = setInterval(async () => {
    try {
      const r = await fetch('/api/admin/check', { cache: 'no-store' });
      if (r.ok) { clearInterval(interval); window.location.reload(); }
    } catch (e) { /* сервер ещё не поднялся */ }
  }, 3000);
}

function setConnStatus(ok) {
  const el = document.getElementById('connStatus');
  el.textContent = ok ? '🟢 Подключено' : '🔴 Нет связи';
  el.style.color = ok ? '#1d9e75' : '#f87171';
}

// ─── Browser notifications ───────────────────────────────────────────────────
let _notifDuration = parseInt(localStorage.getItem('bibNotifDuration') || '8', 10);

function bibNotify(title, body) {
  if (!('Notification' in window) || Notification.permission !== 'granted') return;
  const n = new Notification(title, { body, icon: '/favicon.ico' });
  n.onclick = () => { window.focus(); n.close(); };
  if (_notifDuration > 0) setTimeout(() => n.close(), _notifDuration * 1000);
}

function saveNotifDuration() {
  const v = parseInt(document.getElementById('notifDurationInput').value || '8', 10);
  _notifDuration = Math.max(1, Math.min(60, isNaN(v) ? 8 : v));
  localStorage.setItem('bibNotifDuration', _notifDuration);
  document.getElementById('notifDurationInput').value = _notifDuration;
}

function updateNotifBtn() {
  const btn    = document.getElementById('notifPermBtn');
  const status = document.getElementById('notifPermStatus');
  if (!btn || !status) return;
  const durInp = document.getElementById('notifDurationInput');
  if (durInp) durInp.value = _notifDuration;
  if (!('Notification' in window)) {
    status.textContent = 'Браузер не поддерживает уведомления';
    status.style.color = '#888';
    return;
  }
  const p = Notification.permission;
  if (p === 'granted') {
    btn.style.display = 'none';
    status.style.color = '#1d9e75';
    status.textContent = '✓ Уведомления включены';
  } else if (p === 'denied') {
    btn.style.display = 'none';
    status.style.color = '#f87171';
    status.textContent = 'Заблокированы — разрешите в настройках браузера';
  } else {
    btn.style.display = '';
    status.textContent = '';
  }
}

async function requestNotifications() {
  if (!('Notification' in window)) return;
  await Notification.requestPermission();
  updateNotifBtn();
}

// ─── Navigation ─────────────────────────────────────────────────────────────
function showSettingsTab(name) {
  document.querySelectorAll('.stab').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.stab-panel').forEach(p => p.classList.remove('active'));
  document.querySelector(`.stab[onclick*="'${name}'"]`).classList.add('active');
  document.getElementById('stab-' + name).classList.add('active');
}

function showPage(name) {
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
  document.getElementById('page-' + name).classList.add('active');
  document.querySelector(`[data-page="${name}"]`).classList.add('active');
  if (name === 'readers') loadReaders();
  if (name === 'stats') initStatsPage();
}

// Event delegation для таблицы читателей — вешается один раз при загрузке
(function() {
  document.addEventListener('DOMContentLoaded', function() {
    var el = document.getElementById('readersTable');
    if (!el) return;
    el.addEventListener('click', function(e) {
      var btn = e.target.closest('[data-action]');
      if (!btn) return;
      if (btn.dataset.action === 'edit') openEditReader(btn.dataset.id);
      else if (btn.dataset.action === 'del') deleteReader(btn.dataset.id, btn.dataset.name);
    });
  });
})();

// ─── PC Grid ──────────────────────────────────────────────────────────────
let _adminFilterState = 'all';

function setAdminFilter(state) {
  _adminFilterState = state;
  ['all','free','session','offline'].forEach(s => {
    document.getElementById('chip' + s.charAt(0).toUpperCase() + s.slice(1))
      ?.classList.toggle('active', s === state);
  });
  filterPcGrid();
}

function filterPcGrid() {
  const query = (document.getElementById('pcSearch')?.value || '').toLowerCase();
  document.querySelectorAll('#pcGrid .pccard').forEach(card => {
    const pcNumber = card.dataset.pcnumber || '';
    const c = pcs[pcNumber];
    if (!c) { card.style.display = ''; return; }

    let matchFilter = true;
    if (_adminFilterState === 'free')    matchFilter = c.isFree && c.isOnline;
    else if (_adminFilterState === 'session') matchFilter = c.isSession;
    else if (_adminFilterState === 'offline') matchFilter = !c.isOnline;

    const matchSearch = !query ||
      (c.pcNumber || '').toLowerCase().includes(query) ||
      (c.customName || '').toLowerCase().includes(query) ||
      (c.ip || '').includes(query) ||
      (c.userName || '').toLowerCase().includes(query);

    card.style.display = (matchFilter && matchSearch) ? '' : 'none';
  });
}

function renderPcGrid() {
  const grid = document.getElementById('pcGrid');
  const sortMode = settings.clientSortMode || 'ByNumber';
  let list = Object.values(pcs);
  if (sortMode === 'ByName') {
    list.sort((a, b) => {
      const an = (a.customName || '').toLowerCase() || '￿';
      const bn = (b.customName || '').toLowerCase() || '￿';
      if (an !== bn) return an.localeCompare(bn);
      return a.pcNumberValue - b.pcNumberValue;
    });
  } else {
    list.sort((a, b) => a.pcNumberValue !== b.pcNumberValue ? a.pcNumberValue - b.pcNumberValue : (a.customName || '').localeCompare(b.customName || ''));
  }

  let sessions = 0, free = 0, offline = 0;
  list.forEach(c => {
    if (c.isSession) sessions++;
    if (c.isFree && c.isOnline) free++;
    if (!c.isOnline) offline++;
  });
  // Update chip counts
  const chipAllN = document.getElementById('chipAllN');
  const chipFreeN = document.getElementById('chipFreeN');
  const chipSessionN = document.getElementById('chipSessionN');
  const chipOfflineN = document.getElementById('chipOfflineN');
  if (chipAllN)     chipAllN.textContent     = list.length;
  if (chipFreeN)    chipFreeN.textContent    = free;
  if (chipSessionN) chipSessionN.textContent = sessions;
  if (chipOfflineN) chipOfflineN.textContent = offline;

  // Legacy pcStats for compat
  const pcStats = document.getElementById('pcStats');
  if (pcStats) pcStats.textContent = `Всего: ${list.length} | Сессий: ${sessions} | Свободных: ${free}`;

  grid.innerHTML = '';
  list.forEach(c => grid.appendChild(buildPcCard(c)));

  filterPcGrid();
  renderUpdatePanel(list);
  if (selectedAdminPc) renderAdminActionBar();
}

function renderUpdatePanel(list) {
  const panel = document.getElementById('updateProgressPanel');
  if (!panel) return;
  const updating = list.filter(c => c.updateStatus);
  if (!updating.length) { panel.style.display = 'none'; updatePanelDismissed = false; return; }
  if (updatePanelDismissed) return;

  const done     = updating.filter(c => c.updateStatus === 'done').length;
  const failed   = updating.filter(c => c.updateStatus === 'failed').length;
  const deferred = updating.filter(c => c.updateStatus === 'deferred').length;
  const active   = updating.length - deferred;
  const finished = done + failed;
  const pct = active ? Math.round(finished / active * 100) : 0;

  document.getElementById('updateProgressBar').style.width = pct + '%';
  document.getElementById('updateProgressTitle').textContent =
    `Обновление клиентов: ${finished}/${active}` + (deferred ? ` (${deferred} после сессии)` : '');

  const stLabel = { pending: 'Ожидание', updating: '🔄 Устанавливает...', done: '✅ Обновлён', failed: '❌ Не обновился', deferred: '⏸ После сессии' };
  document.getElementById('updateProgressList').innerHTML = updating.map(c => {
    const verText = c.preUpdateVersion
      ? `v${c.preUpdateVersion} → v${latestClientVersion || '?'}`
      : (c.clientVersion ? `v${c.clientVersion}` : '');
    return `<div class="update-progress-row">
      <span class="upd-pc">${esc(c.pcNumber)}</span>
      <span class="upd-ver">${verText}</span>
      <span class="upd-st ${c.updateStatus}">${stLabel[c.updateStatus] || ''}</span>
    </div>`;
  }).join('');

  panel.style.display = 'block';
}

function closeUpdatePanel() {
  updatePanelDismissed = true;
  document.getElementById('updateProgressPanel').style.display = 'none';
}

function getStatusKey(c) {
  if (!c.isOnline) return 'offline';
  if (c.isSession && c.isPaused) return 'pause';
  if (c.isSession && c.sessionType === 'VIP') return 'vip';
  if (c.isSession) return 'limit';
  if (c.isFree) return 'free';
  return 'locked';
}

function buildPcCard(c) {
  const div = document.createElement('div');
  const stKey = getStatusKey(c);

  div.className = 'pccard' + (!c.isOnline ? ' is-offline' : '');
  if (selectedAdminPc === c.pcNumber) div.classList.add('is-selected');
  div.id = 'pc-' + c.pcNumber.replace(/\s/g, '_');
  div.dataset.pcnumber = c.pcNumber;
  div.style.setProperty('--st', `var(--${stKey})`);

  div.addEventListener('contextmenu', e => { e.preventDefault(); showCtxMenu(e.clientX, e.clientY, c); });
  div.addEventListener('click', e => {
    if (!e.target.closest('button')) selectAdminPc(c.pcNumber);
  });

  // Header
  const indBadge = c.hasIndividualSettings
    ? `<span class="pc-ind-badge" title="Индивидуальные настройки">★</span>`
    : '';
  const badge = `<span class="badge" style="color:var(--${stKey});background:var(--${stKey}-bg);border-color:var(--${stKey}-ring)"><span class="dot" style="background:var(--${stKey})"></span>${esc(c.status)}</span>`;

  const head = `<div class="pccard-stripe"></div>
    <div class="pccard-head">
      <div class="pccard-title">
        <span class="pccard-name" onclick="event.stopPropagation();openRename(${c.pcNumberValue},'${esc(c.customName)}')">${esc(c.pcNumber)}${indBadge}</span>
        ${c.ip ? `<span class="pccard-ip">${esc(c.ip)}</span>` : ''}
      </div>
      <div class="pccard-head-right">
        ${badge}
        <button class="pc-menu-btn" data-pcnumber="${esc(c.pcNumber)}" title="Меню">⋮</button>
      </div>
    </div>`;

  // Body
  let body = '';
  if (c.isSession) {
    const isLow = c.sessionType === 'Лимит' && c.limitSeconds > 0 && Math.max(0, c.limitSeconds - c.elapsedSeconds) <= 300;
    const timerId = 'timer-' + c.pcNumber.replace(/\s/g, '_');
    const clockCls = 'sess-clock mono' + (isLow ? ' low' : '');

    const nameLabel = c.userName || (c.readerId ? `🪪 ${c.readerId}` : '');
    const userLine = nameLabel
      ? `<div class="sess-user"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg><span class="sess-user-name">${esc(nameLabel)}</span></div>`
      : '';

    let remainText = '';
    if (c.limitSeconds > 0) {
      const rem = Math.max(0, c.limitSeconds - c.elapsedSeconds);
      const remStyle = rem <= 300 ? 'style="color:var(--locked)"' : '';
      remainText = `<span class="sess-clock-cap" ${remStyle}>/ ${fmtTime(c.limitSeconds)}</span>`;
    }

    const costLine = c.sessionType === 'VIP'
      ? `<div style="font-size:12px;font-weight:700;color:var(--vip);margin-top:2px">К оплате: ${Math.floor(c.elapsedSeconds * (settings.tariff || 3000) / 3600).toLocaleString()} сум</div>`
      : '';
    const offlineWarn = !c.isOnline
      ? `<div style="font-size:12px;color:var(--locked);font-weight:700;margin-top:2px">📵 нет связи</div>`
      : '';

    const isOutdated = c.clientVersion && latestClientVersion && c.clientVersion !== latestClientVersion;
    const updBadgeMap = { pending: '⏳', updating: '🔄', done: '✅', failed: '❌', deferred: '⏸' };
    const updBadgeLbl = { pending: 'Ожидание', updating: 'Устанавливает...', done: 'Обновлён', failed: 'Не обновился', deferred: 'После сессии' };
    const updBadge = c.updateStatus
      ? `<div style="margin-top:4px"><span class="pc-update-badge ${c.updateStatus}">${updBadgeMap[c.updateStatus]} ${updBadgeLbl[c.updateStatus]}</span></div>`
      : (isOutdated ? `<div style="margin-top:4px"><span class="pc-update-badge pending">⬆ v${latestClientVersion} доступно</span></div>` : '');

    const pauseIco = c.isPaused
      ? `<svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor"><polygon points="6 4 20 12 6 20 6 4"/></svg>`
      : `<svg width="13" height="13" viewBox="0 0 24 24" fill="none"><rect x="7" y="5" width="3.5" height="14" rx="1" fill="currentColor"/><rect x="13.5" y="5" width="3.5" height="14" rx="1" fill="currentColor"/></svg>`;

    body = `<div class="pccard-body">
      ${userLine}
      <div class="sess-timer">
        <span class="${clockCls}" id="${timerId}">${fmtTime(c.elapsedSeconds)}</span>
        ${remainText}
      </div>
      ${costLine}${offlineWarn}${updBadge}
      <div class="pccard-actions">
        <button class="qbtn qbtn-ghost" title="Экран" onclick="event.stopPropagation();openScreenView('${esc(c.pcNumber)}')"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg></button>
        <button class="qbtn qbtn-ghost" title="${c.isPaused ? 'Продолжить' : 'Пауза'}" onclick="event.stopPropagation();togglePause('${esc(c.pcNumber)}')">${pauseIco}</button>
        <button class="qbtn qbtn-danger qbtn-grow" onclick="event.stopPropagation();endSession('${esc(c.pcNumber)}')" title="Завершить сессию"><svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><rect x="4" y="4" width="16" height="16" rx="2"/></svg>Завершить</button>
      </div>
    </div>`;
  } else {
    const stMark = !c.isOnline ? 'state-mark offline' : c.isFree ? 'state-mark free' : 'state-mark locked';
    let icon = '';
    if (!c.isOnline) {
      icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><line x1="1" y1="1" x2="23" y2="23"/><path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55"/><path d="M5 12.55a10.94 10.94 0 0 1 5.17-2.39"/><path d="M10.71 5.05A16 16 0 0 1 22.56 9"/><path d="M1.42 9a15.91 15.91 0 0 1 4.7-2.88"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><line x1="12" y1="20" x2="12.01" y2="20"/></svg>`;
    } else if (c.isFree) {
      icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><polyline points="4 12 9 17 20 6"/></svg>`;
    } else {
      icon = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>`;
    }
    const stateLabel = !c.isOnline ? 'Нет связи' : c.isFree ? 'Готов к работе' : 'Заблокирован';

    const freeActions = c.isOnline
      ? `<div class="pccard-actions">
          <button class="qbtn qbtn-ghost" title="Экран" onclick="event.stopPropagation();openScreenView('${esc(c.pcNumber)}')"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg></button>
          <button class="qbtn qbtn-accent qbtn-grow" onclick="event.stopPropagation();openStartSession('${esc(c.pcNumber)}')"><svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><polygon points="6 4 20 12 6 20 6 4"/></svg>Начать сессию</button>
        </div>`
      : `<div class="pccard-actions">
          <button class="qbtn qbtn-danger qbtn-grow" onclick="event.stopPropagation();deletePc('${esc(c.pcNumber)}')">🗑 Удалить</button>
        </div>`;

    body = `<div class="pccard-body pccard-body-state">
      <div class="${stMark}">${icon}</div>
      <span class="state-text">${stateLabel}</span>
      ${freeActions}
    </div>`;
  }

  div.innerHTML = head + body;
  return div;
}

// ─── Bottom action bar ───────────────────────────────────────────────────────
function selectAdminPc(pcNumber) {
  if (!pcNumber || selectedAdminPc === pcNumber) {
    selectedAdminPc = null;
    document.querySelectorAll('#pcGrid .pccard.is-selected').forEach(c => c.classList.remove('is-selected'));
    document.getElementById('adminActionBar').classList.add('hidden');
    return;
  }
  selectedAdminPc = pcNumber;
  document.querySelectorAll('#pcGrid .pccard.is-selected').forEach(c => c.classList.remove('is-selected'));
  const card = document.querySelector(`#pcGrid [data-pcnumber="${CSS.escape(pcNumber)}"]`);
  if (card) card.classList.add('is-selected');
  renderAdminActionBar();
}

function renderAdminActionBar() {
  const ab = document.getElementById('adminActionBar');
  const pc = pcs[selectedAdminPc];
  if (!pc) { ab.classList.add('hidden'); return; }

  document.getElementById('aabPcName').textContent = pc.pcNumber;
  document.getElementById('aabStatus').textContent = pc.status;

  const p = pc.pcNumber;
  const btns = [];

  if (!pc.isOnline) {
    btns.push(`<button class="ab-btn red" onclick="deletePc('${esc(p)}')">🗑 Удалить</button>`);
  } else {
    btns.push(`<button class="ab-btn" style="background:#374151;border-color:#4b5563" onclick="openScreenView('${esc(p)}')">👁 Экран</button>`);
    if (!pc.isSession && !pc.isFree) {
      btns.push(`<button class="ab-btn green" onclick="openStartSession('${esc(p)}')">▶ Начать сессию</button>`);
      btns.push(`<button class="ab-btn" style="background:#1A3A1A;border-color:#2A5A2A;color:#90E090" onclick="unlock('${esc(p)}')">🔓 Разблокировать</button>`);
    }
    if (!pc.isSession && pc.isFree) {
      btns.push(`<button class="ab-btn green" onclick="openStartSession('${esc(p)}')">▶ Начать сессию</button>`);
      btns.push(`<button class="ab-btn" style="background:#3A1A1A;border-color:#5A2A2A;color:#E09090" onclick="lock('${esc(p)}')">🔒 Заблокировать</button>`);
    }
    if (pc.isSession) {
      const pauseLabel = pc.isPaused ? '▶ Продолжить' : '⏸ Пауза';
      const pauseCls   = pc.isPaused ? 'green' : 'amber';
      btns.push(`<button class="ab-btn ${pauseCls}" onclick="togglePause('${esc(p)}')">${pauseLabel}</button>`);
      btns.push(`<button class="ab-btn blue" onclick="openTransfer('${esc(p)}')">↔ Пересадить</button>`);
      if (pc.sessionType === 'Лимит') {
        btns.push(`<button class="ab-btn blue" onclick="openExtend('${esc(p)}')">+⏱ Время</button>`);
        btns.push(`<button class="ab-btn red" onclick="openSubtract('${esc(p)}')">−⏱ Убрать</button>`);
      }
      btns.push(`<button class="ab-btn red" onclick="openPenalty('${esc(p)}')">⚠ Штраф</button>`);
      btns.push(`<button class="ab-btn red" onclick="endSession('${esc(p)}')">⏹ Завершить</button>`);
    }
  }

  document.getElementById('aabActions').innerHTML = btns.join('');
  ab.classList.remove('hidden');
}

// ─── Timers ──────────────────────────────────────────────────────────────────
function tickTimers() {
  if (document.getElementById('dlgStartSession')?.classList.contains('open')) ssUpdateEndTimeHint();

  Object.values(pcs).forEach(c => {
    // Тикаем таймер если сессия активна (включая оффлайн-ПК с сессией — таймер идёт)
    if (!c.isSession || c.isPaused) return;
    c.elapsedSeconds++;
    const el = document.getElementById('timer-' + c.pcNumber.replace(/\s/g, '_'));
    if (el) el.textContent = fmtTime(c.elapsedSeconds);
  });
}

// ─── Session actions ─────────────────────────────────────────────────────────
let _ssType = 'Лимит';
let _ssSyncing = false;
let _ssLookupState = null;  // null | 'not_found' | 'expired' | 'valid'
let _ssLookedUpId  = '';
let _ssLookupInFlight = null;  // deduplicate concurrent lookups
let _ssLookupTimer    = null;  // debounce timer for auto-lookup on input

function _ssParseDate(str) {
  if (!str) return null;
  const p = str.split('-');
  if (p.length === 3) return new Date(+p[2], +p[1] - 1, +p[0]);
  const d = new Date(str);
  return isNaN(d) ? null : d;
}

// Returns the date from which card validity (3 years) is counted
function _ssCardBaseDate(data) {
  return _ssParseDate(data.updatedAt) || _ssParseDate(data.registeredAt);
}

function ssOnCardTypeChanged() {
  const isTemp = document.querySelector('[name="ssCardType"]:checked')?.value === 'temp';
  const prefix = document.getElementById('dlgSsReaderPrefix');
  if (prefix) prefix.textContent = isTemp ? '№' : (settings.readerCardPrefix || 'FAA');
  const rowName = document.getElementById('rowSsName');
  if (rowName) rowName.style.display = (isTemp || !settings.requireUserName) ? 'none' : '';
  _ssLookupState = null;
  _ssLookedUpId  = '';
  document.getElementById('dlgSsReader').value = '';
  document.getElementById('dlgSsReaderInfo').className = 'reader-info';
  document.getElementById('dlgSsName').value = '';
  document.getElementById('dlgSsReader').placeholder = isTemp ? '842' : '260500456';
}

// Вызывается из oninput поля читательского билета — фильтрует цифры + debounce поиск
function onSsReaderInput() {
  const el = document.getElementById('dlgSsReader');
  el.value = el.value.replace(/\D/g, '').slice(0, 9);
  _ssLookupState = null;
  clearTimeout(_ssLookupTimer);
  const nums = el.value;
  if (nums.length >= 6) {
    _ssLookupTimer = setTimeout(ssLookupReader, 500);
  } else {
    document.getElementById('dlgSsReaderInfo').className = 'reader-info';
    document.getElementById('dlgSsName').value = '';
  }
}

// Deduplication wrapper — prevents two concurrent lookups (blur + button click)
async function ssQuickAddReader(cardId) {
  const infoEl = document.getElementById('dlgSsReaderInfo');
  infoEl.className = 'reader-info valid';
  infoEl.textContent = 'Добавление…';
  try {
    const r = await fetch('/api/admin/readers/quick-add', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cardId })
    });
    if (r.ok) {
      _ssLookupState = 'valid';
      _ssLookedUpId  = cardId;
      infoEl.className = 'reader-info valid';
      infoEl.textContent = `✓ ${cardId} — добавлен как новый читатель`;
      toast('Читатель добавлен', 'success');
    } else {
      toast('Ошибка добавления', 'warn');
    }
  } catch { toast('Ошибка добавления', 'warn'); }
}

async function ssLookupReader() {
  if (_ssLookupInFlight) { await _ssLookupInFlight; return; }
  // Не перезапускать если результат уже известен для этого ID
  const nums = document.getElementById('dlgSsReader').value.trim();
  const prefix = settings.readerCardPrefix || 'FAA';
  const isTemp = document.querySelector('[name="ssCardType"]:checked')?.value === 'temp';
  const currentId = isTemp ? nums : (prefix + nums);
  if (_ssLookupState !== null && _ssLookedUpId === currentId) return;
  _ssLookupInFlight = _ssLookupReaderImpl();
  try { await _ssLookupInFlight; } finally { _ssLookupInFlight = null; }
}

async function _ssLookupReaderImpl() {
  const nums   = document.getElementById('dlgSsReader').value.trim();
  const infoEl = document.getElementById('dlgSsReaderInfo');
  if (!nums) { infoEl.className = 'reader-info'; _ssLookupState = null; return; }

  const isTemp = document.querySelector('[name="ssCardType"]:checked')?.value === 'temp';
  if (isTemp) {
    _ssLookupState = 'valid';
    _ssLookedUpId  = nums;
    infoEl.className = 'reader-info valid';
    infoEl.textContent = `✓ Временный билет №${nums} — посещение будет зафиксировано`;
    return;
  }

  const prefix = settings.readerCardPrefix || 'FAA';
  const cardId = prefix + nums;
  _ssLookedUpId = cardId;
  try {
    const r = await fetch(`/api/readers/lookup/${encodeURIComponent(cardId)}`);
    if (!r.ok) {
      _ssLookupState = 'not_found';
      document.getElementById('dlgSsName').value = '';
      infoEl.className = 'reader-info invalid';
      infoEl.innerHTML = `<span style="flex:1">✗ Читатель ${esc(cardId)} не найден в базе</span>
        <button data-quick-add="${esc(cardId)}"
          style="padding:3px 10px;font-size:11px;border-radius:5px;cursor:pointer;border:1px solid var(--free-ring);background:var(--free-bg);color:var(--free);white-space:nowrap">
          + Добавить
        </button>`;
      infoEl.querySelector('[data-quick-add]').addEventListener('click', function() {
        ssQuickAddReader(this.dataset.quickAdd);
      });
      return;
    }
    const data = await r.json();
    const baseDate = _ssCardBaseDate(data);
    if (baseDate) {
      const expDate = new Date(baseDate);
      expDate.setFullYear(expDate.getFullYear() + 3);
      if (Date.now() > expDate) {
        _ssLookupState = 'expired';
        document.getElementById('dlgSsName').value = data.fullName || '';
        infoEl.className = 'reader-info expired';
        infoEl.textContent = `⚠ ${data.fullName} · Билет просрочен с ${expDate.toLocaleDateString('ru-RU')}`;
        return;
      }
    }
    _ssLookupState = 'valid';
    document.getElementById('dlgSsName').value = data.fullName || '';
    infoEl.className = 'reader-info valid';
    infoEl.textContent = `✓ ${data.fullName}${data.category ? ' · ' + data.category : ''}`;
  } catch {
    _ssLookupState = null;
    infoEl.className = 'reader-info';
  }
}

function openStartSession(pcNumber) {
  activePc = pcNumber;
  document.getElementById('dlgSsPc').textContent = pcNumber;
  document.getElementById('dlgSsReader').value = '';
  document.getElementById('dlgSsReader').placeholder = '260500456';
  document.getElementById('dlgSsName').value = '';
  document.getElementById('dlgSsHours').value = '';
  document.getElementById('dlgSsMins').value = '';
  document.getElementById('dlgSsMoney').value = '';
  document.getElementById('dlgSsHint').textContent = '';
  document.getElementById('dlgSsReaderInfo').className = 'reader-info';

  // Reset card type to regular
  const regularRadio = document.querySelector('[name="ssCardType"][value="regular"]');
  if (regularRadio) regularRadio.checked = true;
  document.getElementById('ssBtnCardRegular')?.classList.toggle('on', true);
  document.getElementById('ssBtnCardTemp')?.classList.toggle('on', false);
  const prefixEl = document.getElementById('dlgSsReaderPrefix');
  if (prefixEl) prefixEl.textContent = settings.readerCardPrefix || 'FAA';
  _ssLookupState = null;
  _ssLookedUpId  = '';

  // Show/hide reader ID row
  const rowReader = document.getElementById('rowSsReader');
  if (rowReader) rowReader.style.display = settings.requireReaderId ? '' : 'none';

  // Show/hide name row
  const rowName = document.getElementById('rowSsName');
  if (rowName) rowName.style.display = settings.requireUserName ? '' : 'none';

  ssSelectType('Лимит');
  ssUpdateEndTimeHint();
  document.getElementById('dlgStartSession').classList.add('open');
}

function ssSelectType(type) {
  _ssType = type;
  const isLimit = type === 'Лимит';
  document.getElementById('dlgSsLimitFields').style.display = isLimit ? '' : 'none';
  document.getElementById('dlgSsVipInfo').style.display     = isLimit ? 'none' : '';
  // Segment control
  document.getElementById('ssBtnLimited').classList.toggle('on', isLimit);
  document.getElementById('ssBtnVip').classList.toggle('on', !isLimit);
  if (!isLimit) {
    document.getElementById('dlgSsHours').value = '';
    document.getElementById('dlgSsMins').value = '';
    document.getElementById('dlgSsMoney').value = '';
    document.getElementById('dlgSsHint').textContent = '';
    const wh = document.getElementById('dlgSsWorkdayHint');
    if (wh) wh.style.display = 'none';
  }
  ssUpdateEndTimeHint();
}

function _fmtHM(h, m) {
  if (h > 0 && m > 0) return `${h} ч ${m} мин`;
  if (h > 0) return `${h} ч`;
  return `${m} мин`;
}

function _fmtClock(date) {
  return date.getHours().toString().padStart(2, '0') + ':' + date.getMinutes().toString().padStart(2, '0');
}

function ssUpdateEndTimeHint() {
  const hint = document.getElementById('dlgSsEndTimeHint');
  if (!hint) return;
  if (_ssType !== 'Лимит') { hint.style.display = 'none'; return; }
  const h = parseInt(document.getElementById('dlgSsHours').value) || 0;
  const m = parseInt(document.getElementById('dlgSsMins').value)  || 0;
  const totalMins = h * 60 + m;
  if (!totalMins) { hint.style.display = 'none'; return; }
  hint.textContent = 'Сессия закончится в ' + _fmtClock(new Date(Date.now() + totalMins * 60000));
  hint.style.display = '';
}

// Возвращает оставшиеся минуты до конца рабочего дня, или null если ограничение не задано
function _ssWorkdayRemaining() {
  const end = (settings.workdayEnd || '').trim();
  if (!end) return null;
  const parts = end.split(':');
  if (parts.length < 2) return null;
  const endH = parseInt(parts[0]), endM = parseInt(parts[1]);
  if (isNaN(endH) || isNaN(endM)) return null;
  const now = new Date();
  const remaining = (endH * 60 + endM) - (now.getHours() * 60 + now.getMinutes());
  return remaining > 0 ? remaining : null;
}

function _ssApplyWorkdayCap(totalMins) {
  const capHint = document.getElementById('dlgSsWorkdayHint');
  const cap = _ssWorkdayRemaining();
  if (!cap || totalMins <= 0 || totalMins <= cap) {
    if (capHint) capHint.style.display = 'none';
    return totalMins;
  }
  const t = GlobalSettings_Tariff();
  const cappedAmount = Math.round((cap / 60) * t);
  if (capHint) {
    capHint.textContent = `До конца рабочего дня (${(settings.workdayEnd || '')}) — ${cap} мин = ${cappedAmount.toLocaleString()} сум`;
    capHint.style.display = '';
  }
  return cap;
}

// Синхронизация часы/минуты → деньги
function ssSyncMinutes() {
  if (_ssSyncing) return;
  _ssSyncing = true;
  try {
    const h = parseInt(document.getElementById('dlgSsHours').value) || 0;
    const m = parseInt(document.getElementById('dlgSsMins').value)  || 0;
    let totalMins = h * 60 + m;
    const capped = _ssApplyWorkdayCap(totalMins);
    if (capped !== totalMins) {
      totalMins = capped;
      document.getElementById('dlgSsHours').value = Math.floor(totalMins / 60) || '';
      document.getElementById('dlgSsMins').value  = totalMins % 60 || '';
    }
    const t = GlobalSettings_Tariff();
    if (totalMins > 0) {
      const cost = Math.round((totalMins / 60) * t);
      document.getElementById('dlgSsMoney').value = cost;
      document.getElementById('dlgSsHint').textContent = `${_fmtHM(Math.floor(totalMins/60), totalMins%60)} = ${cost.toLocaleString()} сум`;
    } else {
      document.getElementById('dlgSsMoney').value = '';
      document.getElementById('dlgSsHint').textContent = '';
    }
  } finally { _ssSyncing = false; }
  ssUpdateEndTimeHint();
}

// Синхронизация деньги → часы/минуты
function ssSyncMoney() {
  if (_ssSyncing) return;
  _ssSyncing = true;
  try {
    const money = parseFloat(document.getElementById('dlgSsMoney').value);
    const t = GlobalSettings_Tariff();
    if (money > 0) {
      let totalMins = Math.round((money / t) * 60);
      const capped = _ssApplyWorkdayCap(totalMins);
      if (capped !== totalMins) {
        totalMins = capped;
        document.getElementById('dlgSsMoney').value = Math.round((totalMins / 60) * t);
      }
      const h = Math.floor(totalMins / 60);
      const m = totalMins % 60;
      document.getElementById('dlgSsHours').value = h || '';
      document.getElementById('dlgSsMins').value  = m || '';
      document.getElementById('dlgSsHint').textContent = totalMins > 0
        ? `${document.getElementById('dlgSsMoney').value} сум = ${_fmtHM(h, m)}` : '';
    } else {
      document.getElementById('dlgSsHours').value = '';
      document.getElementById('dlgSsMins').value  = '';
      document.getElementById('dlgSsHint').textContent = '';
      const capHint = document.getElementById('dlgSsWorkdayHint');
      if (capHint) capHint.style.display = 'none';
    }
  } finally { _ssSyncing = false; }
  ssUpdateEndTimeHint();
}

async function confirmStartSession() {
  const isTemp  = document.querySelector('[name="ssCardType"]:checked')?.value === 'temp';
  const nums    = document.getElementById('dlgSsReader').value.trim();

  const prefix = settings.readerCardPrefix || 'FAA';
  const reader = isTemp ? nums : (prefix + nums);

  if (settings.requireReaderId) {
    if (!nums) { toast('Введите номер читательского билета', 'warn'); return; }
    if (!isTemp) {
      if (_ssLookupState === null || _ssLookedUpId !== reader) await ssLookupReader();
      if (_ssLookupState === 'not_found') { toast('Читатель не найден в базе', 'warn'); return; }
      if (_ssLookupState === 'expired')   { toast('Читательский билет просрочен', 'warn'); return; }
      if (_ssLookupState !== 'valid')     { toast('Проверьте номер читательского билета', 'warn'); return; }
    }
  }
  const name = document.getElementById('dlgSsName').value.trim();
  if (settings.requireUserName && !name) { toast('Введите имя пользователя', 'warn'); return; }

  let limitSeconds = 0, paidAmount = 0;
  if (_ssType === 'Лимит') {
    const h     = parseInt(document.getElementById('dlgSsHours').value) || 0;
    const m     = parseInt(document.getElementById('dlgSsMins').value)  || 0;
    const mins  = h * 60 + m;
    const money = parseFloat(document.getElementById('dlgSsMoney').value) || 0;
    if (!mins && !money) { toast('Введите время или сумму', 'warn'); return; }
    const t = GlobalSettings_Tariff();
    if (mins > 0) {
      limitSeconds = Math.round(mins * 60);
      paidAmount   = money || Math.round((mins / 60) * t);
    } else {
      paidAmount   = money;
      limitSeconds = Math.round((money / t) * 3600);
    }
  }
  closeDlg('dlgStartSession');
  await conn.invoke('StartSession', activePc, _ssType, limitSeconds, paidAmount, name, reader);
}

function GlobalSettings_Tariff() { return settings.tariff || 3000; }

const _manuallyEndedPcs = new Set();

async function endSession(pcNumber) {
  _manuallyEndedPcs.add(pcNumber);
  await conn.invoke('EndSession', pcNumber);
}

async function togglePause(pcNumber) {
  await conn.invoke('TogglePause', pcNumber);
}

async function lock(pcNumber) {
  await conn.invoke('SendCommandToPc', pcNumber, 'REMOTE_LOCK', 'true');
}

async function unlock(pcNumber) {
  await conn.invoke('SendCommandToPc', pcNumber, 'REMOTE_UNLOCK', '');
}

async function lockAll() {
  await conn.invoke('SendCommandToAll', 'REMOTE_LOCK', 'true');
}

async function unlockAll() {
  await conn.invoke('SendCommandToAll', 'REMOTE_UNLOCK', '');
}

async function shutdownAll() {
  if (!confirm('Выключить все ПК?')) return;
  await conn.invoke('SendCommandToAll', 'SHUTDOWN', 'true');
  toast('Команда выключения отправлена всем ПК');
}

async function restartAll() {
  if (!confirm('Перезагрузить все ПК?')) return;
  await conn.invoke('SendCommandToAll', 'RESTART', 'true');
  toast('Команда перезагрузки отправлена всем ПК');
}

async function updateAllClients() {
  if (!confirm('Отправить команду обновления всем клиентским ПК?\n\nОни автоматически скачают и тихо установят новую версию BibClient. Клиенты перезапустятся сами.')) return;
  const btn = document.getElementById('btnUpdateClients');
  const statusEl = document.getElementById('updateStatusClients');
  btn.disabled = true;
  btn.textContent = '⏳ Отправка...';
  updatePanelDismissed = false;
  // Перечитываем актуальную версию с сервера перед показом панели
  try {
    const vr = await fetch('/updates/bibclient-version.json', { cache: 'no-store' });
    if (vr.ok) { const vj = await vr.json(); if (vj?.Version) latestClientVersion = vj.Version; }
  } catch {}
  try {
    await conn.invoke('SendCommandToAll', 'UPDATE_NOW', '');
    toast('Команда обновления отправлена всем ПК', 'good');
    btn.textContent = '✓ Отправлено';
    if (statusEl) { statusEl.style.display = 'inline'; statusEl.style.color = '#1d9e75'; statusEl.textContent = '✓ Команда отправлена'; }
    setTimeout(() => { btn.disabled = false; btn.textContent = '⬆️ Обновить все клиенты (exe)'; if (statusEl) statusEl.style.display = 'none'; }, 5000);
  } catch (e) {
    toast('Ошибка отправки команды', 'error');
    btn.disabled = false;
    btn.textContent = '⬆️ Обновить все клиенты (exe)';
  }
}

// ─── Folder picker ───────────────────────────────────────────────────────────

let _fpTarget   = null;   // ID of input to fill
let _fpCurrent  = '';     // currently shown path
let _fpParent   = null;   // parent of current path (null = we're at drive list)

function openFolderPicker(targetInputId) {
  _fpTarget = targetInputId;
  const initial = document.getElementById(targetInputId)?.value?.trim() || '';
  document.getElementById('folderPickerModal').style.display = 'flex';
  _fpBrowseTo(initial || '');
}

function closeFolderPicker() {
  document.getElementById('folderPickerModal').style.display = 'none';
}

function folderPickerSelect() {
  if (_fpTarget && _fpCurrent) {
    document.getElementById(_fpTarget).value = _fpCurrent;
    if (_fpTarget === 'updateFolderPath')  localStorage.setItem('bib_update_folder',        _fpCurrent);
    if (_fpTarget === 'clientFolderPath')  localStorage.setItem('bib_client_update_folder', _fpCurrent);
  }
  closeFolderPicker();
}

function folderPickerUp() {
  _fpBrowseTo(_fpParent || '');
}

async function _fpBrowseTo(path) {
  const url = '/api/admin/browse' + (path ? '?path=' + encodeURIComponent(path) : '');
  let data;
  try {
    const r = await fetch(url);
    data = await r.json();
    if (!r.ok) { toast(data.error || 'Ошибка доступа к папке', 'error'); return; }
  } catch(e) { toast('Ошибка: ' + e, 'error'); return; }

  _fpCurrent = data.current || '';
  _fpParent  = data.parent  || null;

  document.getElementById('folderPickerPath').textContent = _fpCurrent || 'Выберите диск:';
  document.getElementById('btnFolderUp').disabled = !_fpParent;

  const list = document.getElementById('folderPickerList');
  list.innerHTML = '';

  if (!data.folders || data.folders.length === 0) {
    list.innerHTML = '<div style="padding:16px;color:#555;font-size:13px;text-align:center">Нет вложенных папок</div>';
    return;
  }

  for (const f of data.folders) {
    const fullPath = f.full;
    const name     = f.name;
    const row = document.createElement('div');
    row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:7px 12px;cursor:pointer;border-bottom:1px solid #1a1a2e';
    row.innerHTML = `<span style="font-size:16px">📁</span><span style="font-family:monospace;font-size:13px;color:#ccc">${name}</span>`;
    row.addEventListener('mouseover', () => { if (!row._selected) row.style.background = '#2a2a4a'; });
    row.addEventListener('mouseout',  () => { if (!row._selected) row.style.background = ''; });
    row.addEventListener('click', () => {
      // Single click → select (highlight + set current)
      list.querySelectorAll('div[data-fp]').forEach(d => { d._selected = false; d.style.background = ''; });
      row._selected = true;
      row.style.background = '#3d3d6b';
      _fpCurrent = fullPath;
      document.getElementById('folderPickerPath').textContent = _fpCurrent;
    });
    row.addEventListener('dblclick', () => _fpBrowseTo(fullPath));
    row.setAttribute('data-fp', '1');
    list.appendChild(row);
  }
}

// ─── Update tab switchers ────────────────────────────────────────────────────

function setSrvMode(mode) {
  ['zip', 'folder'].forEach(m => {
    const panel = document.getElementById('srvPanel' + m.charAt(0).toUpperCase() + m.slice(1));
    const tab   = document.getElementById('srvTab'   + m.charAt(0).toUpperCase() + m.slice(1));
    if (panel) panel.style.display = (m === mode) ? '' : 'none';
    if (tab)   { tab.classList.toggle('on', m === mode); tab.style.background = ''; tab.style.color = ''; }
  });
  document.getElementById('serverZipUploadStatus').textContent = '';
  document.getElementById('folderUpdateStatus').textContent = '';
}

function setCliMode(mode) {
  ['zip', 'server', 'folder'].forEach(m => {
    const panel = document.getElementById('cliPanel' + m.charAt(0).toUpperCase() + m.slice(1));
    const tab   = document.getElementById('cliTab'   + m.charAt(0).toUpperCase() + m.slice(1));
    if (panel) panel.style.display = (m === mode) ? '' : 'none';
    if (tab)   { tab.classList.toggle('on', m === mode); tab.style.background = ''; tab.style.color = ''; }
  });
  document.getElementById('clientZipUploadStatus').textContent   = '';
  document.getElementById('clientZipCmdStatus').textContent      = '';
  document.getElementById('clientFolderUpdateStatus').textContent = '';
}

// ─── Server (BibAdminWeb) zip upload from browser ────────────────────────────

function onServerZipSelected(input) {
  const nameEl = document.getElementById('serverZipFileName');
  const btnEl  = document.getElementById('btnUploadServerZip');
  if (input.files && input.files[0]) {
    const mb = (input.files[0].size / 1024 / 1024).toFixed(1);
    nameEl.textContent = `${input.files[0].name} (${mb} MB)`;
    nameEl.style.color = '#ccc';
    btnEl.disabled = false;
  } else {
    nameEl.textContent = 'файл не выбран';
    nameEl.style.color = '#aaa';
    btnEl.disabled = true;
  }
}

async function uploadServerZip() {
  const input = document.getElementById('serverZipFile');
  const statusEl = document.getElementById('serverZipUploadStatus');
  if (!input.files || !input.files[0]) { toast('Выберите zip-файл', 'warn'); return; }

  const file = input.files[0];
  if (!confirm(`Загрузить архив и обновить сервер?\n\nФайл: ${file.name}\n\nСервер перезапустится через несколько секунд. Все подключения временно прервутся.`)) return;

  statusEl.textContent = '⬆️ Загрузка...';
  statusEl.style.color = '#aaa';
  document.getElementById('btnUploadServerZip').disabled = true;

  try {
    const fd = new FormData();
    fd.append('zip', file);
    const r = await fetch('/api/admin/upload-server-zip', { method: 'POST', body: fd });
    const data = await r.json();
    if (!r.ok) {
      statusEl.textContent = data.error || 'Ошибка';
      statusEl.style.color = '#f87171';
      toast(data.error || 'Ошибка загрузки', 'error');
      document.getElementById('btnUploadServerZip').disabled = false;
      return;
    }
    statusEl.textContent = '✓ Загружено, сервер перезапускается...';
    statusEl.style.color = '#1d9e75';
    toast('Архив загружен, сервер перезапускается...', 'good');
  } catch (e) {
    // Сервер мог уже упасть — это нормально при самообновлении
    statusEl.textContent = '✓ Сервер перезапускается...';
    statusEl.style.color = '#1d9e75';
    toast('Сервер перезапускается для применения обновления', 'good');
  }

  // Переподключаемся через 15 секунд
  setTimeout(() => {
    statusEl.textContent = '🔄 Переподключение...';
    window.location.reload();
  }, 15000);
}

// ─── Client zip upload from browser ─────────────────────────────────────────

function onClientZipSelected(input) {
  const nameEl = document.getElementById('clientZipFileName');
  const btnEl  = document.getElementById('btnUploadClientZip');
  if (input.files && input.files[0]) {
    const mb = (input.files[0].size / 1024 / 1024).toFixed(1);
    nameEl.textContent = `${input.files[0].name} (${mb} MB)`;
    nameEl.style.color = '#ccc';
    btnEl.disabled = false;
  } else {
    nameEl.textContent = 'файл не выбран';
    nameEl.style.color = '#aaa';
    btnEl.disabled = true;
  }
}

async function uploadClientZip() {
  const input = document.getElementById('clientZipFile');
  const statusEl = document.getElementById('clientZipUploadStatus');
  if (!input.files || !input.files[0]) { toast('Выберите zip-файл', 'warn'); return; }

  const file = input.files[0];
  if (!confirm(`Загрузить на сервер и разослать обновление всем клиентам?\n\nФайл: ${file.name}\n\nКлиенты скачают архив, перезапустятся и продолжат работу.`)) return;

  // Шаг 1: загружаем zip на сервер
  statusEl.textContent = '⬆️ Загрузка...';
  statusEl.style.color = '#aaa';
  document.getElementById('btnUploadClientZip').disabled = true;

  try {
    const fd = new FormData();
    fd.append('zip', file);
    const r = await fetch('/api/admin/upload-client-zip', { method: 'POST', body: fd });
    const data = await r.json();
    if (!r.ok) {
      statusEl.textContent = data.error || 'Ошибка загрузки';
      statusEl.style.color = '#f87171';
      toast(data.error || 'Ошибка загрузки', 'error');
      document.getElementById('btnUploadClientZip').disabled = false;
      return;
    }
  } catch (e) {
    statusEl.textContent = 'Ошибка: ' + e;
    statusEl.style.color = '#f87171';
    toast('Ошибка загрузки: ' + e, 'error');
    document.getElementById('btnUploadClientZip').disabled = false;
    return;
  }

  // Шаг 2: рассылаем команду
  statusEl.textContent = '📡 Рассылаю команду...';
  try {
    updatePanelDismissed = false;
    await conn.invoke('SendCommandToAll', 'UPDATE_FOLDER_NOW', '');
    statusEl.textContent = '✓ Загружено и разослано';
    statusEl.style.color = '#1d9e75';
    toast('Zip загружен, команда обновления отправлена клиентам', 'good');
    document.getElementById('btnUploadClientZip').disabled = false;
    setTimeout(() => { statusEl.textContent = ''; }, 8000);
  } catch (e) {
    statusEl.textContent = 'Ошибка рассылки: ' + e;
    statusEl.style.color = '#f87171';
    toast('Zip загружен, но рассылка не удалась: ' + e, 'error');
    document.getElementById('btnUploadClientZip').disabled = false;
  }
}

// ─── Send UPDATE_FOLDER_NOW when zip is already on the server ────────────────

async function sendClientZipCommand() {
  const statusEl = document.getElementById('clientZipCmdStatus');
  if (!confirm('Разослать команду zip-обновления всем клиентам?\n\nКлиенты скачают bibclient-update.zip с сервера и перезапустятся.\n\nУбедитесь, что zip актуален (скопирован deploy-update.cmd).')) return;
  statusEl.textContent = '📡 Рассылаю...';
  statusEl.style.color = '#aaa';
  try {
    updatePanelDismissed = false;
    await conn.invoke('SendCommandToAll', 'UPDATE_FOLDER_NOW', '');
    statusEl.textContent = '✓ Команда отправлена';
    statusEl.style.color = '#1d9e75';
    toast('Команда zip-обновления отправлена клиентам', 'good');
    setTimeout(() => { statusEl.textContent = ''; }, 6000);
  } catch (e) {
    statusEl.textContent = 'Ошибка: ' + e;
    statusEl.style.color = '#f87171';
    toast('Ошибка рассылки: ' + e, 'error');
  }
}

// ─── Client folder update (zip, no installer) ────────────────────────────────
async function applyClientFolderUpdate() {
  const pathVal = document.getElementById('clientFolderPath').value.trim();
  if (!pathVal) { toast('Укажите путь к папке с обновлением BibClient', 'warn'); return; }

  localStorage.setItem('bib_client_update_folder', pathVal);

  if (!confirm(`Упаковать папку и разослать обновление всем клиентам?\n\nПапка: ${pathVal}\n\nКлиенты скачают архив, перезапустятся и продолжат работу с новыми файлами.`)) return;

  const statusEl = document.getElementById('clientFolderUpdateStatus');
  statusEl.textContent = '📦 Упаковка...';
  statusEl.style.color = '#aaa';

  // Шаг 1: Упаковываем папку в zip на сервере
  try {
    const r = await fetch('/api/admin/pack-client-update', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sourcePath: pathVal })
    });
    const data = await r.json();
    if (!r.ok) {
      statusEl.textContent = data.error || 'Ошибка упаковки';
      statusEl.style.color = '#f87171';
      toast(data.error || 'Ошибка упаковки', 'error');
      return;
    }
  } catch (e) {
    statusEl.textContent = 'Ошибка: ' + e;
    statusEl.style.color = '#f87171';
    toast('Ошибка упаковки: ' + e, 'error');
    return;
  }

  // Шаг 2: Рассылаем команду UPDATE_FOLDER_NOW всем клиентам
  statusEl.textContent = '📡 Рассылаю команду...';
  try {
    updatePanelDismissed = false;
    await conn.invoke('SendCommandToAll', 'UPDATE_FOLDER_NOW', '');
    statusEl.textContent = '✓ Команда отправлена. Клиенты обновляются...';
    statusEl.style.color = '#1d9e75';
    toast('Команда обновления из папки отправлена клиентам', 'good');
    setTimeout(() => { statusEl.textContent = ''; }, 8000);
  } catch (e) {
    statusEl.textContent = 'Ошибка рассылки: ' + e;
    statusEl.style.color = '#f87171';
    toast('Ошибка рассылки: ' + e, 'error');
  }
}

// ─── Extend session ──────────────────────────────────────────────────────────
let _extTariff = 0;
let _extSyncing = false;

function openExtend(pcNumber) {
  activePc = pcNumber;
  _extTariff = settings?.tariff || 0;
  document.getElementById('dlgExtPc').textContent = pcNumber;
  document.getElementById('dlgExtHours').value = 0;
  document.getElementById('dlgExtMins').value  = 30;
  document.getElementById('dlgExtAmount').value = _extTariff ? Math.round(_extTariff * 30 / 60) : 0;
  document.getElementById('dlgExtend').style.display = 'flex';
}

function calcExtAmount() {
  if (_extSyncing || !_extTariff) return;
  _extSyncing = true;
  const h = parseInt(document.getElementById('dlgExtHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgExtMins').value) || 0);
  document.getElementById('dlgExtAmount').value = Math.round(_extTariff * min / 60);
  _extSyncing = false;
}

function calcExtTime() {
  if (_extSyncing || !_extTariff) return;
  _extSyncing = true;
  const amount = parseInt(document.getElementById('dlgExtAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / _extTariff) || 0;
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
  await conn.invoke('ExtendSession', activePc, min * 60, amount);
}

let _subSyncing = false;

function openSubtract(pcNumber) {
  activePc = pcNumber;
  _extTariff = settings?.tariff || 0;
  document.getElementById('dlgSubPc').textContent = pcNumber;
  document.getElementById('dlgSubHours').value = 0;
  document.getElementById('dlgSubMins').value  = 10;
  document.getElementById('dlgSubAmount').value = _extTariff ? Math.round(_extTariff * 10 / 60) : 0;
  document.getElementById('dlgSubtract').style.display = 'flex';
}

function calcSubAmount() {
  if (_subSyncing || !_extTariff) return;
  _subSyncing = true;
  const h = parseInt(document.getElementById('dlgSubHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgSubMins').value) || 0);
  document.getElementById('dlgSubAmount').value = Math.round(_extTariff * min / 60);
  _subSyncing = false;
}

function calcSubTime() {
  if (_subSyncing || !_extTariff) return;
  _subSyncing = true;
  const amount = parseInt(document.getElementById('dlgSubAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / _extTariff) || 0;
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
  await conn.invoke('SubtractTime', activePc, min * 60, amount);
}

let _penSyncing = false;

function openPenalty(pcNumber) {
  activePc = pcNumber;
  _extTariff = settings?.tariff || 0;
  const pc = pcs[pcNumber];
  const isVip = pc?.sessionType === 'VIP';
  document.getElementById('dlgPenPc').textContent = pcNumber;
  document.getElementById('penTimeRow').style.display = isVip ? 'none' : '';
  document.getElementById('dlgPenHours').value = 0;
  document.getElementById('dlgPenMins').value = 10;
  document.getElementById('dlgPenAmount').value = (!isVip && _extTariff) ? Math.round(_extTariff * 10 / 60) : 0;
  document.getElementById('dlgPenalty').style.display = 'flex';
}

function calcPenAmount() {
  if (_penSyncing || !_extTariff) return;
  const pc = pcs[activePc];
  if (pc?.sessionType === 'VIP') return;
  _penSyncing = true;
  const h = parseInt(document.getElementById('dlgPenHours').value) || 0;
  const min = h * 60 + (parseInt(document.getElementById('dlgPenMins').value) || 0);
  document.getElementById('dlgPenAmount').value = Math.round(_extTariff * min / 60);
  _penSyncing = false;
}

function calcPenTime() {
  if (_penSyncing || !_extTariff) return;
  const pc = pcs[activePc];
  if (pc?.sessionType === 'VIP') return;
  _penSyncing = true;
  const amount = parseInt(document.getElementById('dlgPenAmount').value) || 0;
  const totalMins = Math.round(amount * 60 / _extTariff) || 0;
  document.getElementById('dlgPenHours').value = Math.floor(totalMins / 60);
  document.getElementById('dlgPenMins').value = totalMins % 60;
  _penSyncing = false;
}

async function confirmPenalty() {
  const pc = pcs[activePc];
  const isVip = pc?.sessionType === 'VIP';
  const h = isVip ? 0 : (parseInt(document.getElementById('dlgPenHours').value) || 0);
  const min = isVip ? 0 : (h * 60 + (parseInt(document.getElementById('dlgPenMins').value) || 0));
  const amount = parseInt(document.getElementById('dlgPenAmount').value) || 0;
  if (!isVip && min <= 0) { toast('Укажите время штрафа', 'warn'); return; }
  if (isVip && amount <= 0) { toast('Укажите сумму штрафа', 'warn'); return; }
  closeDlg('dlgPenalty');
  await conn.invoke('ApplyPenalty', activePc, min * 60, amount);
}

let _adminSummaryReaderId = '';
let _adminSummaryPcNumber = '';

function showSummary(d, isManual = false) {
  const h = Math.floor(d.duration / 3600), m = Math.floor((d.duration % 3600) / 60), s = d.duration % 60;
  _adminSummaryReaderId = d.readerId || '';
  _adminSummaryPcNumber = d.pcNumber || '';
  if (!isManual) {
    const name = d.userName || d.readerId || 'Анонимный';
    bibNotify(`✅ ${d.pcNumber} — сессия завершена`,
      `${name} · ${h}ч ${m}м · ${d.earned.toLocaleString()} сум`);
  }

  let html = `
    <b>ПК:</b> ${esc(d.pcNumber)}<br>
    <b>Тип:</b> ${esc(d.sessionType)}<br>
    <b>Длительность:</b> ${h}ч ${m}м ${s}с<br>
    <b>Заработано:</b> ${d.earned.toLocaleString()} сум<br>
    <b>Оплачено:</b> ${d.paidAmount.toLocaleString()} сум<br>
    <b>Возврат:</b> ${d.refund.toLocaleString()} сум
  `;

  const debts = d.serviceDebts || [];
  const payBtn = document.getElementById('btnSummaryPayDebts');
  if (debts.length > 0) {
    html += `<hr style="border-color:#333;margin:12px 0">
      <div style="color:#E09000;font-weight:600;margin-bottom:8px">Неоплаченные услуги:</div>`;
    debts.forEach(dbt => {
      html += `<div style="display:flex;justify-content:space-between;font-size:13px;margin-bottom:4px">
        <span>${esc(dbt.name)} × ${dbt.qty} ${esc(dbt.unit)}</span>
        <b style="color:#E09000">${dbt.debt.toLocaleString()} сум</b>
      </div>`;
    });
    const total = d.totalServiceDebt || debts.reduce((a, b) => a + b.debt, 0);
    html += `<div style="display:flex;justify-content:space-between;font-weight:700;color:#E09000;margin-top:8px;padding-top:8px;border-top:1px solid #444">
      <span>Итого долгов</span><span>${total.toLocaleString()} сум</span>
    </div>`;
    if (payBtn) payBtn.style.display = '';
  } else {
    if (payBtn) payBtn.style.display = 'none';
  }

  document.getElementById('dlgSummaryContent').innerHTML = html;
  document.getElementById('dlgSummary').style.display = 'flex';
  loadFinance();
}

async function paySessionDebtsAdmin() {
  try {
    await conn.invoke('PaySessionDebts', _adminSummaryPcNumber, _adminSummaryReaderId);
    const payBtn = document.getElementById('btnSummaryPayDebts');
    if (payBtn) payBtn.style.display = 'none';
    toast('Долги оплачены');
    loadFinance();
  } catch (e) { toast('Ошибка: ' + e); }
}

async function openDebtsDlgAdmin(inline) {
  try {
    const debts = await conn.invoke('GetAllDebts');
    renderDebtsDlgAdmin(debts, inline);
    if (!inline) document.getElementById('dlgDebts').style.display = 'flex';
  } catch (e) { toast('Ошибка загрузки долгов: ' + e); }
}

function renderDebtsDlgAdmin(debts, inline) {
  const target = inline
    ? document.getElementById('finTable')
    : document.getElementById('dlgDebtsBody');
  if (!target) return;
  if (!debts || !debts.length) {
    target.innerHTML = '<p style="text-align:center;color:#888;padding:24px">Нет непогашенных долгов</p>';
    return;
  }
  let html = '';
  debts.forEach(d => {
    const reader = d.readerName || d.readerId || '—';
    const pc = d.pcNumber || '—';
    html += `<div style="display:flex;align-items:center;gap:12px;padding:10px 0;border-bottom:1px solid #333">
      <div style="flex:1;color:#ccc">
        <b>${esc(d.serviceName)}</b>
        <span style="color:#888;font-size:12px"> × ${d.quantity} ${esc(d.unit)}</span><br>
        <span style="color:#666;font-size:12px">ПК: ${esc(pc)} · Читатель: ${esc(reader)}</span>
      </div>
      <b style="color:#E09000">${d.debtAmount.toLocaleString()} сум</b>
      <button class="btn btn-primary" style="padding:4px 12px;font-size:12px"
        onclick="payOneDebtAdmin('${esc(d.id)}', this, ${inline ? 'true' : 'false'})">Оплатить</button>
    </div>`;
  });
  const total = debts.reduce((a, d) => a + d.debtAmount, 0);
  html += `<div style="text-align:right;font-weight:700;color:#E09000;padding-top:10px">
    Итого: ${total.toLocaleString()} сум
  </div>`;
  target.innerHTML = html;
}

async function payOneDebtAdmin(id, btn, inline) {
  btn.disabled = true; btn.textContent = '...';
  try {
    await conn.invoke('PayDebt', id);
    const debts = await conn.invoke('GetAllDebts');
    renderDebtsDlgAdmin(debts, inline);
    toast('Долг оплачен');
    loadFinance();
  } catch (e) { toast('Ошибка: ' + e); btn.disabled = false; btn.textContent = 'Оплатить'; }
}

// ─── Transfer ────────────────────────────────────────────────────────────────
async function openTransfer(pcNumber) {
  activePc = pcNumber;
  document.getElementById('dlgTransferFrom').textContent = pcNumber;
  const targets = await conn.invoke('GetTransferTargets', pcNumber);
  const sel = document.getElementById('dlgTransferTarget');
  sel.innerHTML = '<option value="">— выберите ПК —</option>';
  targets.forEach(t => sel.innerHTML += `<option value="${esc(t.pcNumber)}">${esc(t.pcNumber)}</option>`);
  document.getElementById('dlgTransfer').style.display = 'flex';
}

async function confirmTransfer() {
  const target = document.getElementById('dlgTransferTarget').value;
  if (!target) { toast('Выберите ПК назначения', 'warn'); return; }
  closeDlg('dlgTransfer');
  const result = await conn.invoke('TransferSession', activePc, target);
  if (result !== 'OK') toast(`Ошибка: ${result}`, 'warn');
  else toast(`Сессия пересажена: ${activePc} → ${target}`, 'success');
}

// ─── Offline alert ────────────────────────────────────────────────────────────
function showOfflineAlert(d) {
  pendingOfflinePc = d.pcNumber;
  const h = Math.floor(d.elapsed / 3600), m = Math.floor((d.elapsed % 3600) / 60), s = d.elapsed % 60;
  document.getElementById('dlgOfflineText').innerHTML =
    `<b>${esc(d.pcNumber)}</b> потерял связь во время сессии <b>${d.sessionType}</b>.<br>
     Прошло: ${h}ч ${m}м ${s}с.<br><br>Что сделать с сессией?`;
  document.getElementById('dlgOffline').style.display = 'flex';
  bibNotify(`⚠️ ${d.pcNumber} — потеря связи`, `Сессия ${d.sessionType} · ${h}ч ${m}м ${s}с`);
}

async function resolveOffline(decision) {
  if (!pendingOfflinePc) return;
  await conn.invoke('ResolveOffline', pendingOfflinePc, decision);
  pendingOfflinePc = null;
  closeDlg('dlgOffline');
}

// ─── Name conflict ────────────────────────────────────────────────────────────
function showNameConflict(d) {
  pendingConflict = d;
  document.getElementById('dlgConflictText').innerHTML =
    `ПК подключается под именем <b>${esc(d.requestedAs)}</b>,<br>
     но в базе зарегистрирован как <b>${esc(d.registeredAs)}</b>.<br>
     MAC: <code>${d.mac}</code>`;
  document.getElementById('dlgNameConflict').style.display = 'flex';
}

async function resolveConflict(accept) {
  if (!pendingConflict) return;
  const d = pendingConflict;
  await conn.invoke('ResolveNameConflict', d.mac, accept, d.pcNumberValue, d.customName);
  pendingConflict = null;
  closeDlg('dlgNameConflict');
}

// ─── Number conflict ─────────────────────────────────────────────────────────
let pendingNumberConflict = null;

function showNumberConflict(d) {
  pendingNumberConflict = d;
  const requestedName = d.customName ? `${d.customName} ${d.pcNumberValue}` : `ПК ${d.pcNumberValue}`;
  document.getElementById('dlgNumberConflictText').innerHTML =
    `Новый ПК (MAC: <code>${esc(d.mac)}</code>) хочет зарегистрироваться как <b>${esc(requestedName)}</b>,<br>
     но этот номер уже занят: <b>${esc(d.takenPcName)}</b>.<br><br>
     Разрешить регистрацию со следующим свободным номером?`;
  document.getElementById('dlgNumberConflict').style.display = 'flex';
}

async function resolveNumberConflict(accept) {
  if (!pendingNumberConflict) return;
  const d = pendingNumberConflict;
  await conn.invoke('ResolveNumberConflict', d.mac, accept);
  pendingNumberConflict = null;
  closeDlg('dlgNumberConflict');
}

// ─── Rename ──────────────────────────────────────────────────────────────────
function openRename(pcNumberValue, currentCustomName) {
  renamePcVal = pcNumberValue;
  document.getElementById('dlgRenameNum').textContent = pcNumberValue;
  document.getElementById('dlgRenameName').value = currentCustomName || '';
  document.getElementById('dlgRename').style.display = 'flex';
}

async function confirmRename() {
  const name = document.getElementById('dlgRenameName').value.trim();
  closeDlg('dlgRename');
  await conn.invoke('RenameClient', renamePcVal, name);
}

// ─── Delete PC ────────────────────────────────────────────────────────────────
async function deletePc(pcNumber) {
  if (!confirm(`Удалить ${pcNumber} из реестра?`)) return;
  await conn.invoke('DeletePc', pcNumber);
}

// ─── Sort ─────────────────────────────────────────────────────────────────────
async function changeSortMode(mode) {
  settings.clientSortMode = mode;
  // Quick-save just the sort mode
  await fetch('/api/admin/settings', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...readSettingsForm(), clientSortMode: mode })
  });
  renderPcGrid();
}

// ─── Context menu ─────────────────────────────────────────────────────────────
let ctxPc = null;

function showCtxMenu(x, y, c) {
  ctxPc = c;
  const menu = document.getElementById('ctxMenu');
  menu.innerHTML = buildCtxHtml(c);
  menu.style.display = 'block';
  // Keep within viewport
  const rect = { w: 220, h: 380 };
  menu.style.left = (x + rect.w > window.innerWidth  ? x - rect.w : x) + 'px';
  menu.style.top  = (y + rect.h > window.innerHeight ? y - rect.h : y) + 'px';
}

function hideCtxMenu() {
  document.getElementById('ctxMenu').style.display = 'none';
  ctxPc = null;
}

document.addEventListener('click', e => {
  const btn = e.target.closest('.pc-menu-btn');
  if (btn) {
    e.stopPropagation();
    const pcNumber = btn.dataset.pcnumber;
    const c = pcs[pcNumber];
    if (!c) return;
    const r = btn.getBoundingClientRect();
    showCtxMenu(r.right, r.bottom + 4, c);
    return;
  }
  if (!e.target.closest('#ctxMenu')) hideCtxMenu();
});

function buildCtxHtml(c) {
  const g = settings;
  const item = (icon, label, action, danger = false) =>
    `<div class="ctx-item${danger ? ' danger' : ''}" data-action="${action}"><span class="ctx-icon">${icon}</span>${label}</div>`;
  const sep = '<div class="ctx-sep"></div>';
  const sub = (icon, label, inner) =>
    `<div class="ctx-item ctx-sub"><span class="ctx-icon">${icon}</span>${label}<span class="ctx-arrow">›</span>
       <div class="ctx-sub-panel">${inner}</div>
     </div>`;

  let html = item('✎', 'Переименовать', `rename:${c.pcNumberValue}:${encodeURIComponent(c.customName || '')}`);
  if (c.isSession)
    html += item('↔', 'Пересадить пользователя', `transfer:${c.pcNumber}`);
  html += sep;

  // Settings submenu
  const showNum = c.hasIndividualSettings && c.showPcNumber !== undefined ? c.showPcNumber : (g.showPcNumber !== false);
  let sInner = item('▣', 'Изменить фон...', `indBg:${c.pcNumber}`);
  sInner += item(showNum ? '◎' : '●', showNum ? 'Скрыть номер ПК' : 'Показать номер ПК', `togglePc:${c.pcNumber}:SHOW_PC_NUMBER`);
  if (c.hasIndividualSettings) {
    sInner += sep;
    sInner += item('🔄', 'Сбросить к глобальным', `resetInd:${c.pcNumber}`, true);
  }
  html += sub('🖥', 'Настройки ПК', sInner);

  // Restrictions submenu
  const usb = c.hasIndividualSettings && c.usbBlocked !== undefined ? c.usbBlocked : !!g.usbBlocked;
  const tm  = c.hasIndividualSettings && c.taskMgrDisabled !== undefined ? c.taskMgrDisabled : !!g.taskMgrDisabled;
  let rInner = '';
  rInner += item(usb ? '▶' : '■', usb ? 'Разблокировать USB' : 'Заблокировать USB', `togglePc:${c.pcNumber}:USB_BLOCK`);
  rInner += item(tm ? '▶' : '■', tm ? 'Вкл. диспетчер задач' : 'Откл. диспетчер задач', `togglePc:${c.pcNumber}:TASKMGR_DISABLE`);
  rInner += sep;
  rInner += item('■', g.blockRegedit ? 'Разрешить regedit' : 'Запретить regedit', `toggleGlob:${c.pcNumber}:BLOCK_REGEDIT`);
  rInner += item('■', g.blockCmd ? 'Разрешить CMD' : 'Запретить CMD', `toggleGlob:${c.pcNumber}:BLOCK_CMD`);
  rInner += item('■', g.blockPowerShell ? 'Разрешить PowerShell' : 'Запретить PowerShell', `toggleGlob:${c.pcNumber}:BLOCK_POWERSHELL`);
  rInner += item('■', g.blockInstall ? 'Разрешить установку' : 'Запретить установку', `toggleGlob:${c.pcNumber}:BLOCK_INSTALL_UNINSTALL`);
  html += sub('🛡', 'Ограничения', rInner);

  html += sep;
  html += item('⟳', 'Переподключить клиент', `reconnect:${c.pcNumber}`);
  if (c.isOnline) {
    html += item('↺', 'Перезагрузить ПК', `restart:${c.pcNumber}`);
    html += item('⏻', 'Выключить ПК', `shutdown:${c.pcNumber}`, true);
    html += sep;
    html += item('📋', 'Показать логи клиента', `logs:${c.pcNumber}`);
  }
  if (!c.isOnline && !c.isSession) {
    html += sep;
    html += item('✕', 'Удалить из списка', `delete:${c.pcNumber}`, true);
  }
  return html;
}

// Event delegation for context menu clicks
document.addEventListener('click', async e => {
  const el = e.target.closest('#ctxMenu .ctx-item[data-action]');
  if (!el) return;
  const [act, ...args] = el.dataset.action.split(':');
  hideCtxMenu();
  switch (act) {
    case 'rename':   openRename(parseInt(args[0]), decodeURIComponent(args[1] || '')); break;
    case 'transfer': openTransfer(args[0]); break;
    case 'indBg':    openIndBg(args[0]); break;
    case 'togglePc': await conn.invoke('TogglePcSetting', args[0], args[1]); break;
    case 'resetInd':
      if (confirm(`Сбросить индивидуальные настройки для ${args[0]}?`))
        await conn.invoke('ResetIndividualSettings', args[0]);
      break;
    case 'toggleGlob': await conn.invoke('ToggleGlobalSetting', args[0], args[1]); break;
    case 'reconnect': await conn.invoke('SendCommandToPc', args[0], 'RECONNECT', 'true'); break;
    case 'restart':
      if (confirm(`Перезагрузить ${args[0]}?`)) await conn.invoke('SendCommandToPc', args[0], 'RESTART', 'true');
      break;
    case 'shutdown':
      if (confirm(`Выключить ${args[0]}?`)) await conn.invoke('SendCommandToPc', args[0], 'SHUTDOWN', 'true');
      break;
    case 'delete':  deletePc(args[0]); break;
    case 'logs':    requestClientLogs(args[0]); break;
  }
});

// ─── Base64 helper (chunked, works for large files) ──────────────────────────
function arrayBufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  const chunk = 8192;
  for (let i = 0; i < bytes.length; i += chunk)
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  return btoa(binary);
}

// ─── Individual PC background ─────────────────────────────────────────────────
let indBgPc = null;

function openIndBg(pcNumber) {
  indBgPc = pcNumber;
  document.getElementById('dlgIndBgPcName').textContent = pcNumber;
  document.getElementById('dlgIndBgInput').value = '';
  document.getElementById('dlgIndBg').style.display = 'flex';
}

async function confirmIndBg() {
  const input = document.getElementById('dlgIndBgInput');
  if (!input.files || !input.files.length) { toast('Выберите файл', 'warn'); return; }
  const file = input.files[0];
  const buf  = await file.arrayBuffer();
  const b64  = arrayBufferToBase64(buf);
  closeDlg('dlgIndBg');
  try {
    await conn.invoke('UploadFile', file.name, b64, indBgPc, false);
    toast(`Фон установлен для ${indBgPc}`, 'success');
  } catch (e) {
    toast('Ошибка загрузки фона: ' + e.message, 'warn');
  }
}

// ─── Finance ──────────────────────────────────────────────────────────────────
async function loadFinance() {
  const [rs, sv] = await Promise.all([
    fetch('/api/admin/finance/sessions').then(r => r.json()),
    fetch('/api/admin/finance/services').then(r => r.json())
  ]);
  finSessions = rs;
  finServices = sv;
  renderFinance();
}

function setFinTab(tab) {
  finTab = tab;
  ['sessions', 'services', 'all', 'debts'].forEach(t => {
    const el = document.getElementById('tab' + t.charAt(0).toUpperCase() + t.slice(1));
    if (el) el.classList.toggle('active', t === tab);
  });
  document.getElementById('finTypeFilter').style.display = tab === 'sessions' ? '' : 'none';
  document.getElementById('finStatusFilter').style.display = tab === 'services' ? '' : 'none';
  const finTable = document.getElementById('finTable');
  if (tab === 'debts') {
    if (finTable) finTable.innerHTML = '';
    openDebtsDlgAdmin(true);
  } else {
    renderFinance();
  }
}

function renderFinance() {
  const period = document.querySelector('input[name=finPeriod]:checked')?.value || 'all';
  const from = periodFrom(period);
  const typeF = document.getElementById('finTypeFilter').value;
  const statusF = document.getElementById('finStatusFilter').value;

  let sessions = finSessions.filter(s => !from || new Date(s.endTime) >= from);
  let services = finServices.filter(t => !from || new Date(t.createdAt) >= from);
  if (typeF) sessions = sessions.filter(s => s.sessionType === typeF);
  if (statusF === 'paid') services = services.filter(t => t.isPaid);
  if (statusF === 'unpaid') services = services.filter(t => !t.isPaid);

  // Stats
  const periodLabels = { today: 'За сегодня', week: 'За неделю', month: 'За месяц', year: 'За год' };
  document.getElementById('statTotalLabel').textContent = periodLabels[period] || 'Итого';

  let statTotal, statCount;
  if (finTab === 'sessions') {
    statTotal = sessions.reduce((a, s) => a + s.earnedAmount, 0);
    statCount = sessions.length;
  } else if (finTab === 'services') {
    statTotal = services.reduce((a, t) => a + t.totalAmount, 0);
    statCount = services.length;
  } else {
    statTotal = sessions.reduce((a, s) => a + s.earnedAmount, 0) + services.reduce((a, t) => a + t.totalAmount, 0);
    statCount = sessions.length + services.length;
  }
  document.getElementById('statTotal').textContent = fmt(statTotal);
  document.getElementById('statCount').textContent = statCount;

  const table = document.getElementById('finTable');
  if (finTab === 'sessions')  table.innerHTML = renderSessionsTable(sessions);
  else if (finTab === 'services') table.innerHTML = renderServicesTable(services);
  else table.innerHTML = renderAllTable(sessions, services);
}

function renderSessionsTable(list) {
  if (!list.length) return '<div class="fin-empty">Нет записей</div>';
  const cols = '120px 80px 100px 1fr 90px 90px 110px 120px';
  let html = `<div class="fin-table-header" style="grid-template-columns:${cols}">
    <span>Компьютер</span><span>Тип</span><span>ID читателя</span><span>Пользователь</span>
    <span>Длит.</span><span>Сумма</span><span>Оператор</span><span>Дата</span>
  </div>`;
  list.forEach(s => {
    const d = s.durationSeconds, h = Math.floor(d/3600), m = Math.floor((d%3600)/60), sec = d%60;
    const typeCls = s.sessionType === 'VIP' ? 'fin-badge-vip' : 'fin-badge-limit';
    const op = s.operatorName || 'Администратор';
    html += `<div class="fin-row" style="grid-template-columns:${cols}">
      <b>${esc(s.pcNumber)}</b>
      <span class="fin-badge ${typeCls}">${s.sessionType}</span>
      <span>${s.readerId || '—'}</span>
      <span>${esc(s.userName)}</span>
      <span>${h}:${pad(m)}:${pad(sec)}</span>
      <b style="color:#1d9e75">${s.earnedAmount.toLocaleString()}</b>
      <span style="color:#9d7fcc">${esc(op)}</span>
      <span style="color:#555">${fmtDate(s.endTime)}</span>
    </div>`;
  });
  return html;
}

function renderServicesTable(list) {
  if (!list.length) return '<div class="fin-empty">Нет транзакций</div>';
  const cols = '1fr 100px 90px 150px 90px 110px';
  let html = `<div class="fin-table-header" style="grid-template-columns:${cols}">
    <span>Услуга</span><span>Кол-во</span><span>Сумма</span><span>Читатель</span><span>Статус</span><span>Дата</span>
  </div>`;
  list.forEach(t => {
    const paidCls = t.isPaid ? 'fin-badge-paid' : 'fin-badge-unpaid';
    const paidText = t.isPaid ? 'Оплачено' : `Долг: ${t.debtAmount?.toLocaleString()}`;
    const reader = t.readerName || t.readerId || '—';
    html += `<div class="fin-row" style="grid-template-columns:${cols}">
      <b>${esc(t.serviceName)}</b>
      <span>${t.quantity} ${esc(t.unit)}</span>
      <b style="color:#1d9e75">${t.totalAmount.toLocaleString()}</b>
      <span>${esc(reader)}</span>
      <span class="fin-badge ${paidCls}">${paidText}</span>
      <span style="color:#555">${fmtDate(t.createdAt)}</span>
    </div>`;
  });
  return html;
}

function renderAllTable(sessions, services) {
  const items = [
    ...sessions.map(s => ({ date: new Date(s.endTime), cat: 'Сессия', desc: `${s.pcNumber} · ${s.sessionType}`, reader: s.userName || s.readerId || '—', amount: s.earnedAmount, status: 'Оплачено' })),
    ...services.map(t => ({ date: new Date(t.createdAt), cat: 'Услуга', desc: `${t.serviceName} ×${t.quantity}`, reader: t.readerName || t.readerId || '—', amount: t.totalAmount, status: t.isPaid ? 'Оплачено' : `Долг: ${t.debtAmount?.toLocaleString()}` }))
  ].sort((a, b) => b.date - a.date);

  if (!items.length) return '<div class="fin-empty">Нет операций</div>';
  const cols = '80px 1fr 140px 100px 90px 110px';
  let html = `<div class="fin-table-header" style="grid-template-columns:${cols}">
    <span>Тип</span><span>Описание</span><span>Читатель</span><span>Сумма</span><span>Статус</span><span>Дата</span>
  </div>`;
  items.forEach(x => {
    const catCls = x.cat === 'Услуга' ? 'fin-badge-service' : 'fin-badge-session';
    const stCls = x.status === 'Оплачено' ? 'fin-badge-paid' : 'fin-badge-unpaid';
    html += `<div class="fin-row" style="grid-template-columns:${cols}">
      <span class="fin-badge ${catCls}">${x.cat}</span>
      <b>${esc(x.desc)}</b>
      <span>${esc(x.reader)}</span>
      <b style="color:#1d9e75">${x.amount.toLocaleString()}</b>
      <span class="fin-badge ${stCls}">${x.status}</span>
      <span style="color:#555">${fmtDate(x.date)}</span>
    </div>`;
  });
  return html;
}

async function exportCsv() {
  window.open('/api/admin/finance/export', '_blank');
}

async function clearFinance() {
  const msg = finTab === 'services' ? 'Очистить историю услуг?' :
              finTab === 'all'      ? 'Очистить всю историю?' :
                                     'Очистить историю сессий?';
  if (!confirm(msg)) return;
  if (finTab !== 'services') await fetch('/api/admin/finance/sessions', { method: 'DELETE' });
  if (finTab !== 'sessions') await fetch('/api/admin/finance/services', { method: 'DELETE' });
  await loadFinance();
}

// ─── Settings ─────────────────────────────────────────────────────────────────
async function loadSettings() {
  settings = await fetch('/api/admin/settings').then(r => r.json());
  fillSettingsForm();
}

function fillSettingsForm() {
  if (!settings) return;
  document.getElementById('sTariff').value = settings.tariff ?? 3000;
  document.getElementById('sAdminPassword').value = settings.adminPassword ?? '';
  document.getElementById('sReaderCardPrefix').value = (settings.readerCardPrefix ?? 'FAA').toUpperCase();
  document.getElementById('sUsbBlocked').checked = !!settings.usbBlocked;
  document.getElementById('sTaskMgr').checked = !!settings.taskMgrDisabled;
  document.getElementById('sBlockRegedit').checked = !!settings.blockRegedit;
  document.getElementById('sBlockCmd').checked = !!settings.blockCmd;
  document.getElementById('sBlockPs').checked = !!settings.blockPowerShell;
  document.getElementById('sHideDrive').checked = !!settings.hideDriveC;
  document.getElementById('sBlockInstall').checked = !!settings.blockInstall;
  document.getElementById('sLockOnOffline').checked = !!settings.lockOnOffline;
  document.getElementById('sPreventClose').checked = !!settings.preventClose;
  document.getElementById('sAutoStart').checked = !!settings.autoStartWithUser;
  document.getElementById('sShowPcName').checked   = settings.showPcName  !== false;
  document.getElementById('sShowPcNumber').checked = settings.showPcNumber !== false;
  document.getElementById('sShowLockedText').checked = settings.showLockedText !== false;

  // Прозрачность фона (сервер хранит 0..1, слайдер 0..100)
  const opacityRaw = settings.backgroundOpacity ?? 0.3;
  const opacityPct = Math.round(opacityRaw * 100);
  document.getElementById('sBgOpacity').value = opacityPct;
  document.getElementById('sBgOpacityVal').textContent = opacityPct;

  // Иконка онлайн-статуса
  document.getElementById('sShowStatusDot').checked = settings.showStatusDot ?? true;

  // Отступы от краёв монитора — синхронизируем слайдер и числовое поле
  const offX = settings.screenOffsetX ?? 0;
  const offY = settings.screenOffsetY ?? 0;
  document.getElementById('sScreenOffsetX').value    = offX;
  document.getElementById('sScreenOffsetXNum').value = offX;
  document.getElementById('sScreenOffsetY').value    = offY;
  document.getElementById('sScreenOffsetYNum').value = offY;
  setupOffsetSync('sScreenOffsetX', 'sScreenOffsetXNum');
  setupOffsetSync('sScreenOffsetY', 'sScreenOffsetYNum');

  // Позиции и размеры шрифтов экрана блокировки
  document.getElementById('sPcNumberPosition').value   = settings.pcNumberPosition   ?? 'MiddleCenter';
  document.getElementById('sLockedTextPosition').value = settings.lockedTextPosition ?? 'MiddleCenter';
  document.getElementById('sTimePosition').value       = settings.timePosition       ?? 'BottomCenter';

  // Порядок стекинга — взаимоисключающие значения (своп при совпадении)
  document.getElementById('sPcNumberOrder').value   = settings.pcNumberOrder   ?? 1;
  document.getElementById('sLockedTextOrder').value = settings.lockedTextOrder ?? 2;
  document.getElementById('sTimeOrder').value       = settings.timeOrder       ?? 3;
  setupOrderSwap();

  // Предпросмотр — wire events и первый рендер
  ['sPcNumberPosition','sLockedTextPosition','sTimePosition',
   'sShowPcNumber','sShowPcName','sShowLockedText'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', updateLockPreview);
  });
  // Слайдеры шрифтов и прозрачности — обновляем превью в реальном времени
  ['sPcNumberFontSize','sLockedTextFontSize','sTimeFontSize','sBgOpacity'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', updateLockPreview);
  });
  // Порядок стекинга
  ['sPcNumberOrder','sLockedTextOrder','sTimeOrder'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', updateLockPreview);
  });
  // Фоновое изображение — предпросмотр сразу при выборе файла (до загрузки)
  const bgFileInput = document.getElementById('sBgFileInput');
  if (bgFileInput) bgFileInput.onchange = () => {
    if (bgFileInput.files?.[0]) {
      if (pvBgUrl?.startsWith('blob:')) URL.revokeObjectURL(pvBgUrl);
      pvBgUrl = URL.createObjectURL(bgFileInput.files[0]);
    }
    updateLockPreview();
  };
  // Если фон уже задан в настройках — загружаем с сервера (если blob не выбран)
  if (!pvBgUrl?.startsWith('blob:')) {
    const bgName = settings.backgroundFileName ?? '';
    pvBgUrl = bgName ? `/files/${encodeURIComponent(bgName)}` : null;
  }
  updateLockPreview();

  const pcFont     = settings.pcNumberFontSize   ?? 150;
  const lockedFont = settings.lockedTextFontSize ?? 16;
  const timeFont   = settings.timeFontSize       ?? 36;
  document.getElementById('sPcNumberFontSize').value  = pcFont;
  document.getElementById('sLockedTextFontSize').value = lockedFont;
  document.getElementById('sTimeFontSize').value       = timeFont;
  document.getElementById('sPcFontSizeVal').textContent    = pcFont;
  document.getElementById('sLockedFontSizeVal').textContent = lockedFont;
  document.getElementById('sTimeFontSizeVal').textContent   = timeFont;

  // Имя файла фона
  document.getElementById('sBgFileName').value = settings.backgroundFileName ?? '';

  // Путь к папке обновлений
  document.getElementById('sUpdatesPath').value = settings.updatesPath ?? '';

  // Поля сессии
  document.getElementById('sRequireReaderId').checked = settings.requireReaderId !== false;
  document.getElementById('sRequireUserName').checked = !!settings.requireUserName;
  document.getElementById('sWorkdayEnd').value = settings.workdayEnd || '';

  // Sort mode selector
  const sortSel = document.getElementById('sortMode');
  if (sortSel) sortSel.value = settings.clientSortMode || 'ByNumber';

  // Привязываем live-обновление подписей слайдеров (один раз)
  bindSliderLabel('sBgOpacity',        'sBgOpacityVal',       v => Math.round(v));
  bindSliderLabel('sPcNumberFontSize', 'sPcFontSizeVal',      v => Math.round(v));
  bindSliderLabel('sLockedTextFontSize','sLockedFontSizeVal', v => Math.round(v));
  bindSliderLabel('sTimeFontSize',     'sTimeFontSizeVal',    v => Math.round(v));

  renderServicesList();
}

// ── Offset: двунаправленная синхронизация слайдера и числового поля ────────
function setupOffsetSync(sliderId, numberId) {
  const slider = document.getElementById(sliderId);
  const number = document.getElementById(numberId);
  if (!slider || !number) return;
  // Удаляем старые обработчики через замену клонами
  const newSlider = slider.cloneNode(true); slider.parentNode.replaceChild(newSlider, slider);
  const newNumber = number.cloneNode(true); number.parentNode.replaceChild(newNumber, number);
  newSlider.addEventListener('input', () => {
    newNumber.value = newSlider.value;
    updateLockPreview();
  });
  newNumber.addEventListener('input', () => {
    const v = Math.max(0, Math.min(300, parseInt(newNumber.value) || 0));
    newSlider.value = v;
    newNumber.value = v;
    updateLockPreview();
  });
}

// ── Order swap: взаимоисключающий выбор порядка стекинга ────────────────────
function setupOrderSwap() {
  const ids = ['sPcNumberOrder', 'sLockedTextOrder', 'sTimeOrder'];
  ids.forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    el.dataset.prev = el.value;
    // Заменяем клоном чтобы убрать старые обработчики
    const clone = el.cloneNode(true);
    clone.dataset.prev = el.value;
    el.parentNode.replaceChild(clone, el);
    clone.addEventListener('change', function () {
      const newVal = this.value;
      const oldVal = this.dataset.prev;
      if (newVal === oldVal) return;
      // Находим другой элемент с таким же значением — меняем местами
      ids.forEach(otherId => {
        if (otherId === this.id) return;
        const other = document.getElementById(otherId);
        if (other && other.value === newVal) {
          other.value = oldVal;
          other.dataset.prev = oldVal;
        }
      });
      this.dataset.prev = newVal;
      updateLockPreview();
    });
  });
}

// ── Lock screen preview ──────────────────────────────────────────────────────
function updateLockPreview() {
  const preview = document.getElementById('lockScreenPreview');
  if (!preview) return;

  const zones = ['TopLeft','TopCenter','TopRight','MiddleLeft','MiddleCenter',
                 'MiddleRight','BottomLeft','BottomCenter','BottomRight'];
  zones.forEach(z => {
    const cell = document.getElementById('pv-' + z);
    if (cell) cell.innerHTML = '';
  });

  // ── Фоновое изображение ───────────────────────────────────────────────────
  preview.style.backgroundImage = pvBgUrl ? `url('${pvBgUrl}')` : 'none';

  // ── Затемнение (слайдер 0..100 → CSS-переменная 0..1) ────────────────────
  const opacityPct = parseInt(document.getElementById('sBgOpacity')?.value) || 30;
  preview.style.setProperty('--pv-dim', (opacityPct / 100).toFixed(2));

  // ── Пунктирная рамка отступа (масштаб ~1:6) ───────────────────────────────
  const offX = parseInt(document.getElementById('sScreenOffsetX')?.value) || 0;
  const offY = parseInt(document.getElementById('sScreenOffsetY')?.value) || 0;
  const offsetScale = 1 / 6;
  preview.style.setProperty('--pv-ox', Math.round(offX * offsetScale) + 'px');
  preview.style.setProperty('--pv-oy', Math.round(offY * offsetScale) + 'px');

  // ── Масштаб шрифтов: preview_height / 1080 ───────────────────────────────
  // Размеры берём из слайдеров и масштабируем пропорционально высоте превью
  const pvH = preview.offsetHeight || 195;
  const fontScale = pvH / 1080;
  const pcFontPx = Math.max(7, Math.min(36,
    Math.round((parseInt(document.getElementById('sPcNumberFontSize')?.value)   || 150) * fontScale)));
  const lockedFontPx = Math.max(5, Math.min(16,
    Math.round((parseInt(document.getElementById('sLockedTextFontSize')?.value) || 16)  * fontScale)));
  const timeFontPx = Math.max(6, Math.min(18,
    Math.round((parseInt(document.getElementById('sTimeFontSize')?.value)       || 36)  * fontScale)));

  const showPc     = document.getElementById('sShowPcNumber')?.checked ||
                     document.getElementById('sShowPcName')?.checked;
  const showLocked = document.getElementById('sShowLockedText')?.checked;

  const items = [];
  if (showPc)
    items.push({ pos: document.getElementById('sPcNumberPosition')?.value   || 'MiddleCenter',
                 order: parseInt(document.getElementById('sPcNumberOrder')?.value)   || 1,
                 cls: 'pv-pc',     label: '42',            fontPx: pcFontPx });
  if (showLocked)
    items.push({ pos: document.getElementById('sLockedTextPosition')?.value || 'MiddleCenter',
                 order: parseInt(document.getElementById('sLockedTextOrder')?.value) || 2,
                 cls: 'pv-locked', label: 'Заблокировано', fontPx: lockedFontPx });
  items.push(  { pos: document.getElementById('sTimePosition')?.value       || 'BottomCenter',
                 order: parseInt(document.getElementById('sTimeOrder')?.value)       || 3,
                 cls: 'pv-time',   label: '14:23',         fontPx: timeFontPx });

  // Группируем по позиции, сортируем по порядку
  const groups = {};
  items.forEach(it => {
    if (!groups[it.pos]) groups[it.pos] = [];
    groups[it.pos].push(it);
  });
  Object.entries(groups).forEach(([pos, grp]) => {
    const cell = document.getElementById('pv-' + pos);
    if (!cell) return;
    grp.sort((a, b) => a.order - b.order).forEach(it => {
      const chip = document.createElement('div');
      chip.className = 'pv-chip ' + it.cls;
      chip.textContent = it.label;
      chip.style.fontSize = it.fontPx + 'px';
      cell.appendChild(chip);
    });
  });
}

function bindSliderLabel(sliderId, labelId, fmt) {
  const slider = document.getElementById(sliderId);
  const label  = document.getElementById(labelId);
  if (!slider || !label) return;
  // Удалить старый обработчик чтобы не дублировать
  slider.oninput = () => { label.textContent = fmt(parseFloat(slider.value)); };
}

function readSettingsForm() {
  // Прозрачность: слайдер 0..100 → сервер хранит 0..1
  const rawOpacity = parseFloat(document.getElementById('sBgOpacity').value);
  const opacityPct = isNaN(rawOpacity) ? 30 : rawOpacity;
  return {
    tariff: parseInt(document.getElementById('sTariff').value) || 3000,
    adminPassword: document.getElementById('sAdminPassword').value,
    readerCardPrefix: (document.getElementById('sReaderCardPrefix').value.trim().toUpperCase()) || 'FAA',
    usbBlocked: document.getElementById('sUsbBlocked').checked,
    taskMgrDisabled: document.getElementById('sTaskMgr').checked,
    blockRegedit: document.getElementById('sBlockRegedit').checked,
    blockCmd: document.getElementById('sBlockCmd').checked,
    blockPowerShell: document.getElementById('sBlockPs').checked,
    hideDriveC: document.getElementById('sHideDrive').checked,
    blockInstall: document.getElementById('sBlockInstall').checked,
    lockOnOffline: document.getElementById('sLockOnOffline').checked,
    preventClose: document.getElementById('sPreventClose').checked,
    autoStartWithUser: document.getElementById('sAutoStart').checked,
    showPcName: document.getElementById('sShowPcName').checked,
    showPcNumber: document.getElementById('sShowPcNumber').checked,
    showLockedText: document.getElementById('sShowLockedText').checked,
    backgroundOpacity: opacityPct / 100,
    // Экран блокировки — позиции, размеры шрифтов, отступы, порядок, иконка
    showStatusDot:      document.getElementById('sShowStatusDot').checked,
    screenOffsetX:      parseInt(document.getElementById('sScreenOffsetX').value) || 0,
    screenOffsetY:      parseInt(document.getElementById('sScreenOffsetY').value) || 0,
    pcNumberPosition:   document.getElementById('sPcNumberPosition').value,
    pcNumberFontSize:   parseInt(document.getElementById('sPcNumberFontSize').value) || 150,
    pcNumberOrder:      parseInt(document.getElementById('sPcNumberOrder').value) || 1,
    lockedTextPosition: document.getElementById('sLockedTextPosition').value,
    lockedTextFontSize: parseInt(document.getElementById('sLockedTextFontSize').value) || 16,
    lockedTextOrder:    parseInt(document.getElementById('sLockedTextOrder').value) || 2,
    timePosition:       document.getElementById('sTimePosition').value,
    timeFontSize:       parseInt(document.getElementById('sTimeFontSize').value) || 36,
    timeOrder:          parseInt(document.getElementById('sTimeOrder').value) || 3,
    // Фон — имя файла берём из поля (uploadBgFile() обновляет его отдельно)
    backgroundFileName: document.getElementById('sBgFileName').value,
    services: readServicesForm(),
    // Сохраняем поля которые не редактируются на этой странице
    clientSortMode: settings.clientSortMode,
    operators: settings.operators,
    updatesPath: document.getElementById('sUpdatesPath').value.trim(),
    requireReaderId: document.getElementById('sRequireReaderId').checked,
    requireUserName: document.getElementById('sRequireUserName').checked,
    workdayEnd: document.getElementById('sWorkdayEnd').value.trim(),
  };
}

async function saveSettings() {
  const s = readSettingsForm();
  await fetch('/api/admin/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(s)
  });
  settings = s;
  const badge = document.getElementById('settingsSaved');
  badge.style.display = 'inline';
  setTimeout(() => badge.style.display = 'none', 2000);
}

// Загрузка файла фона через SignalR (аналог SendBgAll_Click в WPF)
async function uploadBgFile() {
  const input = document.getElementById('sBgFileInput');
  if (!input.files || !input.files.length) {
    toast('Выберите файл изображения', 'warn');
    return;
  }
  const file = input.files[0];
  const fileName = file.name;
  const buf = await file.arrayBuffer();
  const b64 = arrayBufferToBase64(buf);
  try {
    await conn.invoke('UploadFile', fileName, b64, '*', true);
    document.getElementById('sBgFileName').value = fileName;
    settings.backgroundFileName = fileName;
    const status = document.getElementById('bgUploadStatus');
    status.style.display = 'inline';
    setTimeout(() => status.style.display = 'none', 3000);
    toast('Фон отправлен всем ПК', 'success');
  } catch (e) {
    toast('Ошибка загрузки фона: ' + e.message, 'warn');
  }
}

// ─── Services (in settings) ───────────────────────────────────────────────────
function renderServicesList() {
  const list = document.getElementById('servicesList');
  list.innerHTML = '';
  (settings.services || []).forEach((svc, i) => {
    const row = document.createElement('div');
    row.className = 'service-row';
    row.innerHTML = `
      <input type="text" value="${esc(svc.name)}" placeholder="Название" data-si="${i}" data-field="name">
      <input type="text" value="${esc(svc.unit)}" placeholder="Ед." data-si="${i}" data-field="unit">
      <input type="number" value="${svc.price}" placeholder="Цена" data-si="${i}" data-field="price" min="0">
      <div class="service-chk"><input type="checkbox" ${svc.isActive ? 'checked' : ''} data-si="${i}" data-field="isActive" title="Активна"></div>
      <button class="del-btn" onclick="removeService(${i})">✕</button>
    `;
    list.appendChild(row);
  });
}

function readServicesForm() {
  const inputs = document.querySelectorAll('#servicesList [data-si]');
  const map = {};
  inputs.forEach(inp => {
    const i = inp.dataset.si;
    if (!map[i]) map[i] = { ...(settings.services[i] || {}), id: settings.services[i]?.id || crypto.randomUUID() };
    const field = inp.dataset.field;
    if (field === 'isActive') map[i][field] = inp.checked;
    else if (field === 'price') map[i][field] = parseInt(inp.value) || 0;
    else map[i][field] = inp.value;
  });
  return Object.values(map);
}

function addService() {
  if (!settings.services) settings.services = [];
  settings.services.push({ id: crypto.randomUUID(), name: 'Новая услуга', unit: 'лист', price: 500, isActive: true });
  renderServicesList();
}

function removeService(i) {
  settings.services.splice(i, 1);
  renderServicesList();
}

// ─── Operators ────────────────────────────────────────────────────────────────
async function loadOperators() {
  const list = await fetch('/api/admin/operators').then(r => r.json());
  renderOperators(list);
}

function renderOperators(list) {
  const el = document.getElementById('operatorsList');
  if (!list.length) { el.innerHTML = '<div style="color:#555;padding:12px">Нет операторов</div>'; return; }
  el.innerHTML = list.map(op => `
    <div class="op-row">
      <div class="op-info">
        <div class="op-name">${esc(op.displayName)}</div>
        <div class="op-login">${esc(op.login)}</div>
      </div>
      <div class="op-perms">
        <label class="op-perm-chk" title="Доступ к разделу Читатели">
          <input type="checkbox" id="opChkReaders_${op.id}" ${op.canViewReaders ? 'checked' : ''}>
          <span>👁 Читатели</span>
        </label>
        <label class="op-perm-chk" title="Доступ к истории финансов">
          <input type="checkbox" id="opChkFinance_${op.id}" ${op.canViewFinance ? 'checked' : ''}>
          <span>💰 Финансы</span>
        </label>
        <label class="op-perm-chk" title="Доступ к статистике">
          <input type="checkbox" id="opChkStats_${op.id}" ${op.canViewStats ? 'checked' : ''}>
          <span>📊 Статистика</span>
        </label>
        <div class="op-perm-divider"></div>
        <button class="btn btn-outline op-perm-apply" onclick="applyOpPermissions('${op.id}')">Применить</button>
        <span id="opPermSaved_${op.id}" class="op-perm-saved">✓</span>
      </div>
      <span class="op-active-badge ${op.isActive ? 'active' : 'inactive'}">${op.isActive ? 'Активен' : 'Отключён'}</span>
      <div class="op-actions">
        <button class="btn btn-outline" onclick="toggleOpActive('${op.id}', ${!op.isActive})">${op.isActive ? 'Откл.' : 'Вкл.'}</button>
        <button class="btn btn-outline" onclick="resetOpPwd('${op.id}')">Пароль</button>
        <button class="btn btn-danger" onclick="deleteOp('${op.id}')">✕</button>
      </div>
    </div>
  `).join('');
}

async function addOperator() {
  const name = document.getElementById('newOpName').value.trim();
  const login = document.getElementById('newOpLogin').value.trim();
  const pwd = document.getElementById('newOpPwd').value;
  if (!name || !login || !pwd) { toast('Заполните все поля', 'warn'); return; }
  const canViewReaders = document.getElementById('newOpReaders').checked;
  const canViewFinance = document.getElementById('newOpFinance').checked;
  const canViewStats   = document.getElementById('newOpStats').checked;
  const r = await fetch('/api/admin/operators', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ displayName: name, login, password: pwd, canViewReaders, canViewFinance, canViewStats })
  });
  if (!r.ok) { const d = await r.json(); toast(d.error || 'Ошибка', 'warn'); return; }
  document.getElementById('newOpName').value = '';
  document.getElementById('newOpLogin').value = '';
  document.getElementById('newOpPwd').value = '';
  document.getElementById('newOpReaders').checked = false;
  document.getElementById('newOpFinance').checked = false;
  document.getElementById('newOpStats').checked   = false;
  const badge = document.getElementById('opSaved');
  badge.style.display = 'inline'; setTimeout(() => badge.style.display = 'none', 2000);
  await loadOperators();
}

async function applyOpPermissions(id) {
  const canViewReaders = document.getElementById(`opChkReaders_${id}`)?.checked ?? false;
  const canViewFinance = document.getElementById(`opChkFinance_${id}`)?.checked ?? false;
  const canViewStats   = document.getElementById(`opChkStats_${id}`)?.checked   ?? false;
  const r = await fetch(`/api/admin/operators/${id}/permissions`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ canViewReaders, canViewFinance, canViewStats })
  });
  if (r.ok) {
    const badge = document.getElementById(`opPermSaved_${id}`);
    if (badge) { badge.style.display = 'inline-flex'; setTimeout(() => badge.style.display = 'none', 2500); }
  } else {
    toast('Ошибка сохранения прав', 'warn');
  }
}

async function toggleOpActive(id, isActive) {
  await fetch(`/api/admin/operators/${id}/active`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ isActive })
  });
  await loadOperators();
}

async function resetOpPwd(id) {
  const pwd = prompt('Новый пароль:');
  if (!pwd) return;
  await fetch(`/api/admin/operators/${id}/password`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password: pwd })
  });
  toast('Пароль обновлён', 'success');
}

async function deleteOp(id) {
  if (!confirm('Удалить оператора?')) return;
  await fetch(`/api/admin/operators/${id}`, { method: 'DELETE' });
  await loadOperators();
}

// ─── Local IP for operator URL ────────────────────────────────────────────────
async function detectLocalIp() {
  try {
    const resp = await fetch('/api/admin/check');
    const data = await resp.json();
    const port = data.port || 8080;
    // Сохраняем порт в localStorage для использования в случае ошибок
    localStorage.setItem('bib_server_port', port);
    document.getElementById('opWebUrl').textContent =
      `http://${location.hostname}:${port}/login.html`;
  } catch (e) {
    // Fallback: используем сохранённый порт или порт по умолчанию
    const savedPort = localStorage.getItem('bib_server_port') || 8080;
    document.getElementById('opWebUrl').textContent =
      `http://${location.hostname}:${savedPort}/login.html`;
  }
}

// ─── Auth ─────────────────────────────────────────────────────────────────────
async function checkUpdates() {
  const btn = document.getElementById('btnCheckUpdate');
  const status = document.getElementById('updateStatus');
  const verLabel = document.getElementById('updateCurrentVer');
  btn.disabled = true;
  btn.textContent = '⏳ Проверка...';
  status.style.display = 'none';
  try {
    const data = await fetch('/api/admin/check-update').then(r => r.json());
    verLabel.textContent = `Текущая версия: ${data.currentVersion}`;
    if (data.hasUpdate) {
      let msg = `Доступна версия ${data.newVersion} (у вас ${data.currentVersion})`;
      if (data.releaseNotes) msg += `\n\n${data.releaseNotes}`;
      msg += '\n\nОбновить сейчас? Сервер перезапустится.';
      if (confirm(msg)) {
        status.style.display = 'inline';
        status.style.color = '#888';
        status.textContent = '⏳ Запуск установщика...';
        await fetch('/api/admin/apply-update', { method: 'POST' });
        status.style.color = '#1d9e75';
        status.textContent = '✓ Установщик запущен. Соединение скоро прервётся.';
      } else {
        status.style.display = 'inline';
        status.style.color = '#888';
        status.textContent = `Обновление отложено`;
      }
    } else {
      status.style.display = 'inline';
      status.style.color = '#1d9e75';
      status.textContent = '✓ Установлена последняя версия';
    }
  } catch {
    status.style.display = 'inline';
    status.style.color = '#c0392b';
    status.textContent = 'Ошибка проверки обновлений';
  } finally {
    btn.disabled = false;
    btn.textContent = '🔄 Проверить обновления';
  }
}

async function logout() {
  await fetch('/api/admin/logout', { method: 'POST' });
  window.location.href = '/admin-login.html';
}

// ─── Dialog helpers ───────────────────────────────────────────────────────────
function closeDlg(id) {
  const el = document.getElementById(id);
  if (!el) return;
  if (el.classList.contains('modal-scrim')) {
    el.classList.remove('open');
  } else {
    el.style.display = 'none';
  }
}

function closeDlgIfOverlay(event, id) {
  if (event.target === document.getElementById(id)) closeDlg(id);
}

function stepDur(id, delta, min, max) {
  const el = document.getElementById(id);
  if (!el) return;
  let v = parseInt(el.value) || 0;
  el.value = Math.min(max, Math.max(min, v + delta));
  el.dispatchEvent(new Event('input'));
}

function ssSetCardType(val) {
  document.getElementById(val === 'temp' ? 'rbSsCardTemp' : 'rbSsCardRegular').checked = true;
  document.getElementById('ssBtnCardRegular').classList.toggle('on', val === 'regular');
  document.getElementById('ssBtnCardTemp').classList.toggle('on', val === 'temp');
  ssOnCardTypeChanged();
}

// Close on overlay click — only if mousedown also started on the overlay,
// so text selection inside the dialog doesn't accidentally close it.
let _dlgMousedownTarget = null;
document.addEventListener('mousedown', e => { _dlgMousedownTarget = e.target; });
document.addEventListener('click', e => {
  if (e.target.classList.contains('dlg-overlay') && _dlgMousedownTarget === e.target)
    e.target.style.display = 'none';
});

// ─── Toast ────────────────────────────────────────────────────────────────────
function toast(msg, type = '') {
  const el = document.createElement('div');
  el.className = 'toast' + (type ? ' ' + type : '');
  el.textContent = msg;
  document.getElementById('toastArea').appendChild(el);
  setTimeout(() => el.remove(), 4000);
}

// ─── Utils ────────────────────────────────────────────────────────────────────
function fmtTime(s) {
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return `${pad(h)}:${pad(m)}:${pad(sec)}`;
}
function pad(n) { return String(n).padStart(2, '0'); }
function fmt(n) { return n.toLocaleString('ru-RU') + ' сум'; }
function fmtDate(d) {
  const dt = new Date(d);
  return `${pad(dt.getDate())}.${pad(dt.getMonth()+1)} ${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
}
function esc(s) {
  if (!s) return '';
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
// ─── Screen viewer ────────────────────────────────────────────────────────────
async function openScreenView(pcNumber) {
  if (_screenPc) await closeScreenView();
  _screenPc = pcNumber;
  document.getElementById('dlgScreenViewTitle').textContent = `Экран: ${pcNumber}`;
  document.getElementById('screenViewImg').src = '';
  document.getElementById('screenViewStatus').textContent = 'Подключение...';
  document.getElementById('dlgScreenView').style.display = 'flex';
  try { await fetch(`/api/screenshot/${encodeURIComponent(pcNumber)}/watch`, { method: 'POST' }); }
  catch (e) { /* ignore */ }
  _screenInterval = setInterval(pollScreen, 500);
}

async function closeScreenView() {
  const pc = _screenPc;
  _screenPc = null;
  clearInterval(_screenInterval);
  _screenInterval = null;
  document.getElementById('dlgScreenView').style.display = 'none';
  if (pc) {
    try { await fetch(`/api/screenshot/${encodeURIComponent(pc)}/unwatch`, { method: 'POST' }); }
    catch (e) { /* ignore */ }
  }
}

async function pollScreen() {
  if (!_screenPc) return;
  try {
    const r = await fetch(`/api/screenshot/${encodeURIComponent(_screenPc)}`, { cache: 'no-store' });
    if (r.status === 204) { document.getElementById('screenViewStatus').textContent = 'Ожидание кадра...'; return; }
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

// ─── Client log viewer ───────────────────────────────────────────────────────
async function requestClientLogs(pcNumber) {
  const dlg = document.getElementById('dlgClientLogs');
  document.getElementById('dlgClientLogsPcName').textContent = pcNumber;
  document.getElementById('dlgClientLogsBody').textContent = '⏳ Запрашиваем логи...';
  dlg.style.display = 'flex';
  try {
    await conn.invoke('SendCommandToPc', pcNumber, 'GET_LOGS', '');
  } catch (e) {
    document.getElementById('dlgClientLogsBody').textContent = 'Ошибка отправки команды: ' + e;
  }
}

function showClientLogs(pcNumber, logContent) {
  document.getElementById('dlgClientLogsPcName').textContent = pcNumber;
  document.getElementById('dlgClientLogsBody').textContent = logContent || '(лог пуст)';
  const body = document.getElementById('dlgClientLogsBody');
  // Прокручиваем вниз чтобы видеть последние строки
  body.scrollTop = body.scrollHeight;
  document.getElementById('dlgClientLogs').style.display = 'flex';
}

function copyClientLogs() {
  const text = document.getElementById('dlgClientLogsBody').textContent;
  navigator.clipboard.writeText(text).then(() => toast('Логи скопированы', 'good'));
}

function periodFrom(p) {
  const t = new Date(); t.setHours(0,0,0,0);
  if (p === 'today') return t;
  if (p === 'week')  { const w = new Date(t); w.setDate(t.getDate() - t.getDay() + 1); return w; }
  if (p === 'month') return new Date(t.getFullYear(), t.getMonth(), 1);
  if (p === 'year')  return new Date(t.getFullYear(), 0, 1);
  return null;
}

// ─── Readers ──────────────────────────────────────────────────────────────────
let readersData  = [];        // текущая страница (для диалогов редактирования)
let readersTotal = 0;         // всего записей на сервере
let readersSortCol = 'fullName';
let readersSortAsc = true;
let readersPage    = 0;
let readersPageSize = 25;     // по умолчанию

const INVALID_DATE = '30-12-1899';

function parseReaderDate(s) {
  if (!s || s === INVALID_DATE) return 0;
  const p = s.split('-');
  if (p.length === 3) return new Date(+p[2], +p[1] - 1, +p[0]).getTime();
  return 0;
}

function cleanUpdatedAt(r) {
  // treat 30-12-1899 (Excel -1) same as empty — fall back to registeredAt
  return (r.updatedAt && r.updatedAt !== INVALID_DATE) ? r.updatedAt : '';
}

function cardIdNum(id) {
  // FAA220500035 → берём только цифровую часть
  const m = id.match(/\d+/);
  return m ? parseInt(m[0], 10) : 0;
}

function sortReaders(col) {
  if (readersSortCol === col) readersSortAsc = !readersSortAsc;
  else { readersSortCol = col; readersSortAsc = true; }
  readersPage = 0;
  loadReaders();
}

function sortArrow(col) {
  if (readersSortCol !== col) return '<span style="color:#444;margin-left:4px">⇅</span>';
  return readersSortAsc
    ? '<span style="color:#1d9e75;margin-left:4px">↑</span>'
    : '<span style="color:#1d9e75;margin-left:4px">↓</span>';
}

async function loadReaders() {
  const search = document.getElementById('readersSearch')?.value.trim() || '';
  const params = new URLSearchParams({
    page:     readersPage,
    pageSize: readersPageSize,
    sort:     readersSortCol,
    order:    readersSortAsc ? 'asc' : 'desc',
  });
  if (search) params.set('search', search);

  const res = await fetch('/api/admin/readers?' + params).catch(() => null);
  if (!res || !res.ok) return;
  const data = await res.json();

  readersData  = data.items  || [];
  readersTotal = data.total  || 0;
  renderReadersTable();

  const q = search ? ` (поиск: «${search}»)` : '';
  document.getElementById('readersCount').textContent =
    `Читателей в базе: ${readersTotal}${q}`;
}

function searchReaders() {
  readersPage = 0;
  loadReaders();
}

function readersSetPageSize(size) {
  readersPageSize = parseInt(size) || 25;
  readersPage = 0;
  loadReaders();
}

function renderReadersTable() {
  const el = document.getElementById('readersTable');
  if (!readersData.length && readersTotal === 0) {
    el.innerHTML = '<div class="fin-empty">Нет читателей. Загрузите данные через «Импорт Excel» или добавьте вручную.</div>';
    return;
  }

  const pages = Math.ceil(readersTotal / readersPageSize);
  const start = readersPage * readersPageSize + 1;
  const end   = Math.min(start + readersData.length - 1, readersTotal);

  const cols = '160px 1fr 110px 55px 170px 115px 115px 74px';
  const th = (label, col) =>
    '<span onclick="sortReaders(\'' + col + '\')" style="cursor:pointer;user-select:none">' + label + sortArrow(col) + '</span>';

  var html = '<div class="fin-table-header" style="grid-template-columns:' + cols + '">' +
    th('ID билета','cardId') + th('ФИО','fullName') +
    '<span>Дата рождения</span><span>Пол</span><span>Категория</span>' +
    th('Дата регистрации','registeredAt') + th('Дата обновления','updatedAt') +
    '<span></span></div>';

  for (var i = 0; i < readersData.length; i++) {
    var r = readersData[i];
    var age = calcReaderAge(r.birthDate);
    html += '<div class="fin-row" style="grid-template-columns:' + cols + '">' +
      '<span style="font-family:monospace;font-size:12px">' + esc(r.cardId) + '</span>' +
      '<b>' + esc(r.fullName) + '</b>' +
      '<span>' + esc(r.birthDate) + (age !== null ? ' <span style="color:#555">(' + age + ' л.)</span>' : '') + '</span>' +
      '<span>' + esc(r.gender) + '</span>' +
      '<span>' + esc(r.category) + '</span>' +
      '<span style="color:#555">' + esc(r.registeredAt) + '</span>' +
      '<span style="color:' + (cleanUpdatedAt(r) ? '#aaa' : '#444') + '">' + esc(cleanUpdatedAt(r) || r.registeredAt || '—') + '</span>' +
      '<span style="display:flex;gap:4px">' +
        '<button data-action="edit" data-id="' + esc(r.cardId) + '" title="Редактировать" style="padding:2px 7px;font-size:11px;border-radius:4px;cursor:pointer;border:1px solid #3D3D6B;background:#1A1A2E;color:#aaa">&#9998;</button>' +
        '<button data-action="del" data-id="' + esc(r.cardId) + '" data-name="' + esc(r.fullName || r.cardId) + '" title="Удалить" style="padding:2px 7px;font-size:11px;border-radius:4px;cursor:pointer;border:1px solid #5D2A2A;background:#2D1A1A;color:#F87171">&#128465;</button>' +
      '</span></div>';
  }

  // Панель пагинации
  var btnStyle = 'padding:4px 12px;border-radius:4px;cursor:pointer;border:1px solid #3D3D6B;background:#1A1A2E;color:#aaa';
  var selStyle = 'background:#1A1A2E;border:1px solid #3D3D6B;border-radius:4px;color:#aaa;padding:3px 6px;font-size:12px';
  html += '<div style="display:flex;align-items:center;gap:10px;padding:10px 16px;border-top:1px solid #1a1a30;background:#111128;flex-wrap:wrap">' +
    '<button onclick="readersPageNav(-1)" ' + (readersPage === 0 ? 'disabled' : '') + ' style="' + btnStyle + '">&#8249;</button>' +
    '<span style="color:#666;font-size:13px">' + start + '–' + end + ' из ' + readersTotal + '</span>' +
    '<button onclick="readersPageNav(1)" ' + (readersPage >= pages - 1 ? 'disabled' : '') + ' style="' + btnStyle + '">&#8250;</button>' +
    '<span style="color:#444;font-size:12px">|</span>' +
    '<span style="color:#666;font-size:12px">Строк:</span>' +
    '<select onchange="readersSetPageSize(this.value)" style="' + selStyle + '">' +
    [25,50,100,250].map(function(n) {
      return '<option value="' + n + '"' + (n === readersPageSize ? ' selected' : '') + '>' + n + '</option>';
    }).join('') +
    '</select>' +
  '</div>';

  el.innerHTML = html;
}

function readersPageNav(dir) {
  var pages = Math.ceil(readersTotal / readersPageSize);
  readersPage = Math.max(0, Math.min(pages - 1, readersPage + dir));
  loadReaders();
}

function calcReaderAge(birthDate) {
  if (!birthDate) return null;
  const p = birthDate.split('-');
  if (p.length !== 3) return null;
  const bd = new Date(+p[2], +p[1] - 1, +p[0]);
  if (isNaN(bd)) return null;
  const today = new Date();
  let age = today.getFullYear() - bd.getFullYear();
  if (today < new Date(today.getFullYear(), bd.getMonth(), bd.getDate())) age--;
  return age;
}

function openImportDlg() {
  document.getElementById('importFile').value = '';
  document.getElementById('importResult').style.display = 'none';
  document.getElementById('importResult').innerHTML = '';
  const btn = document.getElementById('importBtn');
  btn.disabled = false;
  btn.textContent = 'Загрузить';
  btn.style.display = '';
  document.getElementById('dlgImportReaders').style.display = 'flex';
}

async function doImport() {
  const input = document.getElementById('importFile');
  if (!input.files || !input.files.length) { toast('Выберите файл', 'warn'); return; }
  const btn = document.getElementById('importBtn');
  btn.disabled = true;
  btn.textContent = 'Загрузка…';
  try {
    const fd = new FormData();
    fd.append('file', input.files[0]);
    const r = await fetch('/api/admin/readers/import', { method: 'POST', body: fd });
    const data = await r.json();
    if (!r.ok) { toast(data.error || 'Ошибка', 'warn'); btn.disabled = false; btn.textContent = 'Загрузить'; return; }

    const html = `<div style="display:flex;gap:20px">
      <span style="color:#1d9e75">✓ Добавлено: <b>${data.added}</b></span>
      ${data.updated ? `<span style="color:#60a5fa">↻ Обновлено: <b>${data.updated}</b></span>` : ''}
      <span style="color:#666">Пропущено: <b>${data.skipped}</b></span>
    </div>`;
    const resultEl = document.getElementById('importResult');
    resultEl.innerHTML = html;
    resultEl.style.display = 'block';
    btn.style.display = 'none';
    await loadReaders();
    toast(`Импорт завершён: добавлено ${data.added}, обновлено ${data.updated ?? 0}`, 'success');
  } catch (e) {
    toast('Ошибка импорта: ' + e.message, 'warn');
    btn.disabled = false;
    btn.textContent = 'Загрузить';
  }
}

function exportReaderStats() {
  window.open('/api/admin/readers/stats/export', '_blank');
}

// ─── Категории читателей (фиксированный список) ───────────────────────────────
var READER_CATEGORIES = [
  'Абитуриент','Академик','Веб-пользователь','Доцент','Другой',
  'Иностранец','Магистр','Научный сотрудник','Не работающий','Пенсионер',
  'Профессор','Рабочий','Служащий','Студент','Учащийся'
];

function buildCatOptions(selected) {
  var opts = '<option value="">—</option>';
  for (var i = 0; i < READER_CATEGORIES.length; i++) {
    var c = READER_CATEGORIES[i];
    opts += '<option value="' + c + '"' + (c === selected ? ' selected' : '') + '>' + c + '</option>';
  }
  return opts;
}

function autoFormatDate(el) {
  var v = el.value.replace(/\D/g, '').slice(0, 8);
  if (v.length >= 5) v = v.slice(0,2) + '-' + v.slice(2,4) + '-' + v.slice(4);
  else if (v.length >= 3) v = v.slice(0,2) + '-' + v.slice(2);
  el.value = v;
}

function onReaderIdNumInput(el) {
  el.value = el.value.replace(/\D/g,'').slice(0,9);
}

function openEditReader(cardId) {
  var r = readersData.find(function(x) { return x.cardId === cardId; });
  if (!r) return;
  document.getElementById('editReaderCardIdRow').style.display = 'none';
  document.getElementById('editReaderId').value      = r.cardId;
  document.getElementById('editReaderIdInput').value = r.cardId.replace(/^[A-Za-z]+/,'');
  document.getElementById('editReaderIdInput').setAttribute('readonly','true');
  document.getElementById('editReaderName').value    = r.fullName;
  document.getElementById('editReaderBirth').value   = r.birthDate;
  document.getElementById('editReaderCat').innerHTML = buildCatOptions(r.category);
  document.getElementById('editReaderGender').value  = r.gender;
  document.getElementById('editReaderReg').value     = r.registeredAt;
  document.getElementById('editReaderUpd').value     = cleanUpdatedAt(r) || r.registeredAt || '';
  document.getElementById('dlgEditReaderTitle').textContent = 'Редактировать читателя';
  document.getElementById('editReaderSaveBtn').onclick = saveEditReader;
  document.getElementById('dlgEditReader').style.display = 'flex';
}

async function saveEditReader() {
  var reader = {
    cardId:       document.getElementById('editReaderId').value,
    fullName:     document.getElementById('editReaderName').value.trim(),
    birthDate:    document.getElementById('editReaderBirth').value.trim(),
    category:     document.getElementById('editReaderCat').value,
    gender:       document.getElementById('editReaderGender').value,
    registeredAt: document.getElementById('editReaderReg').value.trim(),
    updatedAt:    document.getElementById('editReaderUpd').value.trim(),
  };
  var btn = document.getElementById('editReaderSaveBtn');
  btn.disabled = true; btn.textContent = '...';
  try {
    var r = await fetch('/api/admin/readers', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(reader)
    });
    if (r.ok) {
      closeDlg('dlgEditReader');
      await loadReaders();
      toast('Читатель обновлён', 'success');
    } else {
      toast('Ошибка сохранения', 'warn');
    }
  } catch(e) { toast('Ошибка сохранения', 'warn'); }
  btn.disabled = false; btn.textContent = 'Сохранить';
}

// ─── Statistics Page ──────────────────────────────────────────────────────────
let currentStatsTab  = 'analytics';
let analyticsPeriod  = 'day';
let analyticsQuarter = 1;
let reportPeriod     = 'day';

function initStatsPage() {
  if (currentStatsTab === 'report') {
    const today = new Date();
    const dateStr = today.toISOString().split('T')[0];
    if (!document.getElementById('rptDateDay').value)   document.getElementById('rptDateDay').value   = dateStr;
    if (!document.getElementById('rptDateMonth').value) document.getElementById('rptDateMonth').value = dateStr.substring(0, 7);
    loadReport();
  }
}

function switchStatsTab(tab) {
  currentStatsTab = tab;
  document.getElementById('statsTabAnalytics').style.display = tab === 'analytics' ? '' : 'none';
  document.getElementById('statsTabReport').style.display    = tab === 'report'    ? '' : 'none';
  document.getElementById('statsBtnAnalytics').classList.toggle('active', tab === 'analytics');
  document.getElementById('statsBtnReport').classList.toggle('active',    tab === 'report');
  if (tab === 'report') {
    const today = new Date();
    const dateStr = today.toISOString().split('T')[0];
    if (!document.getElementById('rptDateDay').value)   document.getElementById('rptDateDay').value   = dateStr;
    if (!document.getElementById('rptDateMonth').value) document.getElementById('rptDateMonth').value = dateStr.substring(0, 7);
    loadReport();
  }
}

function setAnalyticsPeriod(period) {
  analyticsPeriod = period;
  ['day','month','quarter','year'].forEach(p => {
    document.getElementById('anlBtn' + p.charAt(0).toUpperCase() + p.slice(1)).classList.toggle('active', p === period);
  });
  document.getElementById('anlDateDay').style.display     = period === 'day'     ? ''     : 'none';
  document.getElementById('anlDateMonth').style.display   = period === 'month'   ? ''     : 'none';
  document.getElementById('anlDateQuarter').style.display = period === 'quarter' ? 'flex' : 'none';
  document.getElementById('anlDateYear').style.display    = period === 'year'    ? ''     : 'none';
}

function setQuarter(q) {
  analyticsQuarter = q;
  [1,2,3,4].forEach(i => document.getElementById('anlQ' + i).classList.toggle('active', i === q));
}

function getAnalyticsDateStr() {
  switch (analyticsPeriod) {
    case 'day':     return document.getElementById('anlDateDay').value;
    case 'month':   return document.getElementById('anlDateMonth').value;
    case 'quarter': return (document.getElementById('anlYearQuarter').value || new Date().getFullYear()) + '-Q' + analyticsQuarter;
    case 'year':    return String(document.getElementById('anlDateYear').value || new Date().getFullYear());
  }
  return '';
}

async function loadAnalytics() {
  const dateStr = getAnalyticsDateStr();
  if (!dateStr) { toast('Выберите дату', 'warn'); return; }

  const emptyEl = document.getElementById('anlEmpty');
  emptyEl.style.display = '';
  emptyEl.textContent = 'Загрузка…';
  document.getElementById('anlSummary').style.display = 'none';
  document.getElementById('anlContent').style.display = 'none';

  try {
    const r = await fetch(`/api/admin/readers/analytics?period=${analyticsPeriod}&date=${encodeURIComponent(dateStr)}`);
    const data = await r.json();
    if (!r.ok) { emptyEl.textContent = data.error || 'Ошибка'; return; }
    renderAnalytics(data);
  } catch {
    emptyEl.textContent = 'Ошибка загрузки';
  }
}

function renderAnalytics(data) {
  const sumEl = document.getElementById('anlSummary');
  sumEl.style.display = 'flex';
  sumEl.innerHTML = `
    <div class="stat-card" style="flex:1"><div class="stat-label">Визитов всего</div><div class="stat-val">${data.totalVisits}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Анонимных визитов</div><div class="stat-val orange">${data.anonymousVisits}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Уникальных читателей</div><div class="stat-val">${data.totalUniqueReaders}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Выручка (сум)</div><div class="stat-val blue">${data.totalRevenue.toLocaleString('ru-RU')}</div></div>
    <div class="stat-card" style="flex:2;min-width:160px"><div class="stat-label">Период</div><div style="font-size:13px;color:#ccc;margin-top:4px">${esc(data.periodLabel)}</div></div>`;

  const emptyEl = document.getElementById('anlEmpty');
  if (!data.totalVisits) {
    emptyEl.style.display = '';
    emptyEl.textContent = 'Нет данных о посещениях зарегистрированных читателей за выбранный период';
    document.getElementById('anlContent').style.display = 'none';
    return;
  }
  emptyEl.style.display = 'none';
  document.getElementById('anlContent').style.display = '';

  document.getElementById('anlGenderTable').innerHTML = buildAnalyticsTable(
    ['Пол', 'Визиты', 'Уникальных'],
    data.gender.map(g => [g.name, g.visits, g.uniqueReaders]), true);

  document.getElementById('anlCategoryTable').innerHTML = buildAnalyticsTable(
    ['Категория', 'Визиты', 'Уникальных'],
    data.categories.map(c => [c.name, c.visits, c.uniqueReaders]), true);

  // Age groups: show only gender columns that have at least one non-zero value
  const knownGenders = [...new Set(data.ageGroups.flatMap(g => Object.keys(g.byGender || {})))].sort();
  const activeGenders = knownGenders.filter(gn =>
    data.ageGroups.some(ag => (ag.byGender[gn]?.visits ?? 0) > 0 || (ag.byGender[gn]?.uniqueReaders ?? 0) > 0));
  const ageHeaders = ['Группа', 'Визиты', 'Уникальных', ...activeGenders.flatMap(g => [`${g} визиты`, `${g} уник.`])];
  const ageRows = data.ageGroups.map(g => {
    const byG = g.byGender || {};
    return [g.group, g.visits, g.uniqueReaders, ...activeGenders.flatMap(gn => [byG[gn]?.visits ?? 0, byG[gn]?.uniqueReaders ?? 0])];
  });
  document.getElementById('anlAgeTable').innerHTML = buildAnalyticsTable(ageHeaders, ageRows, true);

  // Services table with «Компьютер» row and «Итого» footer
  document.getElementById('anlServicesTable').innerHTML = buildServicesTable(data.services, data.pcStats);

  // PC stats block
  renderPcStats(data.pcStats);
}

function buildServicesTable(services, pcStats) {
  const pc = pcStats || {};
  const border = '1px solid #2A2A4A';
  const rowBorder = '1px solid #1E1E38';
  const thS = h => `<th style="padding:7px 14px;white-space:nowrap;color:#777;font-weight:500;font-size:12px;text-transform:uppercase;letter-spacing:.4px;border-bottom:2px solid #2A2A4A;background:#111128">${esc(h)}</th>`;
  const thC = h => `<th style="padding:7px 14px;text-align:center;color:#777;font-weight:500;font-size:12px;text-transform:uppercase;letter-spacing:.4px;border-bottom:2px solid #2A2A4A;border-left:${border};background:#111128">${esc(h)}</th>`;

  let html = `<div style="overflow-x:auto;border:1px solid #2A2A4A;border-radius:8px;overflow:hidden">
    <table style="width:100%;border-collapse:collapse">
    <thead><tr>${thS('Услуга')}${thC('Кол-во')}${thC('Сумма (сум)')}</tr></thead><tbody>`;

  const tdN = (v, ri) => `<td style="padding:7px 14px;border-bottom:${rowBorder};font-size:13px;color:#d0d0e8;${ri%2?'background:#0e0e22':''}">${esc(String(v))}</td>`;
  const tdC = (v, ri, color) => `<td style="padding:7px 14px;border-bottom:${rowBorder};border-left:${border};text-align:center;font-size:13px;color:${color||((v===0||v==='0')?'#444':'#aaa')};${ri%2?'background:#0e0e22':''}">${typeof v==='number'?v.toLocaleString('ru-RU'):esc(String(v))}</td>`;

  // «Компьютер» row first
  if ((pc.totalSessions ?? 0) > 0) {
    html += `<tr><td style="padding:7px 14px;border-bottom:${rowBorder};font-size:13px;color:#7799cc;font-weight:500">🖥 Компьютер (сессии)</td>
      <td style="padding:7px 14px;border-bottom:${rowBorder};border-left:${border};text-align:center;font-size:13px;color:#aaa">${(pc.totalSessions||0).toLocaleString('ru-RU')}</td>
      <td style="padding:7px 14px;border-bottom:${rowBorder};border-left:${border};text-align:center;font-size:13px;color:#aaa">${(pc.totalRevenue||0).toLocaleString('ru-RU')}</td></tr>`;
  }

  services.forEach((s, i) => {
    html += `<tr>${tdN(s.name, i)}${tdC(s.quantity, i)}${tdC(s.totalAmount, i)}</tr>`;
  });

  // «Итого» footer row
  const totalQty = (pc.totalSessions || 0) + services.reduce((sum, s) => sum + s.quantity, 0);
  const totalAmt = (pc.totalRevenue  || 0) + services.reduce((sum, s) => sum + s.totalAmount, 0);
  if (totalQty > 0 || totalAmt > 0) {
    html += `<tr style="border-top:2px solid #2A2A4A">
      <td style="padding:8px 14px;font-size:13px;font-weight:700;color:#e0e0f0">Итого</td>
      <td style="padding:8px 14px;border-left:${border};text-align:center;font-size:13px;font-weight:700;color:#e0e0f0">${totalQty.toLocaleString('ru-RU')}</td>
      <td style="padding:8px 14px;border-left:${border};text-align:center;font-size:13px;font-weight:700;color:#1d9e75">${totalAmt.toLocaleString('ru-RU')}</td></tr>`;
  }

  if (!services.length && !(pc.totalSessions > 0)) {
    html += `<tr><td colspan="3" style="padding:16px;text-align:center;color:#444;font-size:13px">Услуги в данном периоде не использовались</td></tr>`;
  }

  html += '</tbody></table></div>';
  return html;
}

function renderPcStats(pc) {
  if (!pc) return;
  // Summary cards
  document.getElementById('anlPcSummary').innerHTML = `
    <div class="stat-card" style="flex:1"><div class="stat-label">Сессий за ПК</div><div class="stat-val">${pc.totalSessions}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Анонимных сессий</div><div class="stat-val orange">${pc.anonSessions}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Уникальных читателей</div><div class="stat-val">${pc.uniqueReaders}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Выручка ПК (сум)</div><div class="stat-val blue">${pc.totalRevenue.toLocaleString('ru-RU')}</div></div>`;

  // Gender / Category breakdowns
  document.getElementById('anlPcGenderTable').innerHTML = buildAnalyticsTable(
    ['Пол', 'Сессий', 'Уникальных'],
    pc.gender.map(g => [g.name, g.sessions, g.uniqueReaders]), true);

  document.getElementById('anlPcCategoryTable').innerHTML = buildAnalyticsTable(
    ['Категория', 'Сессий', 'Уникальных'],
    pc.categories.map(c => [c.name, c.sessions, c.uniqueReaders]), true);

  // Age groups for PC (same auto-hide logic)
  const pcGenders = [...new Set(pc.ageGroups.flatMap(g => Object.keys(g.byGender || {})))].sort();
  const pcActiveG = pcGenders.filter(gn =>
    pc.ageGroups.some(ag => (ag.byGender[gn]?.sessions ?? 0) > 0 || (ag.byGender[gn]?.uniqueReaders ?? 0) > 0));
  const pcAgeHdr = ['Группа', 'Сессий', 'Уникальных', ...pcActiveG.flatMap(g => [`${g} сессий`, `${g} уник.`])];
  const pcAgeRows = pc.ageGroups.map(g => {
    const byG = g.byGender || {};
    return [g.group, g.sessions, g.uniqueReaders, ...pcActiveG.flatMap(gn => [byG[gn]?.sessions ?? 0, byG[gn]?.uniqueReaders ?? 0])];
  });
  document.getElementById('anlPcAgeTable').innerHTML = buildAnalyticsTable(pcAgeHdr, pcAgeRows, true);

  // Top tables
  const topHdr = ['Читатель', 'Категория', 'Визитов', 'Часов'];
  const topVisitRows = pc.topByVisits.map(u => [u.readerName, u.category, u.visits, +(u.totalMinutes / 60).toFixed(1)]);
  const topHoursRows = pc.topByHours.map(u => [u.readerName, u.category, u.visits, +(u.totalMinutes / 60).toFixed(1)]);
  document.getElementById('anlPcTopVisits').innerHTML = topVisitRows.length
    ? buildAnalyticsTable(topHdr, topVisitRows, false)
    : '<div class="fin-empty" style="text-align:left;padding:8px 0">Нет данных</div>';
  document.getElementById('anlPcTopHours').innerHTML = topHoursRows.length
    ? buildAnalyticsTable(topHdr, topHoursRows, false)
    : '<div class="fin-empty" style="text-align:left;padding:8px 0">Нет данных</div>';
}

function buildAnalyticsTable(headers, rows, numericFromCol1 = true) {
  const borderCol = '1px solid #2A2A4A';
  const borderRow = '1px solid #1E1E38';
  const th = (h, i) => {
    const align = (numericFromCol1 && i > 0) ? 'center' : 'left';
    const bl = i > 0 ? `border-left:${borderCol};` : '';
    return `<th style="padding:7px 14px;white-space:nowrap;color:#777;font-weight:500;font-size:12px;text-transform:uppercase;letter-spacing:.4px;border-bottom:2px solid #2A2A4A;text-align:${align};${bl}background:#111128">${esc(String(h))}</th>`;
  };
  const td = (v, i) => {
    const isNum = numericFromCol1 && i > 0;
    const align = isNum ? 'center' : 'left';
    const val = typeof v === 'number' ? v.toLocaleString('ru-RU') : esc(String(v));
    const color = i === 0 ? '#d0d0e8' : (typeof v === 'number' && v === 0 ? '#444' : '#b0b0cc');
    const bl = i > 0 ? `border-left:${borderCol};` : '';
    return `<td style="padding:7px 14px;border-bottom:${borderRow};font-size:13px;text-align:${align};color:${color};${bl}">${val}</td>`;
  };
  if (!rows.length) return '<div class="fin-empty" style="text-align:left;padding:8px 0">Нет данных</div>';
  // Hide columns where every numeric value is 0
  const keep = headers.map((_, ci) =>
    ci === 0 || rows.some(r => { const v = r[ci]; return typeof v === 'number' ? v !== 0 : v !== '0'; })
  );
  const hdr2 = headers.filter((_, ci) => keep[ci]);
  const rows2 = rows.map(r => r.filter((_, ci) => keep[ci]));
  let html = `<div style="overflow-x:auto;border:1px solid #2A2A4A;border-radius:8px;overflow:hidden"><table style="width:100%;border-collapse:collapse">
    <thead><tr>${hdr2.map((h, i) => th(h, i)).join('')}</tr></thead><tbody>`;
  rows2.forEach((row, ri) => {
    const bg = ri % 2 === 1 ? 'background:#0e0e22;' : '';
    html += `<tr style="${bg}">${row.map((v, i) => td(v, i)).join('')}</tr>`;
  });
  html += '</tbody></table></div>';
  return html;
}

function exportAnalytics() {
  const dateStr = getAnalyticsDateStr();
  if (!dateStr) { toast('Выберите дату', 'warn'); return; }
  window.open(`/api/admin/readers/analytics/export?period=${analyticsPeriod}&date=${encodeURIComponent(dateStr)}`, '_blank');
}

// ─── Readers Report ───────────────────────────────────────────────────────────
function setReportPeriod(period) {
  reportPeriod = period;
  document.getElementById('rptBtnDay').classList.toggle('active', period === 'day');
  document.getElementById('rptBtnMonth').classList.toggle('active', period === 'month');
  document.getElementById('rptDateDay').style.display = period === 'day' ? '' : 'none';
  document.getElementById('rptDateMonth').style.display = period === 'month' ? '' : 'none';
  loadReport();
}

// ─── Delete reader ────────────────────────────────────────────────────────────
async function deleteReader(cardId, name) {
  if (!confirm(`Удалить читателя «${name}»?\nЭто действие нельзя отменить.`)) return;
  const r = await fetch(`/api/admin/readers/${encodeURIComponent(cardId)}`, { method: 'DELETE' });
  if (r.ok) { await loadReaders(); toast('Читатель удалён', 'success'); }
  else toast('Ошибка удаления', 'warn');
}

async function deleteAllReaders() {
  if (!confirm('Удалить ВСЕХ читателей (' + readersData.length + ' записей)?\nЭто действие нельзя отменить.')) return;
  const r = await fetch('/api/admin/readers', { method: 'DELETE' });
  if (r.ok) { await loadReaders(); toast('База читателей очищена', 'success'); }
  else toast('Ошибка очистки', 'warn');
}

// ─── Add reader manually ──────────────────────────────────────────────────────
function openAddReader() {
  document.getElementById('editReaderCardIdRow').style.display = '';
  document.getElementById('editReaderId').value = '';
  document.getElementById('editReaderIdInput').removeAttribute('readonly');
  document.getElementById('editReaderIdInput').value = '';
  document.getElementById('editReaderName').value  = '';
  document.getElementById('editReaderBirth').value = '';
  document.getElementById('editReaderCat').innerHTML = buildCatOptions('Студент');
  document.getElementById('editReaderGender').value = '';
  var d = new Date();
  var dd = String(d.getDate()).padStart(2,'0');
  var mm = String(d.getMonth()+1).padStart(2,'0');
  var yyyy = d.getFullYear();
  var today = dd + '-' + mm + '-' + yyyy;
  document.getElementById('editReaderReg').value = today;
  document.getElementById('editReaderUpd').value = today;
  document.getElementById('dlgEditReaderTitle').textContent = 'Добавить читателя';
  document.getElementById('editReaderSaveBtn').onclick = saveAddReader;
  document.getElementById('dlgEditReader').style.display = 'flex';
}

async function saveAddReader() {
  var numPart = document.getElementById('editReaderIdInput').value.trim();
  if (!numPart) { toast('Введите номер билета (цифры)', 'warn'); return; }
  var prefix = (settings && settings.readerCardPrefix) || 'FAA';
  var cardId = prefix + numPart;
  var reader = {
    cardId:       cardId,
    fullName:     document.getElementById('editReaderName').value.trim(),
    birthDate:    document.getElementById('editReaderBirth').value.trim(),
    category:     document.getElementById('editReaderCat').value,
    gender:       document.getElementById('editReaderGender').value,
    registeredAt: document.getElementById('editReaderReg').value.trim(),
    updatedAt:    document.getElementById('editReaderUpd').value.trim(),
  };
  var btn = document.getElementById('editReaderSaveBtn');
  btn.disabled = true; btn.textContent = '...';
  try {
    var res = await fetch('/api/admin/readers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(reader)
    });
    var data = await res.json();
    if (res.ok) {
      closeDlg('dlgEditReader');
      await loadReaders();
      toast('Читатель добавлен', 'success');
    } else {
      toast(data.error || 'Ошибка добавления', 'warn');
    }
  } catch(e) { toast('Ошибка добавления', 'warn'); }
  btn.disabled = false; btn.textContent = 'Сохранить';
}

// ─── Clear readers report ─────────────────────────────────────────────────────
function clearReadersReport() {
  document.getElementById('reportTable').innerHTML  = '<div class="fin-empty">Выберите период и нажмите «Показать»</div>';
  document.getElementById('reportSummary').style.display = 'none';
}

async function loadReport() {
  const dateVal = reportPeriod === 'day'
    ? document.getElementById('rptDateDay').value
    : document.getElementById('rptDateMonth').value;
  if (!dateVal) return;

  const el = document.getElementById('reportTable');
  el.innerHTML = '<div class="fin-empty">Загрузка…</div>';
  document.getElementById('reportSummary').style.display = 'none';

  try {
    const r = await fetch(`/api/admin/readers/report?period=${reportPeriod}&date=${encodeURIComponent(dateVal)}`);
    const data = await r.json();
    if (!r.ok) { el.innerHTML = `<div class="fin-empty">${esc(data.error || 'Ошибка')}</div>`; return; }
    renderReport(data);
  } catch {
    el.innerHTML = '<div class="fin-empty">Ошибка загрузки</div>';
  }
}

function renderReport(data) {
  const { items, serviceColumns = [], summary } = data;

  const sumEl = document.getElementById('reportSummary');
  sumEl.style.display = 'flex';
  const hrs = (summary.totalDurationMin / 60).toFixed(1);
  sumEl.innerHTML = `
    <div class="stat-card" style="flex:1"><div class="stat-label">Посещений</div><div class="stat-val">${items.length}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Сессий</div><div class="stat-val">${summary.totalSessions}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Читателей</div><div class="stat-val">${summary.totalUniqueReaders}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Времени (ч)</div><div class="stat-val green">${hrs}</div></div>
    <div class="stat-card" style="flex:1"><div class="stat-label">Итого (сум)</div><div class="stat-val blue">${summary.totalAmount.toLocaleString('ru-RU')}</div></div>`;

  const el = document.getElementById('reportTable');
  if (!items.length) {
    el.innerHTML = '<div class="fin-empty">Нет данных за выбранный период</div>';
    return;
  }

  // serviceColumns is [{id, name}, ...]; row.services is keyed by id
  const th  = s => `<th style="text-align:left;padding:6px 8px;white-space:nowrap;color:#666;font-weight:500;border-bottom:1px solid #2A2A4A">${s}</th>`;
  const thc = s => `<th style="text-align:center;padding:6px 8px;white-space:nowrap;color:#666;font-weight:500;border-bottom:1px solid #2A2A4A">${s}</th>`;

  let html = `<div style="overflow-x:auto"><table style="width:100%;border-collapse:collapse;font-size:13px">
    <thead><tr>
      ${th('Дата/Время')}${th('Читатель')}${th('Категория')}${th('ПК')}${thc('Мин')}
      ${serviceColumns.map(sc => thc(esc(sc.name))).join('')}
      ${th('Итого')}
    </tr></thead><tbody>`;

  items.forEach(row => {
    const dt = new Date(row.timestamp);
    const dtStr = dt.toLocaleDateString('ru-RU', { day:'2-digit', month:'2-digit' }) + ' '
                + dt.toLocaleTimeString('ru-RU', { hour:'2-digit', minute:'2-digit' });
    const nameColor = row.readerStatus === 'registered' ? '#e0e0f0'
                    : row.readerStatus === 'temp'        ? '#AAAACC'
                    : row.readerStatus === 'anonymous'   ? '#666' : '#f59e0b';

    const td = (content, style = '') =>
      `<td style="padding:6px 8px;border-bottom:1px solid #1A1A2E${style ? ';' + style : ''}">${content}</td>`;

    let tds = '';
    tds += td(`<span style="color:#666;font-size:12px">${dtStr}</span>`);
    tds += td(`<span style="color:${nameColor}">${esc(row.readerName || row.readerId || '—')}</span>`);
    tds += td(`<span style="color:#666;font-size:12px">${esc(row.readerCategory || '—')}</span>`);
    tds += td(`<span style="font-family:monospace;font-size:12px">${esc(row.pcNumber || '—')}</span>`);
    tds += td(row.hasSession ? `<span style="color:#888">${row.durationMin}</span>` : `<span style="color:#333">—</span>`, 'text-align:center');

    serviceColumns.forEach(sc => {
      const s = row.services?.[sc.id];
      if (s) {
        tds += td(`<b style="color:#1d9e75">${s.qty}</b><br><span style="color:#555;font-size:11px">${s.amount.toLocaleString('ru-RU')}</span>`, 'text-align:center');
      } else {
        tds += td(`<span style="color:#333">—</span>`, 'text-align:center');
      }
    });

    tds += td(`<b style="color:#1d9e75">${row.totalAmount.toLocaleString('ru-RU')}</b>`);
    html += `<tr>${tds}</tr>`;
  });

  html += '</tbody></table></div>';
  el.innerHTML = html;
}

function exportReport() {
  const dateVal = reportPeriod === 'day'
    ? document.getElementById('rptDateDay').value
    : document.getElementById('rptDateMonth').value;
  if (!dateVal) { toast('Выберите дату', 'warn'); return; }
  window.open(`/api/admin/readers/report/export?period=${reportPeriod}&date=${encodeURIComponent(dateVal)}`, '_blank');
}

// ─── Admin Service Dialog ─────────────────────────────────────────────────────
let _adminSvcRows = [];
let _adminSvcTargetPc = '';

function openAdminServiceDlg(pcNumber) {
  const svcTypes = (settings.services || []).filter(s => s.isActive);
  if (!svcTypes.length) { toast('Нет доступных услуг. Добавьте услуги в Настройках → Услуги.', 'warn'); return; }

  _adminSvcTargetPc = pcNumber || '';

  // Fill PC selector
  const pcSel = document.getElementById('dlgAdminSvcPc');
  pcSel.innerHTML = '<option value="">— Без привязки —</option>';
  Object.values(pcs)
    .filter(c => c.isSession)
    .sort((a, b) => a.pcNumberValue - b.pcNumberValue)
    .forEach(c => {
      const reader = c.userName || c.readerId || '(анонимный)';
      pcSel.innerHTML += `<option value="${esc(c.pcNumber)}">${esc(c.pcNumber)} — ${esc(reader)}</option>`;
    });

  // Pre-select if PC has session
  if (_adminSvcTargetPc && pcs[_adminSvcTargetPc]?.isSession) pcSel.value = _adminSvcTargetPc;
  else pcSel.value = '';

  // Init rows
  _adminSvcRows = [{ id: Date.now(), typeId: svcTypes[0]?.id || '', qty: 1 }];

  // Reset reader input and payment
  const readerInput = document.getElementById('dlgAdminSvcReaderId');
  if (readerInput) readerInput.value = '';
  const payNowRadio = document.querySelector('[name="svcAdminPay"][value="now"]');
  if (payNowRadio) payNowRadio.checked = true;

  renderAdminSvcRows();
  updateAdminSvcTotal();
  onAdminSvcPcChanged();
  document.getElementById('dlgAdminService').classList.add('open');
}

function addAdminSvcRow() {
  const svcTypes = (settings.services || []).filter(s => s.isActive);
  const usedTypes = new Set(_adminSvcRows.map(r => r.typeId));
  const nextType = svcTypes.find(s => !usedTypes.has(s.id));
  if (!nextType) { toast('Все доступные услуги уже добавлены', 'warn'); return; }
  _adminSvcRows.push({ id: Date.now(), typeId: nextType.id, qty: 1 });
  renderAdminSvcRows();
  updateAdminSvcTotal();
}

function removeAdminSvcRow(rowId) {
  _adminSvcRows = _adminSvcRows.filter(r => r.id !== rowId);
  const svcTypes = (settings.services || []).filter(s => s.isActive);
  if (_adminSvcRows.length === 0)
    _adminSvcRows = [{ id: Date.now(), typeId: svcTypes[0]?.id || '', qty: 1 }];
  renderAdminSvcRows();
  updateAdminSvcTotal();
}

function onAdminSvcRowTypeChange(rowId, typeId) {
  const row = _adminSvcRows.find(r => r.id === rowId);
  if (row) row.typeId = typeId;
  renderAdminSvcRows();
  updateAdminSvcTotal();
}

function onAdminSvcRowQtyChange(rowId, qty) {
  const row = _adminSvcRows.find(r => r.id === rowId);
  if (row) row.qty = Math.max(1, parseInt(qty) || 1);
  updateAdminSvcTotal();
}

function renderAdminSvcRows() {
  const container = document.getElementById('dlgAdminSvcList');
  const svcTypes = (settings.services || []).filter(s => s.isActive);
  const usedTypes = new Set(_adminSvcRows.map(r => r.typeId));

  container.innerHTML = _adminSvcRows.map(row => {
    const opts = svcTypes.map(s => {
      const disabled = s.id !== row.typeId && usedTypes.has(s.id) ? 'disabled' : '';
      const selected = s.id === row.typeId ? 'selected' : '';
      return `<option value="${esc(s.id)}" ${disabled} ${selected}>${esc(s.name)} — ${s.price.toLocaleString('ru-RU')} сум/${esc(s.unit)}</option>`;
    }).join('');
    const canRemove = _adminSvcRows.length > 1;
    return `<div style="display:flex;gap:6px;align-items:center;margin-bottom:8px;min-width:0">
      <select style="flex:1;min-width:0;padding:7px 8px;border:1px solid #3D3D6B;border-radius:8px;background:#1A1A2E;color:#fff;font-size:12px"
        onchange="onAdminSvcRowTypeChange(${row.id}, this.value)">${opts}</select>
      <input type="number" min="1" max="999" value="${row.qty}"
        style="width:60px;flex-shrink:0;padding:7px 6px;border:1px solid #3D3D6B;border-radius:8px;background:#1A1A2E;color:#fff;font-size:13px;text-align:center"
        oninput="onAdminSvcRowQtyChange(${row.id}, this.value)">
      ${canRemove
        ? `<button onclick="removeAdminSvcRow(${row.id})" style="flex-shrink:0;width:28px;height:28px;background:#2D1A1A;color:#F87171;border:1px solid #5D2A2A;border-radius:6px;cursor:pointer;font-size:13px;line-height:1;padding:0">✕</button>`
        : '<div style="width:28px;flex-shrink:0"></div>'}
    </div>`;
  }).join('');

  const btnAdd = document.getElementById('btnAdminAddSvcRow');
  if (btnAdd) btnAdd.style.display = usedTypes.size >= svcTypes.length ? 'none' : '';
}

function updateAdminSvcTotal() {
  const svcTypes = (settings.services || []).filter(s => s.isActive);
  let total = 0;
  _adminSvcRows.forEach(row => {
    const svc = svcTypes.find(s => s.id === row.typeId);
    if (svc) total += svc.price * row.qty;
  });
  document.getElementById('dlgAdminSvcTotal').textContent = total > 0 ? total.toLocaleString('ru-RU') + ' сум' : '—';
}

function onAdminSvcPcChanged() {
  const pcVal = document.getElementById('dlgAdminSvcPc').value;
  const info = document.getElementById('dlgAdminSvcSessionInfo');
  const readerRow = document.getElementById('dlgAdminSvcReaderRow');

  if (pcVal && pcs[pcVal]) {
    const c = pcs[pcVal];
    const reader = c.userName || c.readerId || '';
    info.textContent = reader ? `✓ Сессия на ${pcVal}: ${reader}` : `✓ Сессия на ${pcVal} (анонимный)`;
    info.style.display = 'block';
    if (readerRow) readerRow.style.display = 'none';
  } else {
    info.style.display = 'none';
    if (readerRow) readerRow.style.display = '';
  }
  onAdminSvcPayChanged();
}

function onAdminSvcPayChanged() {
  const pcVal = document.getElementById('dlgAdminSvcPc').value;
  const wantLater = document.getElementById('rbAdminSvcLater')?.checked;
  document.getElementById('dlgAdminSvcDeferNote').style.display =
    (wantLater && !pcVal) ? 'block' : 'none';
}

async function confirmAdminService() {
  if (_adminSvcRows.length === 0) { toast('Добавьте хотя бы одну услугу', 'warn'); return; }

  const svcTypes = (settings.services || []).filter(s => s.isActive);
  const validRows = _adminSvcRows.filter(r => r.typeId);
  if (!validRows.length) { toast('Выберите услугу', 'warn'); return; }

  // Compute total for toast
  let total = 0;
  validRows.forEach(row => {
    const svc = svcTypes.find(s => s.id === row.typeId);
    if (svc) total += svc.price * row.qty;
  });

  const pcNumber = document.getElementById('dlgAdminSvcPc').value;
  const payNow   = document.querySelector('[name="svcAdminPay"]:checked')?.value === 'now';

  let readerId = '', readerName = '';
  const c = pcNumber ? pcs[pcNumber] : null;
  if (c) {
    readerId   = c.readerId || '';
    readerName = c.userName || '';
  } else {
    readerId = (document.getElementById('dlgAdminSvcReaderId')?.value || '').trim();
  }

  const items = validRows.map(r => ({ serviceTypeId: r.typeId, quantity: r.qty }));

  closeDlg('dlgAdminService');
  try {
    const r = await fetch('/api/admin/finance/services/batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ items, pcNumber, readerId, readerName, payNow })
    });
    if (!r.ok) { const d = await r.json(); toast(d.error || 'Ошибка', 'warn'); return; }
    toast(`Услуги созданы. Итого: ${total.toLocaleString('ru-RU')} сум${payNow ? '' : ' (отложено)'}`, 'success');
    loadFinance();
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

// ─── Self-update from publish folder ─────────────────────────────────────────
async function applyFolderUpdate() {
  const pathVal = document.getElementById('updateFolderPath').value.trim();
  if (!pathVal) { toast('Укажите путь к папке с обновлением', 'warn'); return; }

  // Remember path in localStorage
  localStorage.setItem('bib_update_folder', pathVal);

  if (!confirm(`Сервер перезапустится для применения обновления.\n\nПапка: ${pathVal}\n\nПродолжить?`)) return;

  const statusEl = document.getElementById('folderUpdateStatus');
  statusEl.textContent = 'Применяется…';
  statusEl.style.color = '#aaa';

  try {
    const r = await fetch('/api/admin/apply-folder-update', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sourcePath: pathVal })
    });
    const data = await r.json();
    if (!r.ok) {
      statusEl.textContent = data.error || 'Ошибка';
      statusEl.style.color = '#f87171';
      toast(data.error || 'Ошибка', 'warn');
      return;
    }
    statusEl.textContent = 'Сервер перезапускается…';
    statusEl.style.color = '#1d9e75';
    toast('Обновление применяется, страница обновится автоматически', 'good');
    // Poll until server is back up
    setTimeout(function poll() {
      fetch('/api/ping').then(r => { if (r.ok) location.reload(); else setTimeout(poll, 2000); }).catch(() => setTimeout(poll, 2000));
    }, 6000);
  } catch {
    statusEl.textContent = 'Нет связи с сервером';
    statusEl.style.color = '#f87171';
  }
}

// Restore saved update folder paths on page load
document.addEventListener('DOMContentLoaded', () => {
  const saved = localStorage.getItem('bib_update_folder');
  if (saved) { const el = document.getElementById('updateFolderPath'); if (el) el.value = saved; }
  const savedClient = localStorage.getItem('bib_client_update_folder');
  if (savedClient) { const el = document.getElementById('clientFolderPath'); if (el) el.value = savedClient; }
});
