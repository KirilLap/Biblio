# API Reference

Веб-сервер `BibAdminWeb` предоставляет REST API и три SignalR-хаба. Все эндпоинты доступны на порту `8080`.

---

## Аутентификация

Система разделяет два типа пользователей: **администратор** и **оператор**.

### Администратор

```http
POST /api/admin/login
Content-Type: application/json

{ "password": "ваш_пароль" }
```

При успехе устанавливается cookie `bib_admin` (HttpOnly, 24 часа). Все запросы `/api/admin/*` требуют этот cookie.

```http
POST /api/admin/logout
GET  /api/admin/check        → { "ok": true } если сессия активна
```

### Оператор

```http
POST /api/op/login
Content-Type: application/json

{ "login": "логин", "password": "пароль" }
```

При успехе устанавливается cookie `bib_op` (HttpOnly, 24 часа). 

```http
POST /api/op/logout
GET  /api/op/me              → информация о текущем операторе
```

---

## REST API — Администратор

Все эндпоинты требуют активную сессию администратора (cookie `bib_admin`).

### Настройки

```http
GET  /api/admin/settings
```
Возвращает глобальные настройки системы.

```http
POST /api/admin/settings
Content-Type: application/json
```
Сохраняет глобальные настройки и рассылает обновления всем клиентским ПК.

---

### Финансы — Сессии

```http
GET  /api/admin/finance/sessions
```
Возвращает историю завершённых сессий. Поддерживает фильтрацию по дате.

```http
DELETE /api/admin/finance/sessions
```
Очищает историю сессий.

```http
GET /api/admin/finance/export
```
Экспортирует историю в CSV-файл.

---

### Финансы — Услуги

```http
GET  /api/admin/finance/services
```
Возвращает список оказанных услуг (печать, сканирование и др.).

```http
DELETE /api/admin/finance/services
```
Очищает историю услуг.

```http
POST /api/admin/finance/services/{id}/pay
```
Отмечает услугу как оплаченную.

---

### Операторы

```http
GET /api/admin/operators
```
Возвращает список всех операторов.

```http
POST /api/admin/operators
Content-Type: application/json

{ "login": "логин", "password": "пароль", "displayName": "Имя" }
```
Создаёт нового оператора. Пароль хешируется перед сохранением.

```http
DELETE /api/admin/operators/{id}
PUT    /api/admin/operators/{id}         → обновление данных
POST   /api/admin/operators/{id}/password → смена пароля
```

---

### Компьютеры

```http
GET /api/admin/computers
```
Возвращает список всех зарегистрированных ПК и их текущее состояние.

```http
DELETE /api/admin/computers/{pcNumber}
```
Удаляет ПК из реестра.

---

## SignalR хабы

SignalR обеспечивает двустороннее взаимодействие в реальном времени. Клиент подключается и может вызывать методы на сервере (и наоборот).

### /hub — AdminHub (клиентские ПК)

Хаб для подключения `BibClient`. Клиент вызывает методы при старте и в процессе работы.

**Методы, вызываемые клиентом:**

| Метод | Параметры | Описание |
|---|---|---|
| `RegisterClient` | `info, macAddress, isRestoring, sessionId, offlineSeconds` | Регистрация ПК при подключении |
| `SendHeartbeat` | `pcNumber` | Периодический сигнал о том, что клиент жив |
| `UpdateStatus` | `pcNumber, status, sessionType, elapsedSeconds` | Обновление статуса сессии |
| `SyncSessionTime` | `pcNumber, force` | Синхронизация времени сессии |
| `RenameClient` | `oldName, newName` | Переименование ПК |
| `SetClientCustomName` | `pcNumber, customName` | Установка кастомного имени |
| `SendCommand` | `pcNumber, commandJson` | Отправка команды конкретному ПК |
| `SendCommandToAll` | `commandJson` | Рассылка команды всем ПК |
| `TransferSession` | `fromPcNumber, toPcNumber` | Перенос активной сессии на другой ПК |
| `UploadFile` | `fileName, fileData, targetPc, replaceIndividual` | Загрузка файла (фон) на клиент |

---

### /adminhub — AdminWebHub (десктопный BibAdmin)

Хаб для подключения десктопного приложения администратора. Получает события об изменении состояния клиентов и отправляет команды управления.

---

### /webhub — OperatorHub (браузер оператора)

Хаб для подключения операторов через браузер. Оператор видит состояние ПК в реальном времени и может управлять сессиями в рамках своих прав.

---

## Команды для клиентских ПК

Команды отправляются через SignalR как JSON. Клиент обрабатывает их в `PolicyEngine.cs`.

### Управление сессией

| Команда | Значение | Описание |
|---|---|---|
| `START_SESSION` | тип сессии | Начать сессию |
| `PAUSE_SESSION` | — | Поставить на паузу |
| `RESUME_SESSION` | — | Возобновить сессию |
| `END_SESSION` | — | Завершить сессию |

### Настройки и безопасность

| Команда | Значение | Описание |
|---|---|---|
| `ADMIN_PASSWORD` | SHA256-хеш | Обновить хеш пароля администратора |
| `SET_TARIFF` | число (тиын) | Установить стоимость минуты |
| `USB_BLOCK` | true/false | Блокировка USB-устройств |
| `TASKMGR_DISABLE` | true/false | Отключить диспетчер задач |
| `BLOCK_REGEDIT` | true/false | Заблокировать редактор реестра |
| `BLOCK_CMD` | true/false | Заблокировать командную строку |
| `BLOCK_POWERSHELL` | true/false | Заблокировать PowerShell |
| `HIDE_DRIVE_C` | true/false | Скрыть диск C в проводнике |
| `BLOCK_INSTALL_UNINSTALL` | true/false | Запретить установку/удаление ПО |
| `LOCK_ON_OFFLINE` | true/false | Блокировать ПК при потере сети |
| `PREVENT_CLOSE` | true/false | Запретить закрытие BibClient |
| `AUTOSTART_WITH_USER` | true/false | Автозапуск при входе пользователя |

### Интерфейс экрана блокировки

| Команда | Значение | Описание |
|---|---|---|
| `SHOW_PC_NAME` | true/false | Показывать имя ПК |
| `SHOW_PC_NUMBER` | true/false | Показывать номер ПК |
| `SET_PC_NUMBER_POSITION` | строка позиции | Позиция номера на экране |
| `SET_PC_NUMBER_FONT_SIZE` | число | Размер шрифта номера |
| `SHOW_LOCKED_TEXT` | true/false | Показывать текст блокировки |
| `SET_LOCKED_TEXT_POSITION` | строка позиции | Позиция текста |
| `SET_LOCKED_TEXT_FONT_SIZE` | число | Размер шрифта текста |
| `SET_TIME_POSITION` | строка позиции | Позиция часов |
| `SET_TIME_FONT_SIZE` | число | Размер шрифта часов |
| `SET_BACKGROUND_OPACITY` | 0.0–1.0 | Прозрачность фонового изображения |
| `SET_BACKGROUND` | имя файла | Установить фоновое изображение |

### Системные

| Команда | Описание |
|---|---|
| `SHUTDOWN` | Выключить ПК |
| `RESTART` | Перезагрузить ПК |
| `SHOW_MESSAGE` | Показать текстовое сообщение (Value = текст) по центру экрана клиента |

---

## Форматы позиций

Значения для параметров `*Position`:

```
TopLeft      TopCenter      TopRight
MiddleLeft   MiddleCenter   MiddleRight
BottomLeft   BottomCenter   BottomRight
```
