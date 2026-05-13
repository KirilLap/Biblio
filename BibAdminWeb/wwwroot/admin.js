'use strict';

// ─── State ─────────────────────────────────────────────────────────────────
let pcs = {};           // pcNumber → state
let finSessions = [];
let finServices = [];
let settings = {};
let finTab = 'sessions';
let activePc = null;    // selected pc for dialogs
let pendingOfflinePc = null;
let pendingConflict = null;
let renamePcVal = null;

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
  conn.on('sessionSummary', d => showSummary(d));
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

  conn.onreconnected(() => {
    setConnStatus(true);
    conn.invoke('RequestSnapshot');
  });
  conn.onclose(() => setConnStatus(false));

  conn.start().then(() => setConnStatus(true)).catch(() => setConnStatus(false));
}

function setConnStatus(ok) {
  const el = document.getElementById('connStatus');
  el.textContent = ok ? '🟢 Подключено' : '🔴 Нет связи';
  el.style.color = ok ? '#1d9e75' : '#f87171';
}

// ─── Navigation ─────────────────────────────────────────────────────────────
function showPage(name) {
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
  document.getElementById('page-' + name).classList.add('active');
  document.querySelector(`[data-page="${name}"]`).classList.add('active');
}

// ─── PC Grid ──────────────────────────────────────────────────────────────
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

  let online = 0, sessions = 0, free = 0;
  list.forEach(c => {
    if (c.isOnline) online++;
    if (c.isSession) sessions++;
    if (c.isFree) free++;
  });
  document.getElementById('pcStats').textContent =
    `Всего: ${list.length} | Онлайн: ${online} | Сессий: ${sessions} | Свободных: ${free}`;

  grid.innerHTML = '';
  list.forEach(c => grid.appendChild(buildPcCard(c)));
}

function buildPcCard(c) {
  const div = document.createElement('div');
  div.className = 'pc-card';
  div.addEventListener('contextmenu', e => { e.preventDefault(); showCtxMenu(e.clientX, e.clientY, c); });
  div.id = 'pc-' + c.pcNumber.replace(/\s/g, '_');

  // Оффлайн + сессия: карточка с тёплым красным акцентом
  if (!c.isOnline && c.isSession) {
    div.style.borderColor = '#c86464';
    div.style.background  = '#1f1010';
  }

  const dotClass = !c.isOnline ? '' : c.isSession && c.isPaused ? 'paused' : c.isSession ? 'session' : c.isFree ? 'free' : 'online';
  const badge = statusBadge(c);

  // Индивидуальные настройки — ★ справа от имени
  const indBadge = c.hasIndividualSettings
    ? `<span class="pc-ind-badge" title="Есть индивидуальные настройки">★</span>`
    : '';

  let timer = '';
  if (c.isSession) {
    const timerCls = `pc-timer ${c.sessionType === 'VIP' ? 'vip-timer' : ''} ${c.isPaused ? 'paused-timer' : ''} ${!c.isOnline ? 'offline-timer' : ''}`;
    timer = `<div class="${timerCls}" id="timer-${esc(c.pcNumber)}">${fmtTime(c.elapsedSeconds)}</div>`;
    if (c.limitSeconds > 0) {
      const rem = Math.max(0, c.limitSeconds - c.elapsedSeconds);
      const remCls = rem <= 300 ? 'style="color:#f87171"' : '';
      timer += `<div class="pc-meta"><span ${remCls}>Осталось: ${fmtTime(rem)}</span></div>`;
    }
    // VIP — показываем стоимость
    if (c.sessionType === 'VIP') {
      const cost = Math.floor(c.elapsedSeconds * (settings.tariff || 3000) / 3600);
      timer += `<div class="pc-meta"><span style="color:#f59e0b">К оплате: ${cost.toLocaleString()} сум</span></div>`;
    }
    // Оффлайн + сессия — предупреждение
    if (!c.isOnline) {
      timer += `<div class="pc-meta"><span style="color:#f87171">📵 нет связи</span></div>`;
    }
  }

  const meta = `<div class="pc-meta">
    ${c.ip ? `<span>${c.ip}</span>` : ''}
    ${c.isSession && c.userName ? `<span>👤 ${esc(c.userName)}</span>` : ''}
    ${c.isSession && c.readerId ? `<span>🪪 ${esc(c.readerId)}</span>` : ''}
    ${c.isSession && c.paidAmount ? `<span>💵 ${c.paidAmount.toLocaleString()} сум</span>` : ''}
  </div>`;

  const actions = buildActions(c);

  div.innerHTML = `
    <div class="pc-card-header">
      <span class="pc-name" onclick="openRename(${c.pcNumberValue}, '${esc(c.customName)}')">${esc(c.pcNumber)}${indBadge}</span>
      <button class="pc-menu-btn" data-pcnumber="${esc(c.pcNumber)}" title="Действия">⋮</button>
      <div class="pc-offline-dot ${dotClass} online"></div>
    </div>
    ${badge}
    ${timer}
    ${meta}
    <div class="pc-actions">${actions}</div>
  `;
  return div;
}

