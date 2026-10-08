"""
reader_sync.py — ежедневное обновление базы читателей BibAdminWeb из UZNEL.

Что делает:
  1. Входит на сайт UZNEL, открывает «Управление пользователями».
  2. Ищет читателей, зарегистрированных или изменённых за последние дни
     (при первом запуске или с ключом --full — всех), и сохраняет список в Excel.
  3. Отдаёт файл серверу BibAdminWeb на этом же компьютере (/api/sync/readers).
  4. Сообщает серверу итог — он виден в админке на странице «Читатели».

Сайт отдаёт в Excel не больше 5000 строк за раз, поэтому большой период робот сам делит
пополам, пока каждая часть не уложится в лимит.

Запуск:
  run_sync.cmd                 — обычный ежедневный запуск
  run_sync.cmd --retry         — утренний повтор: работает, только если вечерний запуск не удался
  run_sync.cmd --full          — загрузить всю базу заново
  run_sync.cmd --headed        — с видимым окном браузера (то же, что show_browser = yes в config.ini)
  run_sync.cmd --dry-run       — скачать, но не отправлять на сервер
  run_sync.cmd --from 01-01-2026 --to 30-06-2026   — за указанный период

Настройки — в config.ini (образец: config.example.ini). «Адреса» кнопок сайта — в uznel_selectors.py.
В журнал пишутся только количества, без имён и номеров билетов.
"""

import argparse
import configparser
import json
import logging
import os
import re
import sys
import time
import urllib.error
import urllib.request
import zipfile
from datetime import date, datetime, timedelta

import uznel_selectors as sel

HERE = os.path.dirname(os.path.abspath(__file__))
STATE_FILE = os.path.join(HERE, "state.json")
LOG_DIR = os.path.join(HERE, "logs")
DOWNLOAD_DIR = os.path.join(HERE, "downloads")
DEBUG_DIR = os.path.join(HERE, "debug")
KEEP_DAYS = 14                       # сколько дней хранить журналы и снимки ошибок
FULL_LOAD_FROM = date(1900, 1, 1)
RETRY_SKIP_HOURS = 18                # утренний повтор не нужен, если успех был не раньше стольких часов назад

log = logging.getLogger("reader_sync")


# ── Настройки, состояние, журнал ─────────────────────────────────────────────

def load_config():
    path = os.path.join(HERE, "config.ini")
    if not os.path.exists(path):
        raise RuntimeError("Нет файла config.ini — скопируйте config.example.ini и заполните его")
    cp = configparser.ConfigParser(interpolation=None)
    cp.read(path, encoding="utf-8-sig")
    cfg = {
        "uznel_url": cp.get("uznel", "url", fallback="").strip(),
        "uznel_login": os.environ.get("UZNEL_LOGIN") or cp.get("uznel", "login", fallback="").strip(),
        "uznel_password": os.environ.get("UZNEL_PASSWORD") or cp.get("uznel", "password", fallback=""),
        "bibadmin_url": cp.get("bibadmin", "url", fallback="http://127.0.0.1:8080").strip().rstrip("/"),
        "token_file": cp.get("bibadmin", "token_file", fallback="").strip(),
        "overlap_days": cp.getint("sync", "overlap_days", fallback=3),
        "show_browser": cp.getboolean("sync", "show_browser", fallback=False),
        "slow_ms": cp.getint("sync", "slow_ms", fallback=0),
    }
    missing = [k for k in ("uznel_url", "uznel_login", "uznel_password") if not cfg[k]]
    if missing:
        raise RuntimeError("В config.ini не заполнено: " + ", ".join(missing))
    return cfg


def load_state():
    try:
        with open(STATE_FILE, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_state(state):
    tmp = STATE_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)
    os.replace(tmp, STATE_FILE)


def setup_logging():
    for d in (LOG_DIR, DOWNLOAD_DIR, DEBUG_DIR):
        os.makedirs(d, exist_ok=True)
    fmt = logging.Formatter("%(asctime)s  %(message)s", "%Y-%m-%d %H:%M:%S")
    fh = logging.FileHandler(os.path.join(LOG_DIR, f"sync_{date.today():%Y%m%d}.log"), encoding="utf-8")
    fh.setFormatter(fmt)
    sh = logging.StreamHandler(sys.stdout)
    sh.setFormatter(fmt)
    log.setLevel(logging.INFO)
    log.handlers[:] = [fh, sh]


