'use strict';

// ── Состояние ────────────────────────────────────────────────────────────────
let pcs = {};            // pcNumber → объект состояния
let selectedPc = null;   // текущий выбранный pcNumber
let tariff = 3000;
let serviceTypes = [];
let offlinePcNumber = null;  // ПК, по которому ждём решения оффлайн
let connection = null;

// ── Инициализация ─────────────────────────────────────────────────────────────
(async function init() {
  // Проверяем авторизацию
  const me = await fetch('/api/op/me').then(r => r.ok ? r.json() : null).catch(() => null);
  if (!me) { window.location.href = '/login.html'; return; }
  document.getElementById('opName').textContent = me.displayName;

  startSignalR();

  // Обновляем таймеры каждую секунду локально
  setInterval(tickTimers, 1000);
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

  connection.on('offlineAlert', data => {
    offlinePcNumber = data.pcNumber;
    const pc = pcs[data.pcNumber] || {};
    document.getElementById('dlgOfflineBody').innerHTML =
      `<div class="summary-row"><span>ПК</span><span class="val">${esc(data.pcNumber)}</span></div>
       <div class="summary-row"><span>Тип</span><span class="val">${esc(data.sessionType)}</span></div>
       <div class="summary-row"><span>Время в сессии</span><span class="val">${fmtTime(data.elapsed)}</span></div>`;
    openDlg('dlgOffline');
  });

  connection.on('offlineResolved', data => {
    if (offlinePcNumber === data.pcNumber) {
      offlinePcNumber = null;
      closeDlg('dlgOffline');
      toast(`Решение по ${data.pcNumber}: ${data.decision === 'Pause' ? 'пауза' : 'продолжить'}`, 'good');
    }
  });

  connection.on('sessionSummary', s => {
    showSessionSummary(s);
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
    toast('Соединение потеряно. Ожидание сервера...', 'warn');
  });

  connection.start()
    .then(() => setDot(true))
    .catch(err => { setDot(false); console.error('SignalR error:', err); });
}

function setDot(online) {
  const d = document.getElementById('connDot');
  d.className = 'conn-dot ' + (online ? 'online' : 'offline');
  d.title = online ? 'Подключено' : 'Нет связи с сервером';
}

// ── Рендер грида ──────────────────────────────────────────────────────────────
function renderGrid() {
  const grid = document.getElementById('grid');
  const keys = Object.keys(pcs).sort((a, b) => {
    const na = pcs[a].pcNumberValue, nb = pcs[b].pcNumberValue;
    return na - nb;
  });

  // Удаляем исчезнувшие карточки
  grid.querySelectorAll('.pc-card').forEach(el => {
    if (!pcs[el.dataset.pc]) el.remove();
  });

  keys.forEach(pcNumber => renderCard(pcNumber));
}

function renderCard(pcNumber) {
  const pc = pcs[pcNumber];
  if (!pc) return;

  const grid = document.getElementById('grid');
  let card = grid.querySelector(`[data-pc="${CSS.escape(pcNumber)}"]`);
  if (!card) {
    card = document.createElement('div');
    card.className = 'pc-card';
    card.dataset.pc = pcNumber;
    card.addEventListener('click', () => selectPc(pcNumber));
    // Вставляем в правильном порядке по pcNumberValue
    const keys = Object.keys(pcs).sort((a, b) => pcs[a].pcNumberValue - pcs[b].pcNumberValue);
    const idx = keys.indexOf(pcNumber);
    const cards = grid.querySelectorAll('.pc-card');
    if (idx >= cards.length) grid.appendChild(card);
    else grid.insertBefore(card, cards[idx]);
  }

  const statusClass = getStatusClass(pc);
  const isSelected = selectedPc === pcNumber;
  card.className = 'pc-card ' + statusClass + (isSelected ? ' selected' : '');

  const timer = getDisplayTime(pc);
  const statusLabel = getStatusLabel(pc);
  const userLine = pc.userName ? `<div class="pc-user">${esc(pc.userName)}</div>` : '';

  card.innerHTML = `
    <div class="pc-name">${esc(pcNumber)}</div>
    <div class="pc-timer" data-pc-timer="${esc(pcNumber)}">${timer}</div>
    <div class="pc-status-label">${statusLabel}</div>
    ${userLine}
  `;
}

// ── Тики таймеров (локальный инкремент) ───────────────────────────────────────
function tickTimers() {
  Object.values(pcs).forEach(pc => {
    if (!pc.isSession || pc.isPaused || !pc.isOnline) return;
    pc.elapsedSeconds += 1;
    const el = document.querySelector(`[data-pc-timer="${CSS.escape(pc.pcNumber)}"]`);
    if (el) el.textContent = getDisplayTime(pc);
  });
}

// ── Статистика ────────────────────────────────────────────────────────────────
function updateStats() {
  const vals = Object.values(pcs);
  document.getElementById('stTotal').textContent    = vals.length;
  document.getElementById('stOnline').textContent   = vals.filter(p => p.isOnline).length;
  document.getElementById('stSessions').textContent = vals.filter(p => p.isSession).length;
  document.getElementById('stLocked').textContent   = vals.filter(p => p.isOnline && p.isLocked).length;
}

// ── Выбор ПК ──────────────────────────────────────────────────────────────────
function selectPc(pcNumber) {
  if (selectedPc === pcNumber) {
    selectedPc = null;
    document.querySelectorAll('.pc-card.selected').forEach(c => c.classList.remove('selected'));
    document.getElementById('actionBar').classList.add('hidden');
    return;
  }
  selectedPc = pcNumber;
  document.querySelectorAll('.pc-card.selected').forEach(c => c.classList.remove('selected'));
  const card = document.querySelector(`[data-pc="${CSS.escape(pcNumber)}"]`);
  if (card) card.classList.add('selected');
  renderActionBar();
}

function renderActionBar() {
  const ab = document.getElementById('actionBar');
  const pc = pcs[selectedPc];
  if (!pc) { ab.classList.add('hidden'); return; }

  document.getElementById('abPcName').textContent = pc.pcNumber;
  document.getElementById('abStatus').textContent = getStatusLabel(pc);

  const btns = [];

  if (pc.isOnline && pc.isLocked && !pc.isSession) {
    btns.push(`<button class="ab-btn green" onclick="openSessionDlg()">▶ Начать сессию</button>`);
  }
  if (pc.isSession) {
    const pauseLabel = pc.isPaused ? '▶ Продолжить' : '⏸ Пауза';
    const pauseCls = pc.isPaused ? 'green' : 'amber';
    btns.push(`<button class="ab-btn ${pauseCls}" onclick="doTogglePause()">${pauseLabel}</button>`);
    btns.push(`<button class="ab-btn blue" onclick="openTransferDlg()">↔ Пересадить</button>`);
    if (pc.sessionType === 'Лимит')
      btns.push(`<button class="ab-btn blue" onclick="openExtendDlg()">+⏱ Время</button>`);
    btns.push(`<button class="ab-btn red" onclick="doEndSession()">⏹ Завершить</button>`);
  }

  document.getElementById('abActions').innerHTML = btns.join('');
  ab.classList.remove('hidden');
}

// ── Действия ──────────────────────────────────────────────────────────────────
function openSessionDlg() {
  if (!selectedPc) return;
  document.getElementById('dlgSessionPc').textContent = selectedPc;
  document.getElementById('dlgLimitMin').value = 60;
  document.getElementById('dlgAmount').value = tariff;
  document.getElementById('dlgUserName').value = '';
  document.getElementById('dlgReaderId').value = '';
  document.querySelectorAll('[name="stype"]')[0].checked = true;
  document.getElementById('limitFields').style.display = '';
  openDlg('dlgSession');

  document.querySelectorAll('[name="stype"]').forEach(r => {
    r.onchange = () => {
      document.getElementById('limitFields').style.display =
        r.value === 'Лимит' && r.checked ? '' : (r.value === 'Лимит' ? 'none' : '');
      if (document.querySelector('[name="stype"]:checked').value !== 'Лимит')
        document.getElementById('limitFields').style.display = 'none';
      else
        document.getElementById('limitFields').style.display = '';
    };
  });
}

function calcAmount() {
  const mins = parseInt(document.getElementById('dlgLimitMin').value) || 0;
  document.getElementById('dlgAmount').value = Math.round(tariff * mins / 60);
}
function calcTime() {
  const amount = parseInt(document.getElementById('dlgAmount').value) || 0;
  document.getElementById('dlgLimitMin').value = Math.round(amount / tariff * 60);
}

async function confirmStartSession() {
  const sessionType = document.querySelector('[name="stype"]:checked')?.value || 'Лимит';
  const limitMin = parseInt(document.getElementById('dlgLimitMin').value) || 0;
  const paidAmount = parseInt(document.getElementById('dlgAmount').value) || 0;
  const userName = document.getElementById('dlgUserName').value.trim();
  const readerId = document.getElementById('dlgReaderId').value.trim();
  closeDlg('dlgSession');
  try {
    await connection.invoke('StartSession', selectedPc, sessionType,
      sessionType === 'Лимит' ? limitMin * 60 : 0,
      sessionType === 'Лимит' ? paidAmount : 0,
      userName, readerId);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

async function doEndSession() {
  if (!selectedPc) return;
  try {
    await connection.invoke('EndSession', selectedPc);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
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
  document.getElementById('dlgExtMin').value = 30;
  document.getElementById('dlgExtAmount').value = tariff ? Math.round(tariff * 30 / 60) : 0;
  openDlg('dlgExtend');
}

function calcExtAmount() {
  if (_extSyncing || !tariff) return;
  _extSyncing = true;
  const min = parseInt(document.getElementById('dlgExtMin').value) || 0;
  document.getElementById('dlgExtAmount').value = Math.round(tariff * min / 60);
  _extSyncing = false;
}

function calcExtTime() {
  if (_extSyncing || !tariff) return;
  _extSyncing = true;
  const amount = parseInt(document.getElementById('dlgExtAmount').value) || 0;
  document.getElementById('dlgExtMin').value = Math.round(amount * 60 / tariff) || 0;
  _extSyncing = false;
}

async function confirmExtend() {
  const min = parseInt(document.getElementById('dlgExtMin').value) || 0;
  const amount = parseInt(document.getElementById('dlgExtAmount').value) || 0;
  if (min <= 0) { toast('Укажите время', 'warn'); return; }
  closeDlg('dlgExtend');
  try {
    await connection.invoke('ExtendSession', selectedPc, min * 60, amount);
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
  const sel = document.getElementById('dlgTransferTarget');
  sel.innerHTML = targets
    .sort((a, b) => a.pcNumberValue - b.pcNumberValue)
    .map(t => `<option value="${esc(t.pcNumber)}">${esc(t.pcNumber)}</option>`)
    .join('');
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
      selectedPc = null;
      document.getElementById('actionBar').classList.add('hidden');
    } else {
      errEl.textContent = result;
      errEl.style.display = 'block';
    }
  } catch (e) { errEl.textContent = String(e); errEl.style.display = 'block'; }
}

function openServiceDlg() {
  if (serviceTypes.length === 0) { toast('Нет доступных услуг', 'warn'); return; }
  const sel = document.getElementById('dlgSvcType');
  sel.innerHTML = serviceTypes.map(s =>
    `<option value="${esc(s.id)}" data-price="${s.price}" data-unit="${esc(s.unit)}">${esc(s.name)} — ${fmt(s.price)} сум/${esc(s.unit)}</option>`
  ).join('');
  document.getElementById('dlgSvcQty').value = 1;
  document.getElementById('dlgSvcReader').value = '';
  document.querySelectorAll('[name="svcPay"]')[0].checked = true;
  updateSvcTotal();
  openDlg('dlgService');
}

function updateSvcTotal() {
  const sel = document.getElementById('dlgSvcType');
  const opt = sel.options[sel.selectedIndex];
  if (!opt) return;
  const price = parseInt(opt.dataset.price) || 0;
  const qty = parseInt(document.getElementById('dlgSvcQty').value) || 1;
  document.getElementById('dlgSvcTotal').textContent = fmt(price * qty) + ' сум';
}

async function confirmService() {
  const sel = document.getElementById('dlgSvcType');
  const id = sel.value;
  const qty = parseInt(document.getElementById('dlgSvcQty').value) || 1;
  const reader = document.getElementById('dlgSvcReader').value.trim();
  const payNow = document.querySelector('[name="svcPay"]:checked')?.value === 'now';
  closeDlg('dlgService');
  try {
    await connection.invoke('CreateService', id, qty, reader, reader, payNow);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

function showSessionSummary(s) {
  let html = `
    <div class="summary-row"><span>ПК</span><span class="val">${esc(s.pcNumber)}</span></div>
    <div class="summary-row"><span>Тип</span><span class="val">${esc(s.sessionType)}</span></div>
    <div class="summary-row"><span>Время</span><span class="val">${fmtTime(s.duration)}</span></div>
    <div class="summary-row"><span>Оплачено</span><span class="val">${fmt(s.paidAmount)} сум</span></div>
    <div class="summary-row"><span>Начислено</span><span class="val">${fmt(s.earned)} сум</span></div>`;
  if (s.refund > 0)
    html += `<div class="refund-highlight">💵 Возврат: ${fmt(s.refund)} сум</div>`;
  document.getElementById('dlgSummaryBody').innerHTML = html;
  openDlg('dlgSummary');
}

async function doLogout() {
  await fetch('/api/op/logout', { method: 'POST' }).catch(() => {});
  window.location.href = '/login.html';
}

// ── Диалоги ───────────────────────────────────────────────────────────────────
function openDlg(id) {
  document.getElementById(id).classList.add('open');
}
function closeDlg(id) {
  document.getElementById(id).classList.remove('open');
}
function closeDlgIfOverlay(e, id) {
  if (e.target === document.getElementById(id)) closeDlg(id);
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
