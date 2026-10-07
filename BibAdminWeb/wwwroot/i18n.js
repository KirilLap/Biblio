'use strict';

// ══════════════════════════════════════════════════════════════════════════════
// Языки интерфейса оператора: RU (исходный) / UZ
//
// Если перевода нет — показывается русский текст.
//
// ВАЖНО: переводится только отображение. Значения «Лимит», «VIP», «Пауза» и т.п.,
// которыми обмениваются сервер, клиентские ПК и база, остаются русскими.
// ══════════════════════════════════════════════════════════════════════════════

// Словарь I18N_UZ лежит в i18n-uz.js (подключается перед этим файлом).
// Правки из админки (Настройки → Переводы) приходят отдельным скриптом /api/i18n/overrides.js:
// узбекские ложатся поверх словаря, русские заменяют текст, написанный в коде.
const I18N_RU = {};
if (typeof window !== 'undefined') {
  for (const [dict, src] of [[I18N_RU, window.I18N_RU_OVERRIDES], [I18N_UZ, window.I18N_UZ_OVERRIDES]]) {
    for (const [k, v] of Object.entries(src || {})) {
      if (typeof v === 'string' && v.trim()) dict[k] = v;
    }
  }
}

function _i18nLookup(s) {
  const has = (d, k) => Object.prototype.hasOwnProperty.call(d, k);
  if (_lang === 'uz' && has(I18N_UZ, s)) return I18N_UZ[s];
  return has(I18N_RU, s) ? I18N_RU[s] : s;
}

// Строки с сервера, в которые уже подставлены значения
const I18N_UZ_PATTERNS = [
  [/^ПК (.+) не найден$/, '{1} kompyuteri topilmadi'],
  [/^Временный №(.+)$/, 'Vaqtinchalik №{1}'],
  [/^Незарег\. (.+)$/, 'Ro‘yxatsiz {1}'],
  [/^(\d+) услуги$/, '{1} ta xizmat'],
];

// Единицы услуг: «лист» → «varaq», «50 мб» → «50 MB»
const I18N_UZ_UNITS = {
  'лист': 'varaq', 'листов': 'varaq', 'страница': 'bet', 'стр': 'bet', 'штука': 'dona', 'шт': 'dona',
  'мин': 'daq', 'час': 'soat', 'мб': 'MB', 'гб': 'GB',
};

const I18N_UZ_MONTHS = {
  'янв': 'yanvar', 'фев': 'fevral', 'мар': 'mart', 'апр': 'aprel', 'май': 'may', 'мая': 'may',
  'июн': 'iyun', 'июл': 'iyul', 'авг': 'avgust', 'сен': 'sentabr', 'окт': 'oktabr', 'ноя': 'noyabr', 'дек': 'dekabr',
};

const I18N_LANGS = ['ru', 'uz'];
let _lang = 'ru';
try {
  const saved = localStorage.getItem('bibLang');
  if (I18N_LANGS.includes(saved)) _lang = saved;
} catch (e) { /* localStorage недоступен — остаёмся на русском */ }

function getLang() { return _lang; }

// Перевод строки. vars — подстановки для {имя}.
function t(s, vars) {
  let r = typeof s === 'string' ? _i18nLookup(s) : s;
  if (vars && typeof r === 'string') r = r.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? vars[k] : m));
  return r;
}

// Перевод строки, пришедшей с сервера (ошибки, подписи в статистике)
function tServer(s) {
  if (typeof s !== 'string') return s;
  const r = _i18nLookup(s);
  if (r !== s || _lang !== 'uz') return r;
  for (const [re, tpl] of I18N_UZ_PATTERNS) {
    const m = s.match(re);
    if (m) return tpl.replace(/\{(\d)\}/g, (x, i) => m[+i]);
  }
  return s;
}

// Готовые переводы названий услуг — используются, пока в админке
// (Настройки → Услуги, поле «Название (UZ)») своё название не задано.
const I18N_UZ_SERVICES = {
  'Ксерокопия А4 (односторонняя)': 'Kseronusxa A4 (bir tomonlama)',
  'Ксерокопия А4 (двухсторонняя)': 'Kseronusxa A4 (ikki tomonlama)',
  'Ч/Б Распечатка А4 (односторонняя)': 'Oq-qora chop etish A4 (bir tomonlama)',
  'Ч/Б Распечатка А4 (двухсторонняя)': 'Oq-qora chop etish A4 (ikki tomonlama)',
  'Цветная Распечатка А4 (односторонняя)': 'Rangli chop etish A4 (bir tomonlama)',
  'Цветная Распечатка А4 (двухсторонняя)': 'Rangli chop etish A4 (ikki tomonlama)',
  'Цветная Распечатка Глянцевая А4 (односторонняя)': 'Rangli chop etish, yaltiroq qog‘oz A4 (bir tomonlama)',
  'Цветная Распечатка Глянцевая А4 (двухсторонняя)': 'Rangli chop etish, yaltiroq qog‘oz A4 (ikki tomonlama)',
  'Сканирование А4': 'Skanerlash A4',
  'Сканирование А4 + Ч/Б печать': 'Skanerlash A4 + oq-qora chop etish',
  'Сканирование А4 + цветная печать': 'Skanerlash A4 + rangli chop etish',
  'Переплет до 100 листов (пластиковая пружина)': 'Muqovalash, 100 varaqgacha (plastik prujina)',
  'Переплет больше 100 листов (пластиковая пружина)': 'Muqovalash, 100 varaqdan ortiq (plastik prujina)',
  'Копирование данных на USB': 'Ma’lumotlarni USB ga nusxalash',
  'Ламинация': 'Laminatsiya',
};