def prune_old_files():
    """Удаляет старые журналы, снимки ошибок и оставшиеся выгрузки (в них персональные данные)."""
    limit = time.time() - KEEP_DAYS * 86400
    for d in (LOG_DIR, DEBUG_DIR, DOWNLOAD_DIR):
        for name in os.listdir(d):
            p = os.path.join(d, name)
            try:
                if os.path.isfile(p) and os.path.getmtime(p) < limit:
                    os.remove(p)
            except OSError:
                pass


# ── Связь с сервером BibAdminWeb ─────────────────────────────────────────────

def read_token(cfg):
    path = cfg["token_file"]
    if not path or not os.path.exists(path):
        raise RuntimeError(f"Не найден файл ключа сервера: {path or '(token_file не задан в config.ini)'}. "
                           "Он создаётся при запуске BibAdminWeb в папке data\\")
    with open(path, encoding="utf-8") as f:
        return f.read().strip()


def _post(cfg, token, path, body, content_type):
    req = urllib.request.Request(cfg["bibadmin_url"] + path, data=body, method="POST",
                                 headers={"X-Sync-Token": token, "Content-Type": content_type})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return json.loads(r.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:300]
        raise RuntimeError(f"Сервер BibAdminWeb ответил {e.code}: {detail}") from None
    except urllib.error.URLError as e:
        raise RuntimeError(f"Сервер BibAdminWeb недоступен ({cfg['bibadmin_url']}): {e.reason}") from None


def upload_file(cfg, token, path):
    with open(path, "rb") as f:
        return _post(cfg, token, "/api/sync/readers", f.read(), "application/octet-stream")


def report_status(cfg, token, status):
    try:
        _post(cfg, token, "/api/sync/readers/status",
              json.dumps(status, ensure_ascii=False).encode("utf-8"), "application/json")
    except Exception as e:
        log.info("не удалось сообщить результат серверу: %s", e)


# ── Работа с сайтом ──────────────────────────────────────────────────────────

def count_rows_xlsx(path):
    """Число строк в выгрузке (без чтения содержимого). В файле UZNEL нет строки заголовков."""
    with zipfile.ZipFile(path) as z:
        sheet = next(n for n in z.namelist() if n.startswith("xl/worksheets/") and n.endswith(".xml"))
        return len(re.findall(rb"<row[ >]", z.read(sheet)))


def digits(s):
    return re.sub(r"\D", "", s or "")


def fmt(d):
    return d.strftime("%d-%m-%Y")