function statusBadge(c) {
  const map = {
    'Оффлайн':     'badge-offline',
    'Заблокирован':'badge-locked',
    'Свободный':   'badge-free',
    'VIP':         'badge-vip',
    'Лимит':       'badge-limit',
    'Пауза':       'badge-pause',
  };
  const cls = map[c.status] || 'badge-locked';
  return `<div class="pc-status-badge ${cls}">${c.status}</div>`;
}

function buildActions(c) {
  if (!c.isOnline) return `<button class="btn btn-outline" onclick="deletePc('${esc(c.pcNumber)}')">🗑</button>`;

  const btns = [];
  if (!c.isSession && !c.isFree) {
    btns.push(`<button class="btn btn-primary" onclick="openStartSession('${esc(c.pcNumber)}')">▶ Старт</button>`);
    btns.push(`<button class="btn btn-outline" onclick="unlock('${esc(c.pcNumber)}')">🔓</button>`);
  }
  if (!c.isSession && c.isFree) {
    btns.push(`<button class="btn btn-primary" onclick="openStartSession('${esc(c.pcNumber)}')">▶ Старт</button>`);
    btns.push(`<button class="btn btn-outline" onclick="lock('${esc(c.pcNumber)}')">🔒</button>`);
  }
  if (c.isSession) {
    btns.push(`<button class="btn btn-danger" onclick="endSession('${esc(c.pcNumber)}')">⏹ Стоп</button>`);
    btns.push(`<button class="btn btn-outline" onclick="togglePause('${esc(c.pcNumber)}')">${c.isPaused ? '▶' : '⏸'}</button>`);
    btns.push(`<button class="btn btn-outline" onclick="openTransfer('${esc(c.pcNumber)}')">↔</button>`);
  }
  return btns.join('');
}

// ─── Timers ──────────────────────────────────────────────────────────────────
function tickTimers() {
  Object.values(pcs).forEach(c => {
    // Тикаем таймер если сессия активна (включая оффлайн-ПК с сессией — таймер идёт)
    if (!c.isSession || c.isPaused) return;
    c.elapsedSeconds++;
    const el = document.getElementById('timer-' + c.pcNumber.replace(/\s/g, '_'));
    if (el) el.textContent = fmtTime(c.elapsedSeconds);
  });
}

// ─── Session actions ─────────────────────────────────────────────────────────
let _ssType = 'Лимит'; // текущий тип в диалоге
let _ssSyncing = false; // защита от рекурсии при синхронизации полей

function openStartSession(pcNumber) {
  activePc = pcNumber;
  document.getElementById('dlgSsPc').textContent = pcNumber;
  document.getElementById('dlgSsReader').value = '';
  document.getElementById('dlgSsName').value = '';
  document.getElementById('dlgSsMinutes').value = '';
  document.getElementById('dlgSsMoney').value = '';
  document.getElementById('dlgSsHint').textContent = '';
  ssSelectType('Лимит');
  document.getElementById('dlgStartSession').style.display = 'flex';
}

function ssSelectType(type) {
  _ssType = type;
  const isLimit = type === 'Лимит';
  document.getElementById('dlgSsLimitFields').style.display = isLimit ? '' : 'none';
  document.getElementById('dlgSsVipInfo').style.display     = isLimit ? 'none' : '';
  // Стили кнопок
  document.getElementById('ssBtnLimited').classList.toggle('active', isLimit);
  document.getElementById('ssBtnVip').classList.toggle('active', !isLimit);
  if (!isLimit) {
    document.getElementById('dlgSsMinutes').value = '';
    document.getElementById('dlgSsMoney').value = '';
    document.getElementById('dlgSsHint').textContent = '';
  }
}

