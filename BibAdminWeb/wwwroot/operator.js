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
let latestClientVersion = '';
let _svcRows = [];

// ── Просмотр экрана ───────────────────────────────────────────────────────────
let _screenPc = null;
let _screenInterval = null;

// ── Инициализация ─────────────────────────────────────────────────────────────
(async function init() {
  // Проверяем авторизацию
  const me = await fetch('/api/op/me').then(r => r.ok ? r.json() : null).catch(() => null);
  if (!me) { window.location.href = '/login.html'; return; }
  document.getElementById('opName').textContent = me.displayName;

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
  connection.on('readerCardPrefix', p => { readerCardPrefix = p || 'FAA'; });

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

  connection.on('serverRestarting', data => {
    showRestartOverlay(data.reason || 'Обновление системы');
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
    const keys = Object.keys(pcs).sort((a, b) => pcs[a].pcNumberValue - pcs[b].pcNumberValue);
    const idx = keys.indexOf(pcNumber);
    const cards = grid.querySelectorAll('.pc-card');
    if (idx >= cards.length) grid.appendChild(card);
    else grid.insertBefore(card, cards[idx]);
  }

  const statusClass = getStatusClass(pc);
  const isSelected = selectedPc === pcNumber;
  card.className = 'pc-card ' + statusClass + (isSelected ? ' selected' : '');
  card.innerHTML = buildCardHtml(pc);
}

function buildCardHtml(pc) {
  const pcNumber = pc.pcNumber;

  // Badge (тип сессии)
  let badge = '';
  if (pc.isSession) {
    const cls = pc.sessionType === 'VIP' ? 'badge-vip' : 'badge-limit';
    badge = `<span class="pc-session-badge ${cls}">${esc(pc.sessionType)}</span>`;
  }

  // Таймер
  let timerBlock = '';
  if (pc.isSession || pc.isPaused) {
    timerBlock = `<div class="pc-timer" data-pc-timer="${esc(pcNumber)}">${fmtTime(pc.elapsedSeconds)}</div>`;
    // VIP: стоимость
    if (pc.sessionType === 'VIP') {
      const cost = Math.floor(pc.elapsedSeconds * tariff / 3600);
      timerBlock += `<div class="pc-cost" data-pc-cost="${esc(pcNumber)}">К оплате: ${cost.toLocaleString('ru-RU')} сум</div>`;
    }
    // Лимит: остаток
    if (pc.sessionType === 'Лимит' && pc.limitSeconds > 0) {
      const rem = Math.max(0, pc.limitSeconds - pc.elapsedSeconds);
      const remCls = rem <= 300 ? 'pc-remaining urgent' : 'pc-remaining';
      timerBlock += `<div class="${remCls}" data-pc-rem="${esc(pcNumber)}">Осталось: ${fmtTime(rem)}</div>`;
    }
  } else {
    timerBlock = `<div class="pc-timer" data-pc-timer="${esc(pcNumber)}">—</div>`;
  }

  // Мета-информация
  const metaParts = [];
  if (pc.ip) metaParts.push(`<span>${esc(pc.ip)}</span>`);
  if (pc.isSession && pc.userName) metaParts.push(`<span>👤 ${esc(pc.userName)}</span>`);
  if (pc.isSession && pc.readerId) metaParts.push(`<span>🪪 ${esc(pc.readerId)}</span>`);
  if (pc.isSession && pc.paidAmount) metaParts.push(`<span>💵 ${pc.paidAmount.toLocaleString('ru-RU')} сум</span>`);
  if (pc.clientVersion) {
    const isOld = latestClientVersion && pc.clientVersion !== latestClientVersion;
    metaParts.push(`<span class="pc-ver${isOld ? ' pc-ver-old' : ''}" title="${isOld ? `Доступно обновление v${latestClientVersion}` : 'Версия BibClient'}">${isOld ? '⬆ ' : ''}v${pc.clientVersion}</span>`);
  }
  const meta = metaParts.length ? `<div class="pc-meta-op">${metaParts.join('')}</div>` : '';

  const statusLabel = getStatusLabel(pc);

  return `
    <div class="pc-name">${esc(pcNumber)}</div>
    ${badge}
    ${timerBlock}
    <div class="pc-status-label">${statusLabel}</div>
    ${meta}
  `;
}

// ── Тики таймеров (локальный инкремент) ───────────────────────────────────────
function tickTimers() {
  Object.values(pcs).forEach(pc => {
    if (!pc.isSession || pc.isPaused || !pc.isOnline) return;
    pc.elapsedSeconds += 1;
    const timerEl = document.querySelector(`[data-pc-timer="${CSS.escape(pc.pcNumber)}"]`);
    if (timerEl) timerEl.textContent = fmtTime(pc.elapsedSeconds);
    if (pc.sessionType === 'VIP') {
      const costEl = document.querySelector(`[data-pc-cost="${CSS.escape(pc.pcNumber)}"]`);
      if (costEl) {
        const cost = Math.floor(pc.elapsedSeconds * tariff / 3600);
        costEl.textContent = `К оплате: ${cost.toLocaleString('ru-RU')} сум`;
      }
    }
    if (pc.sessionType === 'Лимит' && pc.limitSeconds > 0) {
      const remEl = document.querySelector(`[data-pc-rem="${CSS.escape(pc.pcNumber)}"]`);
      if (remEl) {
        const rem = Math.max(0, pc.limitSeconds - pc.elapsedSeconds);
        remEl.textContent = `Осталось: ${fmtTime(rem)}`;
        remEl.className = rem <= 300 ? 'pc-remaining urgent' : 'pc-remaining';
      }
    }
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

  if (pc.isOnline) {
    btns.push(`<button class="ab-btn" onclick="openScreenView('${esc(pc.pcNumber)}')" style="background:#374151;border-color:#4b5563">👁 Экран</button>`);
  }
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
function parseRegDate(dateStr) {
  if (!dateStr) return null;
  const p = dateStr.split('-');
  if (p.length !== 3) return null;
  const d = new Date(+p[2], +p[1] - 1, +p[0]);
  return isNaN(d) ? null : d;
}

function onCardTypeChanged() {
  const isTemp = document.querySelector('[name="cardType"]:checked')?.value === 'temp';
  const prefix = document.getElementById('dlgReaderPrefix');
  if (prefix) prefix.textContent = isTemp ? '№' : readerCardPrefix;
  // Для временных билетов поле «Имя» скрываем — имён в базе нет
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
  document.getElementById('dlgLimitMin').value = 60;
  document.getElementById('dlgAmount').value = tariff;
  document.getElementById('dlgUserName').value = '';
  document.getElementById('dlgReaderId').value = '';
  document.getElementById('dlgReaderId').placeholder = '260500456';
  document.getElementById('dlgReaderPrefix').textContent = readerCardPrefix;
  const infoEl = document.getElementById('dlgReaderInfo');
  infoEl.style.display = 'none';
  infoEl.textContent = '';
  // Reset card type to regular
  const regularRadio = document.querySelector('[name="cardType"][value="regular"]');
  if (regularRadio) regularRadio.checked = true;
  _readerLookupState = null;
  _readerLookedUpId = '';
  document.querySelectorAll('[name="stype"]')[0].checked = true;
  document.getElementById('limitFields').style.display = '';

  // Показываем/скрываем имя согласно настройкам
  const reqName = !!sessionFields.requireUserName;
  const rowName = document.getElementById('rowUserName');
  if (rowName) rowName.style.display = reqName ? '' : 'none';
  const lblName = document.getElementById('lblUserName');
  if (lblName) lblName.innerHTML = reqName ? 'Имя *' : 'Имя читателя <span class="opt">(заполняется автоматически)</span>';

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
  const limitMin    = parseInt(document.getElementById('dlgLimitMin').value) || 0;
  const paidAmount  = parseInt(document.getElementById('dlgAmount').value) || 0;
  const userName    = document.getElementById('dlgUserName').value.trim();
  const isTemp     = document.querySelector('[name="cardType"]:checked')?.value === 'temp';
  const readerNums = document.getElementById('dlgReaderId').value.trim();

  if (!readerNums) { toast('Введите номер читательского билета', 'warn'); return; }

  const readerId = isTemp ? readerNums : (readerCardPrefix + readerNums);

  if (isTemp) {
    // Временные билеты не ищутся в базе — просто фиксируем посещение
  } else {
    // Для постоянных — обязательна проверка в базе
    if (_readerLookupState === null || _readerLookedUpId !== readerId) {
      await lookupReader();
    }
    if (_readerLookupState === 'not_found') { toast('Читатель не найден в базе', 'warn'); return; }
    if (_readerLookupState === 'expired')   { toast('Читательский билет просрочен', 'warn'); return; }
    if (_readerLookupState !== 'valid')     { toast('Проверьте номер читательского билета', 'warn'); return; }
  }

  if (!!sessionFields.requireUserName && !userName) { toast('Введите имя пользователя', 'warn'); return; }

  closeDlg('dlgSession');
  try {
    await connection.invoke('StartSession', selectedPc, sessionType,
      sessionType === 'Лимит' ? limitMin * 60 : 0,
      sessionType === 'Лимит' ? paidAmount : 0,
      userName, readerId);
  } catch (e) { toast('Ошибка: ' + e, 'warn'); }
}

async function lookupReader() {
  const nums   = document.getElementById('dlgReaderId').value.trim();
  const infoEl = document.getElementById('dlgReaderInfo');
  if (!nums) { infoEl.style.display = 'none'; _readerLookupState = null; return; }

  const isTemp = document.querySelector('[name="cardType"]:checked')?.value === 'temp';

  if (isTemp) {
    // Временный билет — без поиска в базе, просто подтверждаем номер
    _readerLookupState = 'valid';
    _readerLookedUpId = nums;
    infoEl.style.cssText = 'display:block;margin-top:6px;padding:7px 10px;border-radius:6px;font-size:12px;background:#1A1A2E;color:#AAAACC;border:1px solid #3D3D6B';
    infoEl.textContent = `✓ Временный билет №${nums} — посещение будет зафиксировано`;
    return;
  }

  const cardId = readerCardPrefix + nums;
  _readerLookedUpId = cardId;

  try {
    const r = await fetch(`/api/readers/lookup/${encodeURIComponent(cardId)}`);
    if (!r.ok) {
      _readerLookupState = 'not_found';
      infoEl.style.cssText = 'display:block;margin-top:6px;padding:7px 10px;border-radius:6px;font-size:12px;background:#2D1A1A;color:#F87171;border:1px solid #5D2A2A';
      infoEl.textContent = `✗ Читатель ${cardId} не найден в базе`;
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
        const daysSince = (Date.now() - regDate) / 86400000;
        if (daysSince > 3 * 365 + 1) {
          const expDate = new Date(regDate);
          expDate.setFullYear(expDate.getFullYear() + 3);
          expired = true;
          expiredMsg = `⚠ ${data.fullName} · Билет просрочен с ${expDate.toLocaleDateString('ru-RU')}`;
        }
      }
      if (expired) {
        _readerLookupState = 'expired';
        document.getElementById('dlgUserName').value = data.fullName || '';
        infoEl.style.cssText = 'display:block;margin-top:6px;padding:7px 10px;border-radius:6px;font-size:12px;background:#2D1A1A;color:#F87171;border:1px solid #5D2A2A';
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
    return `<div style="display:flex;gap:8px;align-items:center;margin-bottom:8px">
      <select style="flex:1;padding:7px 10px;border:1px solid #3D3D6B;border-radius:8px;background:#1A1A2E;color:#fff;font-size:13px"
        onchange="onSvcRowTypeChange(${row.id}, this.value)">${opts}</select>
      <input type="number" min="1" max="999" value="${row.qty}"
        style="width:70px;padding:7px 10px;border:1px solid #3D3D6B;border-radius:8px;background:#1A1A2E;color:#fff;font-size:13px;text-align:center"
        oninput="onSvcRowQtyChange(${row.id}, this.value)">
      ${canRemove
        ? `<button onclick="removeSvcRow(${row.id})" style="padding:6px 10px;background:#2D1A1A;color:#F87171;border:1px solid #5D2A2A;border-radius:6px;cursor:pointer;font-size:14px;line-height:1">✕</button>`
        : '<div style="width:34px"></div>'}
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