class Uznel:
    """Действия на сайте. page — страница Playwright."""

    def __init__(self, page):
        self.page = page
        self.step = "запуск"
        self.trace = False      # снимки промежуточных шагов (включается при пробном запуске)
        # «Сохранить в Excel» открывает отдельную вкладку (about:blank), и файл скачивается в ней,
        # поэтому скачивания ловим на всех вкладках окна, а не только на основной.
        self._downloads = []
        self._watch(page)
        page.context.on("page", self._watch)

    def _watch(self, p):
        p.on("download", lambda d: self._downloads.append(d))

    def _download_after(self, action, timeout_s=600):
        """Выполняет action (клик по экспорту) и ждёт файл с любой вкладки. Лишние вкладки закрывает."""
        page = self.page
        self._downloads.clear()
        action()
        start = time.time()
        while not self._downloads:
            if time.time() - start > timeout_s:
                raise RuntimeError("сайт не отдал файл: скачивание не началось")
            msg = self.dismiss_alert()
            if msg:
                raise RuntimeError(f"сайт не отдал файл: {msg}")
            page.wait_for_timeout(300)
        dl = self._downloads[0]
        path = os.path.join(DOWNLOAD_DIR, f"{datetime.now():%Y%m%d_%H%M%S_%f}.xlsx")
        dl.save_as(path)
        for extra in page.context.pages:
            if extra is not page:
                try:
                    extra.close()
                except Exception:
                    pass
        return path

    def set_step(self, text):
        self.step = text
        log.info("  · %s", text)

    # -- вход и переход в раздел --

    def login(self, url, user, password):
        page = self.page
        self.set_step("вход: открываю страницу UZNEL")
        last_err = None
        for attempt in range(3):
            try:
                page.goto(url, wait_until="domcontentloaded", timeout=30_000)
                last_err = None
                break
            except Exception as e:
                last_err = e
                page.wait_for_timeout(5_000)
        if last_err:
            raise RuntimeError(f"сайт UZNEL недоступен: {str(last_err).splitlines()[0]}")

        self.set_step("вход: ввожу логин и пароль")
        login = page.locator(sel.LOGIN_ID)
        login.wait_for(timeout=30_000)
        login.click()
        login.fill(user)
        pwd = page.locator(sel.LOGIN_PASSWORD)
        pwd.click()
        pwd.fill(password)
        self.set_step("вход: нажимаю кнопку входа")
        page.locator(sel.LOGIN_BUTTON).click()
        try:
            page.locator(sel.LOGGED_IN).wait_for(state="visible", timeout=30_000)
        except Exception:
            msg = self.dismiss_alert()
            raise RuntimeError("не удалось войти на UZNEL" + (f": {msg}" if msg else " (проверьте логин и пароль)"))

    def open_readers(self):
        page = self.page
        self.set_step("меню: «Управление регистрацией пользователей»")
        group = page.get_by_text(sel.MENU_GROUP_TEXT, exact=True).first
        group.wait_for(state="visible", timeout=30_000)
        group.click()
        self.set_step("меню: «Управление пользователями»")
        leaf = page.get_by_text(sel.MENU_LEAF_TEXT, exact=True).first
        leaf.wait_for(state="visible", timeout=15_000)
        leaf.dblclick()
        self.set_step("жду загрузку списка читателей")
        page.locator(sel.RESULT_COUNT).first.wait_for(state="visible", timeout=90_000)
        self.wait_count_stable(timeout=120)

    # -- мелкие действия --

    def dismiss_alert(self):
        """Закрывает всплывающее сообщение сайта, возвращает его текст (или '')."""
        page = self.page
        try:
            ok = page.locator(sel.ALERT_OK)
            if ok.count() == 0:
                return ""
            msg = ""
            try:
                msg = page.locator(sel.ALERT_MSG).first.inner_text(timeout=1_000).strip()
            except Exception:
                pass
            ok.first.click(timeout=2_000)
            page.wait_for_timeout(300)
            return msg or "сообщение сайта"
        except Exception:
            return ""

    def counter_text(self):
        try:
            return self.page.locator(sel.RESULT_COUNT).first.inner_text(timeout=2_000).strip()
        except Exception:
            return ""

    def read_count(self):
        """Число найденных записей. Текст счётчика бывает «268» или «268 (0)» — берём первое число."""
        m = re.search(r"\d+", self.counter_text())
        return int(m.group()) if m else None

    def trace_shot(self, name):
        """Снимок экрана на промежуточном шаге (только при пробном запуске) — для отладки."""
        if not self.trace:
            return
        try:
            self.page.screenshot(path=os.path.join(DEBUG_DIR, f"{datetime.now():%Y%m%d_%H%M%S}_{name}.png"))
        except Exception:
            pass

    def slow_click(self, locator, button="left"):
        """Клик «как человек»: навести, подождать, нажать, отпустить. Меню сайта не всегда
        реагирует на мгновенный клик."""
        page = self.page
        locator.wait_for(state="visible", timeout=5_000)
        box = locator.bounding_box()
        if not box:
            raise RuntimeError("элемент не виден на странице")
        x, y = box["x"] + box["width"] / 2, box["y"] + box["height"] / 2
        page.mouse.move(x - 30, y - 6)
        page.mouse.move(x, y, steps=6)
        page.wait_for_timeout(250)
        page.mouse.down(button=button)
        page.wait_for_timeout(80)
        page.mouse.up(button=button)

    def wait_count_stable(self, timeout=120):
        """Ждёт, пока счётчик результатов перестанет меняться. Возвращает число записей."""
        page = self.page
        last, since, start = None, time.time(), time.time()
        while time.time() - start < timeout:
            msg = self.dismiss_alert()
            if msg and sel.ALERT_NO_RESULT_TEXT.lower() in msg.lower():
                return 0
            cur = self.read_count()
            if cur != last:
                last, since = cur, time.time()
            elif cur is not None and time.time() - since >= 2.0:
                return cur
            page.wait_for_timeout(400)
        raise RuntimeError("счётчик результатов поиска не дождался окончания загрузки")

    def type_into(self, selector, text, what):
        """Поля сайта плохо принимают fill(): очищаем и печатаем с клавиатуры, потом проверяем."""
        page = self.page
        field = page.locator(selector).first
        field.wait_for(state="visible", timeout=15_000)
        for _ in range(3):
            field.click()
            field.press("Control+a")
            field.press("Delete")
            page.wait_for_timeout(100)
            page.keyboard.type(text, delay=30)
            page.wait_for_timeout(250)
            if digits(field.input_value()) == digits(text):
                return
            page.wait_for_timeout(700)
        raise RuntimeError(f"не удалось ввести {what}")

    def choose_combo(self, combo, input_sel, drop_sel, text, what):
        """Выбирает пункт выпадающего списка по точному тексту (если он ещё не выбран)."""
        page = self.page
        inp = page.locator(input_sel).first
        inp.wait_for(state="visible", timeout=15_000)
        if inp.input_value().strip() == text:
            return
        for _ in range(3):
            page.locator(drop_sel).first.click()
            page.wait_for_timeout(500)
            item = page.locator(sel.combo_popup(combo)).get_by_text(text, exact=True)
            if item.count() == 0:
                item = page.get_by_text(text, exact=True)
            try:
                item.last.click(timeout=4_000)
            except Exception:
                page.keyboard.press("Escape")
            page.wait_for_timeout(400)
            if inp.input_value().strip() == text:
                return
        raise RuntimeError(f"не удалось выбрать {what} «{text}»")

    # -- поиск и экспорт --

    def set_page_size(self):
        self.set_step(f"размер страницы: {sel.PAGE_SIZE_MAX}")
        self.choose_combo(sel.PAGE_SIZE_COMBO, sel.PAGE_SIZE_INPUT, sel.PAGE_SIZE_DROP,
                          str(sel.PAGE_SIZE_MAX), "размер страницы")

    def search(self, criterion, d1, d2):
        """Ставит критерий и даты, нажимает «Поиск». Возвращает число найденных записей."""
        page = self.page
        self.choose_combo(sel.CRIT_COMBO, sel.CRIT_INPUT, sel.CRIT_DROP, criterion, "критерий поиска")
        self.type_into(sel.DATE_FROM, fmt(d1), "дату «от»")
        self.type_into(sel.DATE_TO, fmt(d2), "дату «до»")
        page.locator(sel.SEARCH_BUTTON).first.click()
        page.wait_for_timeout(1_500)
        return self.wait_count_stable(timeout=180)

    def _select_all(self):
        page = self.page
        page.keyboard.press("Escape")
        grid = page.locator(sel.GRID_BODY).first
        grid.wait_for(state="visible", timeout=15_000)
        # левый клик по первой строке — таблица получает фокус; затем правый клик там же
        grid.click(position={"x": 200, "y": 12})
        page.wait_for_timeout(300)
        grid.click(button="right", position={"x": 200, "y": 12})
        page.wait_for_timeout(600)
        self.trace_shot("1_menu")
        item = page.locator(sel.MENU_SELECT_ALL)
        if item.count() == 0:
            item = page.get_by_text(sel.MENU_SELECT_ALL_TEXT, exact=True)
        self.slow_click(item.last)
        page.wait_for_timeout(1_200)
        log.info("    после «Выбрать все» счётчик: «%s»", self.counter_text())
        self.trace_shot("2_selected")

    def export(self, expected):
        """«Выбрать все» → «Сохранить в Excel». Возвращает путь к файлу, проверив число строк."""
        page = self.page
        last_rows = None
        for attempt in (1, 2):
            self._select_all()

            def click_toolbar():
                page.locator(sel.EXPORT_BUTTON).click()

            def click_menu():
                # запасной путь: пункт контекстного меню таблицы
                page.locator(sel.GRID_BODY).first.click(button="right", position={"x": 200, "y": 40})
                page.wait_for_timeout(400)
                item = page.locator(sel.MENU_SAVE_EXCEL)
                if item.count() == 0:
                    item = page.get_by_text(sel.MENU_SAVE_EXCEL_TEXT, exact=True)
                item.last.click(timeout=5_000)

            path = self._download_after(click_toolbar if attempt == 1 else click_menu)
            last_rows = count_rows_xlsx(path)
            # Число на экране может чуть вырасти, пока идёт выгрузка, — допускаем расхождение в 1 %
            if last_rows > 0 and abs(last_rows - expected) <= max(3, expected // 100):
                return path, last_rows
            log.info("    в файле %s строк при %s на экране — пробую ещё раз", last_rows, expected)
            os.remove(path)
        raise RuntimeError(f"выгрузка не совпала с экраном: в файле {last_rows} строк, на экране {expected}")

    def logout(self):
        page = self.page
        try:
            page.keyboard.press("Escape")
            page.locator(sel.LOGOUT_BUTTON).click(timeout=5_000)
            page.locator(sel.LOGOUT_YES).click(timeout=5_000)
            page.wait_for_timeout(1_500)
        except Exception:
            pass

    def dump_debug(self, tag="error"):
        """Снимок экрана и HTML страницы в момент ошибки — чтобы поправить uznel_selectors.py."""
        base = os.path.join(DEBUG_DIR, f"{datetime.now():%Y%m%d_%H%M%S}_{tag}")
        try:
            self.page.screenshot(path=base + ".png", full_page=True)
            with open(base + ".html", "w", encoding="utf-8") as f:
                f.write(self.page.content())
            return base
        except Exception:
            return ""


# ── План выгрузки ────────────────────────────────────────────────────────────

class Totals:
    def __init__(self):
        self.files = self.rows = self.added = self.updated = self.skipped = 0


def export_range(uz, cfg, token, totals, criterion, d1, d2, dry_run, depth=0):
    """Выгружает период; если записей больше лимита сайта — делит период пополам."""
    count = uz.search(criterion, d1, d2)
    pad = "  " * depth
    log.info("%s%s  %s — %s: найдено %s", pad, criterion, fmt(d1), fmt(d2), count)
    if count == 0:
        return
    if count > sel.PAGE_SIZE_MAX:
        if d1 >= d2:
            raise RuntimeError(f"за один день {fmt(d1)} найдено {count} записей — больше лимита сайта "
                               f"({sel.PAGE_SIZE_MAX}); такой случай робот не обрабатывает")
        mid = d1 + (d2 - d1) // 2
        export_range(uz, cfg, token, totals, criterion, d1, mid, dry_run, depth + 1)
        export_range(uz, cfg, token, totals, criterion, mid + timedelta(days=1), d2, dry_run, depth + 1)
        return

    path, rows = uz.export(count)
    totals.files += 1
    totals.rows += rows
    if dry_run:
        log.info("%s  файл сохранён (%s строк), на сервер не отправляю: %s", pad, rows, path)
        return
    res = upload_file(cfg, token, path)
    totals.added += res.get("added", 0)
    totals.updated += res.get("updated", 0)
    totals.skipped += res.get("skipped", 0)
    log.info("%s  отправлено: строк %s, добавлено %s, обновлено %s, без изменений %s",
             pad, rows, res.get("added", 0), res.get("updated", 0), res.get("skipped", 0))
    os.remove(path)     # в файле персональные данные — после успешной отправки не храним


def parse_date(s):
    return datetime.strptime(s, "%d-%m-%Y").date()


def main():
    ap = argparse.ArgumentParser(description="Обновление базы читателей BibAdminWeb из UZNEL")
    ap.add_argument("--full", action="store_true", help="загрузить всю базу заново")
    ap.add_argument("--retry", action="store_true", help="работать, только если последний запуск не удался")
    ap.add_argument("--headed", action="store_true", help="показывать окно браузера")
    ap.add_argument("--dry-run", action="store_true", help="скачать, но не отправлять на сервер")
    ap.add_argument("--from", dest="date_from", help="начало периода, ДД-ММ-ГГГГ")
    ap.add_argument("--to", dest="date_to", help="конец периода, ДД-ММ-ГГГГ")
    args = ap.parse_args()

    setup_logging()
    prune_old_files()
    state = load_state()
    started = datetime.now()

    if args.retry and state.get("last_success"):
        last = datetime.fromisoformat(state["last_success"])
        if started - last < timedelta(hours=RETRY_SKIP_HOURS):
            log.info("повтор не нужен: последнее успешное обновление %s", last.strftime("%d.%m.%Y %H:%M"))
            return 0

    cfg, token, uz = None, "", None
    totals = Totals()
    mode = "за период"
    try:
        cfg = load_config()
        token = "" if args.dry_run else read_token(cfg)
        today = date.today()

        if args.date_from:
            d1 = parse_date(args.date_from)
            d2 = parse_date(args.date_to) if args.date_to else today
            plan = [(sel.CRIT_REGISTERED, d1, d2), (sel.CRIT_UPDATED, d1, d2)]
        elif args.full or not state.get("last_to"):
            mode = "полная загрузка"
            plan = [(sel.CRIT_REGISTERED, FULL_LOAD_FROM, today)]
        else:
            mode = "ежедневное"
            d1 = date.fromisoformat(state["last_to"]) - timedelta(days=cfg["overlap_days"])
            # новые читатели и те, у кого билет продлён или данные изменены
            plan = [(sel.CRIT_REGISTERED, d1, today), (sel.CRIT_UPDATED, d1, today)]

        log.info("=== обновление читателей: %s%s ===", mode, " (без отправки на сервер)" if args.dry_run else "")

        from playwright.sync_api import sync_playwright
        with sync_playwright() as pw:
            # Окно браузера видно, если так задано в config.ini (show_browser) или ключом --headed.
            # slow_ms — пауза между действиями, чтобы за шагами можно было следить глазами.
            show = args.headed or cfg["show_browser"]
            browser = pw.chromium.launch(headless=not show, slow_mo=cfg["slow_ms"] if show else 0)
            ctx = browser.new_context(ignore_https_errors=True, accept_downloads=True,
                                      viewport={"width": 1600, "height": 900})
            page = ctx.new_page()
            uz = Uznel(page)
            uz.trace = args.dry_run
            try:
                uz.login(cfg["uznel_url"], cfg["uznel_login"], cfg["uznel_password"])
                uz.open_readers()
                uz.set_page_size()
                for criterion, d1, d2 in plan:
                    uz.set_step(f"выгрузка: {criterion}")
                    export_range(uz, cfg, token, totals, criterion, d1, d2, args.dry_run)
                uz.set_step("выход с сайта")
                uz.logout()
            except Exception:
                dumped = uz.dump_debug()
                if dumped:
                    log.info("снимок страницы в момент ошибки: %s", dumped)
                raise
            finally:
                browser.close()

        finished = datetime.now()
        log.info("=== готово: файлов %s, строк %s, добавлено %s, обновлено %s ===",
                 totals.files, totals.rows, totals.added, totals.updated)
        if not args.dry_run:
            if not args.date_from:      # ручной период не сдвигает точку отсчёта ежедневных запусков
                state["last_to"] = today.isoformat()
            state["last_success"] = finished.isoformat(timespec="seconds")
            save_state(state)
            report_status(cfg, token, {
                "ok": True, "mode": mode,
                "startedAt": started.isoformat(timespec="seconds"),
                "finishedAt": finished.isoformat(timespec="seconds"),
                "files": totals.files, "rows": totals.rows,
                "added": totals.added, "updated": totals.updated, "skipped": totals.skipped,
                "message": "",
            })
        return 0

    except Exception as e:
        step = uz.step if uz else "подготовка"
        message = f"{str(e).splitlines()[0][:300]} (шаг: {step})"
        log.info("!!! ОШИБКА: %s", message)
        if cfg and token:
            report_status(cfg, token, {
                "ok": False, "mode": mode,
                "startedAt": started.isoformat(timespec="seconds"),
                "finishedAt": datetime.now().isoformat(timespec="seconds"),
                "files": totals.files, "rows": totals.rows,
                "added": totals.added, "updated": totals.updated, "skipped": totals.skipped,
                "message": message,
            })
        return 1


if __name__ == "__main__":
    sys.exit(main())