// Синхронизация минуты → деньги (как в WPF TxtMinutes_TextChanged)
function ssSyncMinutes() {
  if (_ssSyncing) return;
  _ssSyncing = true;
  try {
    const mins = parseFloat(document.getElementById('dlgSsMinutes').value);
    const t = GlobalSettings_Tariff();
    if (mins > 0) {
      const cost = Math.round((mins / 60) * t);
      document.getElementById('dlgSsMoney').value = cost;
      document.getElementById('dlgSsHint').textContent = `${mins} мин = ${cost.toLocaleString()} сум`;
    } else {
      document.getElementById('dlgSsMoney').value = '';
      document.getElementById('dlgSsHint').textContent = '';
    }
  } finally { _ssSyncing = false; }
}

// Синхронизация деньги → минуты (как в WPF TxtMoney_TextChanged)
function ssSyncMoney() {
  if (_ssSyncing) return;
  _ssSyncing = true;
  try {
    const money = parseFloat(document.getElementById('dlgSsMoney').value);
    const t = GlobalSettings_Tariff();
    if (money > 0) {
      const mins = Math.round((money / t) * 60);
      document.getElementById('dlgSsMinutes').value = mins;
      document.getElementById('dlgSsHint').textContent = `${money.toLocaleString()} сум = ${mins} мин`;
    } else {
      document.getElementById('dlgSsMinutes').value = '';
      document.getElementById('dlgSsHint').textContent = '';
    }
  } finally { _ssSyncing = false; }
}

async function confirmStartSession() {
  const reader = document.getElementById('dlgSsReader').value.trim();
  if (!reader) { toast('Введите ID читателя', 'warn'); return; }
  const name = document.getElementById('dlgSsName').value.trim();

  let limitSeconds = 0, paidAmount = 0;
  if (_ssType === 'Лимит') {
    const mins  = parseFloat(document.getElementById('dlgSsMinutes').value) || 0;
    const money = parseFloat(document.getElementById('dlgSsMoney').value)   || 0;
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

async function endSession(pcNumber) {
  await conn.invoke('EndSession', pcNumber);
}

async function togglePause(pcNumber) {
  await conn.invoke('TogglePause', pcNumber);
}

async function lock(pcNumber) {
  await conn.invoke('SendCommandToPc', pcNumber, 'REMOTE_LOCK', 'true');
}

async function unlock(pcNumber) {
  await conn.invoke('SendCommandToPc', pcNumber, 'REMOTE_LOCK', 'false');
}

async function lockAll() {
  await conn.invoke('SendCommandToAll', 'REMOTE_LOCK', 'true');
}

async function unlockAll() {
  await conn.invoke('SendCommandToAll', 'REMOTE_LOCK', 'false');
}

function showSummary(d) {
  const h = Math.floor(d.duration / 3600), m = Math.floor((d.duration % 3600) / 60), s = d.duration % 60;
  document.getElementById('dlgSummaryContent').innerHTML = `
    <b>ПК:</b> ${esc(d.pcNumber)}<br>
    <b>Тип:</b> ${d.sessionType}<br>
    <b>Длительность:</b> ${h}ч ${m}м ${s}с<br>
    <b>Заработано:</b> ${d.earned.toLocaleString()} сум<br>
    <b>Оплачено:</b> ${d.paidAmount.toLocaleString()} сум<br>
    <b>Возврат:</b> ${d.refund.toLocaleString()} сум
  `;
  document.getElementById('dlgSummary').style.display = 'flex';
  loadFinance();
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
  }
});

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
  const b64  = btoa(String.fromCharCode(...new Uint8Array(buf)));
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
  ['sessions', 'services', 'all'].forEach(t => {
    document.getElementById('tab' + t.charAt(0).toUpperCase() + t.slice(1)).classList.toggle('active', t === tab);
  });
  document.getElementById('finTypeFilter').style.display = tab === 'sessions' ? '' : 'none';
  document.getElementById('finStatusFilter').style.display = tab === 'services' ? '' : 'none';
  renderFinance();
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
  const today = new Date(); today.setHours(0,0,0,0);
  const weekStart = new Date(today); weekStart.setDate(today.getDate() - today.getDay() + 1);
  const monthStart = new Date(today.getFullYear(), today.getMonth(), 1);

  if (finTab === 'sessions' || finTab === 'all') {
    document.getElementById('statToday').textContent = fmt(finSessions.filter(s => new Date(s.endTime) >= today).reduce((a,s)=>a+s.earnedAmount,0));
    document.getElementById('statWeek').textContent  = fmt(finSessions.filter(s => new Date(s.endTime) >= weekStart).reduce((a,s)=>a+s.earnedAmount,0));
    document.getElementById('statMonth').textContent = fmt(finSessions.filter(s => new Date(s.endTime) >= monthStart).reduce((a,s)=>a+s.earnedAmount,0));
    document.getElementById('statCount').textContent = finSessions.length;
  } else {
    document.getElementById('statToday').textContent = fmt(finServices.filter(t => new Date(t.createdAt) >= today).reduce((a,t)=>a+t.totalAmount,0));
    document.getElementById('statWeek').textContent  = fmt(finServices.filter(t => new Date(t.createdAt) >= weekStart).reduce((a,t)=>a+t.totalAmount,0));
    document.getElementById('statMonth').textContent = fmt(finServices.filter(t => new Date(t.createdAt) >= monthStart).reduce((a,t)=>a+t.totalAmount,0));
    document.getElementById('statCount').textContent = finServices.length;
  }

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

  // Позиции и размеры шрифтов экрана блокировки
  document.getElementById('sPcNumberPosition').value  = settings.pcNumberPosition   ?? 'MiddleCenter';
  document.getElementById('sLockedTextPosition').value = settings.lockedTextPosition ?? 'MiddleCenter';
  document.getElementById('sTimePosition').value       = settings.timePosition       ?? 'BottomCenter';

  const pcFont     = settings.pcNumberFontSize   ?? 52;
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

