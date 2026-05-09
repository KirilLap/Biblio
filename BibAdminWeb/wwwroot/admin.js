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
  const list = Object.values(pcs).sort((a, b) => a.pcNumberValue - b.pcNumberValue);

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
  div.id = 'pc-' + c.pcNumber.replace(/\s/g, '_');

  const dotClass = !c.isOnline ? '' : c.isSession && c.isPaused ? 'paused' : c.isSession ? 'session' : c.isFree ? 'free' : 'online';
  const badge = statusBadge(c);

  let timer = '';
  if (c.isSession) {
    timer = `<div class="pc-timer ${c.sessionType === 'VIP' ? 'vip-timer' : ''} ${c.isPaused ? 'paused-timer' : ''}" id="timer-${esc(c.pcNumber)}">
      ${fmtTime(c.elapsedSeconds)}
    </div>`;
    if (c.limitSeconds > 0) {
      const rem = Math.max(0, c.limitSeconds - c.elapsedSeconds);
      timer += `<div class="pc-meta"><span>Осталось: ${fmtTime(rem)}</span></div>`;
    }
  }

  const meta = `<div class="pc-meta">
    ${c.ip ? `<span>${c.ip}</span>` : ''}
    ${c.isSession && c.userName ? `<span>👤 ${c.userName}</span>` : ''}
    ${c.isSession && c.readerId ? `<span>🪪 ${c.readerId}</span>` : ''}
    ${c.isSession && c.paidAmount ? `<span>💵 ${c.paidAmount.toLocaleString()} сум</span>` : ''}
  </div>`;

  const actions = buildActions(c);

  div.innerHTML = `
    <div class="pc-card-header">
      <span class="pc-name" onclick="openRename(${c.pcNumberValue}, '${esc(c.customName)}')">${esc(c.pcNumber)}</span>
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
    if (!c.isSession || c.isPaused) return;
    c.elapsedSeconds++;
    const el = document.getElementById('timer-' + c.pcNumber.replace(/\s/g, '_'));
    if (el) el.textContent = fmtTime(c.elapsedSeconds);
  });
}

// ─── Session actions ─────────────────────────────────────────────────────────
function openStartSession(pcNumber) {
  activePc = pcNumber;
  document.getElementById('dlgSsPc').textContent = pcNumber;
  document.getElementById('dlgSsType').value = 'VIP';
  document.getElementById('dlgSsLimitRow').style.display = 'none';
  document.getElementById('dlgSsPaidRow').style.display = 'none';
  document.getElementById('dlgSsReader').value = '';
  document.getElementById('dlgSsName').value = '';
  document.getElementById('dlgSsType').onchange = function() {
    const isLimit = this.value === 'Лимит';
    document.getElementById('dlgSsLimitRow').style.display = isLimit ? '' : 'none';
    document.getElementById('dlgSsPaidRow').style.display = isLimit ? '' : 'none';
  };
  document.getElementById('dlgStartSession').style.display = 'flex';
}

async function confirmStartSession() {
  const type = document.getElementById('dlgSsType').value;
  const limitType = document.getElementById('dlgSsLimitType').value;
  const limitVal = parseInt(document.getElementById('dlgSsLimitVal').value) || 0;
  const paid = parseInt(document.getElementById('dlgSsPaid').value) || 0;
  const reader = document.getElementById('dlgSsReader').value.trim();
  const name = document.getElementById('dlgSsName').value.trim();

  let limitSeconds = 0, paidAmount = 0;
  if (type === 'Лимит') {
    if (limitType === 'time') limitSeconds = limitVal * 60;
    else { paidAmount = limitVal; const t = GlobalSettings_Tariff(); limitSeconds = Math.floor(limitVal / t * 3600); }
    paidAmount = paid || paidAmount;
  }
  closeDlg('dlgStartSession');
  await conn.invoke('StartSession', activePc, type, limitSeconds, paidAmount, name, reader);
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
  document.getElementById('sShowPcName').checked = !!settings.showPcName;
  document.getElementById('sShowPcNumber').checked = !!settings.showPcNumber;
  document.getElementById('sShowLockedText').checked = !!settings.showLockedText;
  const opacity = settings.backgroundOpacity ?? 0.3;
  document.getElementById('sBgOpacity').value = opacity;
  document.getElementById('sBgOpacityVal').textContent = opacity.toFixed(2);
  document.getElementById('sBgOpacity').oninput = function() {
    document.getElementById('sBgOpacityVal').textContent = parseFloat(this.value).toFixed(2);
  };
  renderServicesList();
}

function readSettingsForm() {
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
    backgroundOpacity: parseFloat(document.getElementById('sBgOpacity').value),
    services: readServicesForm(),
    // keep untouched fields from original
    pcNumberPosition: settings.pcNumberPosition,
    pcNumberFontSize: settings.pcNumberFontSize,
    lockedTextPosition: settings.lockedTextPosition,
    lockedTextFontSize: settings.lockedTextFontSize,
    timePosition: settings.timePosition,
    timeFontSize: settings.timeFontSize,
    backgroundFileName: settings.backgroundFileName,
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
function detectLocalIp() {
  fetch('/api/admin/check').then(() => {
    document.getElementById('opWebUrl').textContent =
      `http://${location.hostname}:${location.port || 8080}/login.html`;
  });
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
