"""
uznel_selectors.py — все «адреса» элементов сайта UZNEL для робота ReaderSync в одном месте.

Если UZNEL поменяет вёрстку и робот перестанет находить кнопки/поля — правки нужны ТОЛЬКО здесь.
Состояние страницы в момент ошибки робот сохраняет в папку debug (снимок экрана и HTML).

Сайт — одностраничное приложение: адрес не меняется, формы открываются внутри одного окна.
id элементов формы содержат порядковый номер («form-03_13_01-1», «-2», …), который растёт при
каждом открытии раздела, поэтому номер в селекторах НЕ зашит: ищем по началу и окончанию id
и берём только видимый элемент (у формы две панели поиска — краткая и расширенная, видна одна).
"""

# ── Вход в систему (проверено в проекте library_catalog) ─────────────────────
LOGIN_ID = "#mainframe_VFrames_HFrames_frameLogin_form_divLogin_edID_input"
LOGIN_PASSWORD = "#mainframe_VFrames_HFrames_frameLogin_form_divLogin_edPassword_input"
LOGIN_BUTTON = "#mainframe_VFrames_HFrames_frameLogin_form_divLogin_btnLogin"
# Признак успешного входа — таймер сессии в строке состояния
LOGGED_IN = "#mainframe_VFrames_frameBottom_form_divInfo_stRemainTime"

# ── Боковое меню ─────────────────────────────────────────────────────────────
MENU_GROUP_TEXT = "Управление регистрацией пользователей"   # один клик раскрывает группу
MENU_LEAF_TEXT = "Управление пользователями"                # двойной клик открывает раздел

# ── Форма «Управление пользователями» ────────────────────────────────────────
FORM_ID_START = "mainframe_VFrames_HFrames_MDIForms_form-03_13_01-"


def form(suffix: str) -> str:
    """form('_btnSearch') -> видимый элемент формы, id которого заканчивается на suffix."""
    return f'[id^="{FORM_ID_START}"][id$="{suffix}"]:visible'


def combo_popup(combo: str) -> str:
    """Раскрытый список комбобокса (рисуется отдельным блоком, только пока список открыт)."""
    return f'[id*="_{combo}_combo"]:visible'


# Критерий поиска (комбобокс) и его пункты
CRIT_COMBO = "cmbSrch0"
CRIT_INPUT = form("_cmbSrch0_comboedit_input")
CRIT_DROP = form("_cmbSrch0_dropbutton")
CRIT_REGISTERED = "Дата регистрации"
CRIT_UPDATED = "Дата последнего обновления"

# Диапазон дат, формат ДД-ММ-ГГГГ
DATE_FROM = form("_Calendar01_calendaredit_input")
DATE_TO = form("_Calendar02_calendaredit_input")

SEARCH_BUTTON = form("_btnSearch")

# Счётчик «Результат поиска» — просто число
RESULT_COUNT = form("_div_GridPage_stSrchCnt")

# Размер страницы: 10, 50, 100, 500, 1000, 5000. Экспорт выгружает только текущую страницу.
PAGE_SIZE_COMBO = "cboPageCntSel"
PAGE_SIZE_INPUT = form("_cboPageCntSel_comboedit_input")
PAGE_SIZE_DROP = form("_cboPageCntSel_dropbutton")
PAGE_SIZE_MAX = 5000

# Таблица и её контекстное меню (правый клик)
GRID_BODY = form("_gridMain_body")
MENU_SELECT_ALL = f'[id^="{FORM_ID_START}"][id$="_popup_menu_gridMain_102"]'    # «Выбрать все»
MENU_SAVE_EXCEL = f'[id^="{FORM_ID_START}"][id$="_popup_menu_gridMain_104"]'    # «Сохранить в Excel»
MENU_SELECT_ALL_TEXT = "Выбрать все"
MENU_SAVE_EXCEL_TEXT = "Сохранить в Excel"

# Кнопка экспорта на верхней панели
EXPORT_BUTTON = "#mainframe_VFrames_frameTop_form_DivRight_imgExport"

# ── Всплывающие сообщения сайта (проверено в проекте library_catalog) ────────
ALERT_OK = '[id*="_popup_modal_"][id$="_form_btnOk"]:visible'
ALERT_MSG = '[id*="_popup_modal_"][id$="_form_staticMsgTextBoxElement"]:visible'
ALERT_NO_RESULT_TEXT = "Нет результата"

# ── Выход ────────────────────────────────────────────────────────────────────
LOGOUT_BUTTON = "#mainframe_VFrames_frameBottom_form_divInfo_ImgLogout"
LOGOUT_YES = "#mainframe_VFrames_frameBottom_logOutBtn_form_btnOk"