// Название услуги на выбранном языке: сначала «Название (UZ)» из админки,
// затем готовый перевод из списка выше, иначе русское.
function svcName(name) {
  if (_lang !== 'uz' || typeof name !== 'string') return name;
  const s = typeof serviceTypes !== 'undefined' && serviceTypes.find(x => x.name === name);
  if (s && s.nameUz) return s.nameUz;
  const key = name.trim();
  return Object.prototype.hasOwnProperty.call(I18N_UZ_SERVICES, key) ? I18N_UZ_SERVICES[key] : name;
}

function tUnit(unit) {
  if (_lang !== 'uz' || typeof unit !== 'string') return unit;
  return unit.replace(/[А-Яа-яЁё]+/g, w => I18N_UZ_UNITS[w.toLowerCase()] || w);
}

// Подпись периода статистики: «05 октября 2026 г.» → «05 oktabr 2026-yil»
function tPeriod(s) {
  if (_lang !== 'uz' || typeof s !== 'string') return s;
  return s
    .replace(/(январ|феврал|март|апрел|ма[йя]|июн|июл|август|сентябр|октябр|ноябр|декабр)[а-яё]*/gi, w => {
      const uz = I18N_UZ_MONTHS[w.toLowerCase().slice(0, 3)];
      if (!uz) return w;
      return w[0] === w[0].toUpperCase() ? uz[0].toUpperCase() + uz.slice(1) : uz;
    })
    .replace(/(\d{4}) г\./g, '$1-yil');
}

// Переводит текст, уже написанный в разметке страницы (один раз при загрузке)
function i18nApplyStatic(root) {
  if (!root || (_lang === 'ru' && !Object.keys(I18N_RU).length)) return;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);
  nodes.forEach(node => {
    const tag = node.parentElement && node.parentElement.tagName;
    if (tag === 'SCRIPT' || tag === 'STYLE') return;
    const raw = node.nodeValue;
    const key = raw.trim();
    if (!key) return;
    let tr = t(key);
    if (tr === key) {
      // «⚠ Потеря связи с ПК», «▶ Продолжить» — значок в начале оставляем как есть
      const m = key.match(/^([^\p{L}\p{N}]+\s)(.+)$/u);
      if (m && t(m[2]) !== m[2]) tr = m[1] + t(m[2]);
    }
    if (tr !== key) node.nodeValue = raw.replace(key, tr);
  });
  root.querySelectorAll('[title],[placeholder],[alt]').forEach(el => {
    ['title', 'placeholder', 'alt'].forEach(attr => {
      const v = el.getAttribute(attr);
      if (v) { const tr = t(v); if (tr !== v) el.setAttribute(attr, tr); }
    });
  });
}

function setLang(lang) {
  if (!I18N_LANGS.includes(lang) || lang === _lang) return;
  try { localStorage.setItem('bibLang', lang); } catch (e) { /* ignore */ }
  window.location.reload();
}

// Переключатель RU / UZ — рисуется в элементе с id="langSwitch"
function i18nRenderSwitch() {
  const box = document.getElementById('langSwitch');
  if (!box) return;
  if (!document.getElementById('langSwitchStyle')) {
    const st = document.createElement('style');
    st.id = 'langSwitchStyle';
    st.textContent = `
      #langSwitch { display: inline-flex; padding: 3px; gap: 2px; border-radius: 9px; background: rgba(255,255,255,.07); }
      #langSwitch a { padding: 5px 9px; border-radius: 7px; font: 700 12px/1.2 inherit; font-family: inherit;
        color: #aab4c5; text-decoration: none; cursor: pointer; transition: .15s; user-select: none; }
      #langSwitch a:hover { color: #fff; }
      #langSwitch a.on { background: rgba(255,255,255,.16); color: #fff; cursor: default; }`;
    document.head.appendChild(st);
  }
  box.innerHTML = '';
  I18N_LANGS.forEach(lang => {
    const a = document.createElement('a');
    a.textContent = lang.toUpperCase();
    a.title = lang === 'ru' ? 'Русский' : 'O‘zbekcha';
    if (lang === _lang) a.className = 'on';
    a.addEventListener('click', e => { e.preventDefault(); setLang(lang); });
    box.appendChild(a);
  });
}

// Скрипт подключается в конце <body>, разметка к этому моменту уже разобрана
document.documentElement.lang = _lang;
document.title = t(document.title);
i18nApplyStatic(document.body);
i18nRenderSwitch();