function bindSliderLabel(sliderId, labelId, fmt) {
  const slider = document.getElementById(sliderId);
  const label  = document.getElementById(labelId);
  if (!slider || !label) return;
  // Удалить старый обработчик чтобы не дублировать
  slider.oninput = () => { label.textContent = fmt(parseFloat(slider.value)); };
}

function readSettingsForm() {
  // Прозрачность: слайдер 0..100 → сервер хранит 0..1
  const opacityPct = parseFloat(document.getElementById('sBgOpacity').value) || 30;
  return {
    tariff: parseInt(document.getElementById('sTariff').value) || 3000,
    adminPassword: document.getElementById('sAdminPassword').value,
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
    // Экран блокировки — позиции и размеры шрифтов
    pcNumberPosition:   document.getElementById('sPcNumberPosition').value,
    pcNumberFontSize:   parseInt(document.getElementById('sPcNumberFontSize').value) || 52,
    lockedTextPosition: document.getElementById('sLockedTextPosition').value,
    lockedTextFontSize: parseInt(document.getElementById('sLockedTextFontSize').value) || 16,
    timePosition:       document.getElementById('sTimePosition').value,
    timeFontSize:       parseInt(document.getElementById('sTimeFontSize').value) || 36,
    // Фон — имя файла берём из поля (uploadBgFile() обновляет его отдельно)
    backgroundFileName: document.getElementById('sBgFileName').value,
    services: readServicesForm(),
    // Сохраняем поля которые не редактируются на этой странице
    clientSortMode: settings.clientSortMode,
    operators: settings.operators,
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
  // SignalR/System.Text.Json deserialization requires byte[] as base64 string
  const b64 = btoa(String.fromCharCode(...new Uint8Array(buf)));
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
  const r = await fetch('/api/admin/operators', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ displayName: name, login, password: pwd })
  });
  if (!r.ok) { const d = await r.json(); toast(d.error || 'Ошибка', 'warn'); return; }
  document.getElementById('newOpName').value = '';
  document.getElementById('newOpLogin').value = '';
  document.getElementById('newOpPwd').value = '';
  const badge = document.getElementById('opSaved');
  badge.style.display = 'inline'; setTimeout(() => badge.style.display = 'none', 2000);
  await loadOperators();
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
async function logout() {
  await fetch('/api/admin/logout', { method: 'POST' });
  window.location.href = '/admin-login.html';
}

// ─── Dialog helpers ───────────────────────────────────────────────────────────
function closeDlg(id) {
  document.getElementById(id).style.display = 'none';
}

// Close on overlay click
document.addEventListener('click', e => {
  if (e.target.classList.contains('dlg-overlay')) e.target.style.display = 'none';
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
function periodFrom(p) {
  const t = new Date(); t.setHours(0,0,0,0);
  if (p === 'today') return t;
  if (p === 'week')  { const w = new Date(t); w.setDate(t.getDate() - t.getDay() + 1); return w; }
  if (p === 'month') return new Date(t.getFullYear(), t.getMonth(), 1);
  return null;
}
