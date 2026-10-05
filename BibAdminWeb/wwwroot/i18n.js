'use strict';

// ══════════════════════════════════════════════════════════════════════════════
// Языки интерфейса оператора: RU (исходный) / UZ
//
// Ключ словаря — русская строка ровно в том виде, как она написана в коде.
// Если перевода нет — показывается русский текст.
// {имя} в фигурных скобках — подстановка, её не переводить.
//
// ВАЖНО: переводится только отображение. Значения «Лимит», «VIP», «Пауза» и т.п.,
// которыми обмениваются сервер, клиентские ПК и база, остаются русскими.
// ══════════════════════════════════════════════════════════════════════════════

const I18N_UZ = {
  // ── Вход ────────────────────────────────────────────────────────────────────
  'Вход — BibOperator': 'Kirish — BibOperator',
  'Веб-интерфейс оператора': 'Operator veb-interfeysi',
  'Логин': 'Login',
  'Пароль': 'Parol',
  'Войти': 'Kirish',
  'Вход...': 'Kirilmoqda...',
  'Введите логин и пароль': 'Login va parolni kiriting',
  'Ошибка входа': 'Kirishda xato',
  'Нет связи с сервером': 'Server bilan aloqa yo‘q',
  'Неверный логин или пароль': 'Login yoki parol noto‘g‘ri',

  // ── Шапка и вкладки ─────────────────────────────────────────────────────────
  'Оператор': 'Operator',
  'ПК': 'Kompyuter',
  'Читатели': 'Kitobxonlar',
  'История': 'Tarix',
  'Статистика': 'Statistika',
  'Браузерные уведомления': 'Brauzer bildirishnomalari',
  'Включить браузерные уведомления': 'Brauzer bildirishnomalarini yoqish',
  'Уведомления заблокированы — разрешите в настройках браузера': 'Bildirishnomalar bloklangan — brauzer sozlamalarida ruxsat bering',
  'Тема оформления': 'Mavzu',
  'Выйти': 'Chiqish',
  'Подключено': 'Ulangan',

  // ── Панель над карточками ПК ────────────────────────────────────────────────
  'Все': 'Barchasi',
  'Свободно': 'Bo‘sh',
  'Сессии': 'Seanslar',
  'Оффлайн': 'Oflayn',
  'Поиск ПК…': 'Kompyuterni qidirish…',
  'Услуга': 'Xizmat',
  'Долги': 'Qarzlar',
  'Перезагрузить': 'Qayta yuklash',
  'Выкл. все': 'Barchasini o‘chirish',

  // ── Карточка ПК и нижняя панель ─────────────────────────────────────────────
  'Свободен': 'Bo‘sh',
  'Готов к работе': 'Ishga tayyor',
  'Нет связи': 'Aloqa yo‘q',
  'Начать сессию': 'Seansni boshlash',
  'Лимит': 'Limit',
  'Открытая': 'Ochiq',
  'прошло': 'o‘tdi',
  'пауза': 'pauza',
  'Пауза': 'Pauza',
  'Продолжить': 'Davom ettirish',
  'продолжить': 'davom ettirish',
  'Завершить': 'Yakunlash',
  'Экран': 'Ekran',
  'Оффлайн (сессия)': 'Oflayn (seans)',
  'Обновление': 'Yangilanish',
  'сум': 'so‘m',
  '0 сум': '0 so‘m',
  'сум/час': 'so‘m/soat',
  'Сообщение': 'Xabar',
  'Отправить сообщение': 'Xabar yuborish',
  'Время': 'Vaqt',
  'Убрать время': 'Vaqtni kamaytirish',
  'Добавить время': 'Vaqt qo‘shish',
  'Штраф': 'Jarima',
  'Пересадить': 'Ko‘chirish',

  // ── Диалог «Начать сессию» ──────────────────────────────────────────────────
  'Тип сессии': 'Seans turi',
  'безлимит': 'cheksiz',
  'Предустановки': 'Tayyor variantlar',
  '30 мин': '30 daq',
  '1 ч': '1 soat',
  '1.5 ч': '1,5 soat',
  '2 ч': '2 soat',
  'ч': 'soat',
  'мин': 'daq',
  'Сумма': 'Summa',
  'Открытая сессия': 'Ochiq seans',
  'Без ограничения по времени. Оплата рассчитывается по факту при завершении —': 'Vaqt cheklanmagan. To‘lov seans yakunida haqiqiy vaqt bo‘yicha hisoblanadi —',
  'Читательский билет': 'Kitobxon bileti',
  'Постоянный (3 года)': 'Doimiy (3 yil)',
  'Временный (1 день)': 'Vaqtinchalik (1 kun)',
  'Имя читателя': 'Kitobxon ismi',
  '(заполняется автоматически)': '(avtomatik to‘ldiriladi)',
  'Имя *': 'Ism *',
  'Или введите вручную': 'Yoki qo‘lda kiriting',
  'Отмена': 'Bekor qilish',
  'Сессия закончится в {time}': 'Seans {time} da tugaydi',
  'Рабочий день заканчивается в {end} — обрезано до {cap} мин = {sum} сум': 'Ish kuni {end} da tugaydi — {cap} daqiqagacha qisqartirildi = {sum} so‘m',
  'Введите номер читательского билета': 'Kitobxon bileti raqamini kiriting',
  'Читатель не найден в базе': 'Kitobxon bazadan topilmadi',
  'Читательский билет просрочен': 'Kitobxon biletining muddati o‘tgan',
  'Проверьте номер читательского билета': 'Kitobxon bileti raqamini tekshiring',
  'Введите имя пользователя': 'Foydalanuvchi ismini kiriting',
  'Временный билет №{num} — посещение будет зафиксировано': 'Vaqtinchalik bilet №{num} — tashrif qayd etiladi',
  'Читатель {id} не найден в базе': '{id} kitobxoni bazadan topilmadi',
  'Добавить': 'Qo‘shish',
  'Добавление…': 'Qo‘shilmoqda…',
  '{id} — добавлен как новый читатель': '{id} — yangi kitobxon sifatida qo‘shildi',
  'Читатель добавлен': 'Kitobxon qo‘shildi',
  'Ошибка добавления': 'Qo‘shishda xato',
  'Временный билет выдан {date}, действителен только в день выдачи': 'Vaqtinchalik bilet {date} da berilgan, faqat berilgan kuni amal qiladi',
  'Билет просрочен с {date}': 'Bilet muddati {date} dan o‘tgan',
  '{n} лет': '{n} yosh',
  'до {date}': '{date} gacha',

  // ── Смена типа сессии ───────────────────────────────────────────────────────
  'Сменить тип сессии': 'Seans turini o‘zgartirish',
  'Подтвердить': 'Tasdiqlash',
  'Текущий тип': 'Joriy tur',
  'Оплачено': 'To‘langan',
  'Прошло': 'O‘tdi',
  'Время сверх оплаченных {time} будет начислено по тарифу в конце сессии.': 'To‘langan {time} dan ortiq vaqt seans oxirida tarif bo‘yicha hisoblanadi.',
  'Перевести на VIP': 'VIP ga o‘tkazish',
  'Ещё осталось': 'Qolgan vaqt',
  'Оплата за всё время сессии начислится по тарифу при завершении.': 'Seansning butun vaqti uchun to‘lov yakunda tarif bo‘yicha hisoblanadi.',
  'Ограничить сессию': 'Seansni cheklash',
  'Сессия завершится в {time}': 'Seans {time} da yakunlanadi',
  'Укажите оставшееся время': 'Qolgan vaqtni ko‘rsating',

  // ── Добавить / убрать время, штраф ──────────────────────────────────────────
  'Убрать': 'Kamaytirish',
  'Сумма возврата': 'Qaytariladigan summa',
  'Укажите время': 'Vaqtni ko‘rsating',
  'Штраф снимет время (при Лимите) и дополнительно спишет сумму из баланса сессии.': 'Jarima vaqtni kamaytiradi (Limit turida) va qo‘shimcha ravishda seans balansidan summani yechadi.',
  'Сумма штрафа': 'Jarima summasi',
  'Применить штраф': 'Jarima qo‘llash',
  'Укажите время штрафа': 'Jarima vaqtini ko‘rsating',
  'Укажите сумму штрафа': 'Jarima summasini ko‘rsating',

  // ── Итог сессии ─────────────────────────────────────────────────────────────
  'Сессия завершена': 'Seans yakunlandi',
  'Тип': 'Turi',
  'Время в сессии': 'Seansdagi vaqt',
  'Начислено': 'Hisoblangan',
  'Возврат': 'Qaytarish',
  'Доплатить': 'Qo‘shimcha to‘lov',
  'Неоплаченные услуги': 'To‘lanmagan xizmatlar',
  'Итого долгов': 'Jami qarz',
  'Оплатить долги по услугам': 'Xizmatlar bo‘yicha qarzlarni to‘lash',
  'Долги оплачены': 'Qarzlar to‘landi',
  'Ошибка оплаты: ': 'To‘lovda xato: ',
  'Закрыть': 'Yopish',

  // ── Потеря связи и пересадка ────────────────────────────────────────────────
  'Потеря связи с ПК': 'Kompyuter bilan aloqa uzildi',
  'Выберите, что сделать с сессией': 'Seans bilan nima qilishni tanlang',
  'Поставить на паузу': 'Pauzaga qo‘yish',
  'Решение по {pc}: {decision}': '{pc} bo‘yicha qaror: {decision}',
  'Пересадить сессию': 'Seansni ko‘chirish',
  'Выберите ПК назначения': 'Qaysi kompyuterga ko‘chirishni tanlang',
  'Сессия с: {pc}': 'Seans: {pc} dan',
  'Сессия перенесена на {pc}': 'Seans {pc} ga ko‘chirildi',
  'Нет доступных ПК для пересадки (нужен свободный онлайн-ПК)': 'Ko‘chirish uchun kompyuter yo‘q (bo‘sh va onlayn kompyuter kerak)',
  'Нет авторизации': 'Avtorizatsiya yo‘q',
  'На исходном ПК нет активной сессии': 'Dastlabki kompyuterda faol seans yo‘q',
  'ПК назначения не в сети': 'Tanlangan kompyuter tarmoqda emas',
  'На ПК назначения уже есть сессия': 'Tanlangan kompyuterda seans bor',

  // ── Услуги и долги ──────────────────────────────────────────────────────────
  'Продажа услуги': 'Xizmat sotish',
  'Привязать к сессии': 'Seansga biriktirish',
  'Привязать к {pc}': '{pc} ga biriktirish',
  '— Без привязки —': '— Biriktirilmagan —',
  'Читатель': 'Kitobxon',
  '(необязательно)': '(ixtiyoriy)',
  'Номер читательского билета': 'Kitobxon bileti raqami',
  'Итого': 'Jami',
  'Оплата': 'To‘lov',
  'Сейчас': 'Hozir',
  'Позже': 'Keyinroq',
  'Выберите активную сессию, чтобы отложить оплату': 'To‘lovni kechiktirish uchun faol seansni tanlang',
  'Оформить': 'Rasmiylashtirish',
  '(анонимный)': '(anonim)',
  'Сессия на {pc}: {reader}': '{pc} dagi seans: {reader}',
  'Сессия на {pc} (анонимный пользователь)': '{pc} dagi seans (anonim foydalanuvchi)',
  'Нет доступных услуг': 'Mavjud xizmatlar yo‘q',
  'Все доступные услуги уже добавлены': 'Barcha mavjud xizmatlar qo‘shilgan',
  'Выберите хотя бы одну услугу': 'Kamida bitta xizmatni tanlang',
  'Услуга "{name}" создана. Сумма: {sum} сум': '"{name}" xizmati yaratildi. Summa: {sum} so‘m',
  '(отложено)': '(kechiktirilgan)',
  'Задолженности по услугам': 'Xizmatlar bo‘yicha qarzlar',
  'Нет непогашенных долгов': 'To‘lanmagan qarzlar yo‘q',
  'Оплатить': 'To‘lash',
  'Долг оплачен': 'Qarz to‘landi',
  'Ошибка загрузки долгов: ': 'Qarzlarni yuklashda xato: ',

  // ── Экран ПК и сообщения ────────────────────────────────────────────────────
  'Экран ПК': 'Kompyuter ekrani',
  'Загрузка...': 'Yuklanmoqda...',
  'Подключение...': 'Ulanmoqda...',
  'Ожидание кадра...': 'Kadr kutilmoqda...',
  'Обновлено': 'Yangilandi',
  'Развернуть': 'Kattalashtirish',
  'Свернуть': 'Kichraytirish',
  'Сообщение на ПК': 'Kompyuterga xabar',
  'Текст сообщения': 'Xabar matni',
  'Сообщение появится по центру экрана пользователя…': 'Xabar foydalanuvchi ekranining markazida paydo bo‘ladi…',
  'Отправить': 'Yuborish',
  'Введите текст сообщения': 'Xabar matnini kiriting',
  'Сообщение отправлено на {pc}': 'Xabar {pc} ga yuborildi',

  // ── Управление ПК ───────────────────────────────────────────────────────────
  'Выключить все ПК?': 'Barcha kompyuterlar o‘chirilsinmi?',
  'Перезагрузить все ПК?': 'Barcha kompyuterlar qayta yuklansinmi?',
  'Команда выключения отправлена всем ПК': 'O‘chirish buyrug‘i barcha kompyuterlarga yuborildi',
  'Команда перезагрузки отправлена всем ПК': 'Qayta yuklash buyrug‘i barcha kompyuterlarga yuborildi',
  'Команда перезагрузки отправлена на {pc}': 'Qayta yuklash buyrug‘i {pc} ga yuborildi',
  'Ошибка перезагрузки: ': 'Qayta yuklashda xato: ',
  'Ошибка: ': 'Xato: ',

  // ── Уведомления и связь с сервером ──────────────────────────────────────────
  '{pc} — потеря связи': '{pc} — aloqa uzildi',
  'Сессия {type} · {time}': 'Seans {type} · {time}',
  '{pc} — сессия завершена': '{pc} — seans yakunlandi',
  'Анонимный': 'Anonim',
  '{h}ч {m}м': '{h} soat {m} daq',
  'Обновление сервера': 'Server yangilanmoqda',
  'Сервер перезапускается. После обновления войдите в систему снова.': 'Server qayta ishga tushmoqda. Yangilanishdan so‘ng tizimga qayta kiring.',
  'Сервер перезапускается': 'Server qayta ishga tushmoqda',
  'Обновление системы': 'Tizim yangilanmoqda',
  'Страница обновится автоматически': 'Sahifa avtomatik yangilanadi',
  'Сервер недоступен': 'Server mavjud emas',
  'Переподключение к серверу...': 'Serverga qayta ulanmoqda...',
  'Связь восстановлена': 'Aloqa tiklandi',
  'Права доступа обновлены': 'Kirish huquqlari yangilandi',

  // ── Вкладка «Читатели» ──────────────────────────────────────────────────────
  'Поиск по базе читателей библиотеки': 'Kutubxona kitobxonlari bazasidan qidirish',
  'Поиск по ФИО или номеру билета…': 'F.I.Sh. yoki bilet raqami bo‘yicha qidirish…',
  'Найти': 'Qidirish',
  'Введите запрос и нажмите «Найти»': 'So‘rovni kiriting va «Qidirish» tugmasini bosing',
  'Поиск…': 'Qidirilmoqda…',
  'Ничего не найдено': 'Hech narsa topilmadi',
  'Ошибка соединения': 'Ulanishda xato',
  'Найдено': 'Topildi',
  '№ билета': 'Bilet №',
  'ФИО': 'F.I.Sh.',
  'Категория': 'Toifa',
  'Дата рождения': 'Tug‘ilgan sana',
  'Пол': 'Jinsi',
  'Зарегистрирован': 'Ro‘yxatdan o‘tgan',

  // ── Вкладка «История» ───────────────────────────────────────────────────────
  'Услуги': 'Xizmatlar',
  'Скачать Excel': 'Excel yuklab olish',
  'Загрузка…': 'Yuklanmoqda…',
  'Нет данных': 'Ma’lumot yo‘q',
  'Пользователь': 'Foydalanuvchi',
  'Длительность': 'Davomiyligi',
  'Начало': 'Boshlanishi',
  'Конец': 'Tugashi',
  'Единица': 'Birlik',
  'Кол-во': 'Soni',
  'Цена/ед': 'Narx/birlik',
  'Дата': 'Sana',

  // ── Вкладка «Статистика» ────────────────────────────────────────────────────
  'День': 'Kun',
  'Месяц': 'Oy',
  'Квартал': 'Chorak',
  'Год': 'Yil',
  'Показать': 'Ko‘rsatish',
  'Экспорт Excel': 'Excelga eksport',
  'Выберите период и нажмите «Показать»': 'Davrni tanlang va «Ko‘rsatish» tugmasini bosing',
  'Выберите дату': 'Sanani tanlang',
  'Ошибка': 'Xato',
  'Ошибка загрузки': 'Yuklashda xato',
  'Нет данных о посещениях за выбранный период': 'Tanlangan davr uchun tashriflar haqida ma’lumot yo‘q',
  'Визитов всего': 'Jami tashriflar',
  'Анонимных': 'Anonim',
  'Уникальных читателей': 'Noyob kitobxonlar',
  'Выручка (сум)': 'Tushum (so‘m)',
  'Период': 'Davr',
  'По полу': 'Jins bo‘yicha',
  'По категориям': 'Toifalar bo‘yicha',
  'По возрастным группам (с разбивкой по полу)': 'Yosh guruhlari bo‘yicha (jinsga ajratilgan)',
  'По возрастным группам': 'Yosh guruhlari bo‘yicha',
  'По услугам': 'Xizmatlar bo‘yicha',
  'Визиты': 'Tashriflar',
  'Уникальных': 'Noyob',
  'Группа': 'Guruh',
  '{g} визиты': '{g} tashriflar',
  '{g} уник.': '{g} noyob',
  '{g} сессий': '{g} seanslar',
  'Сумма (сум)': 'Summa (so‘m)',
  'Компьютер (сессии)': 'Kompyuter (seanslar)',
  'Услуги не использовались': 'Xizmatlardan foydalanilmagan',
  'Компьютеры': 'Kompyuterlar',
  '— статистика сессий за ПК': '— kompyuter seanslari statistikasi',
  'Сессий за ПК': 'Kompyuter seanslari',
  'Сессий': 'Seanslar',
  'Выручка ПК (сум)': 'Kompyuter tushumi (so‘m)',
  'Топ 10 по визитам': 'Tashriflar bo‘yicha top 10',
  'Топ 10 по часам': 'Soatlar bo‘yicha top 10',
  'Визитов': 'Tashriflar',
  'Часов': 'Soat',
  'до 14': '14 gacha',
  'Не указан': 'Ko‘rsatilmagan',
  'Не указана': 'Ko‘rsatilmagan',
  'М': 'E',
  'Ж': 'A',
  'Временный': 'Vaqtinchalik',

  // ── Темы оформления ─────────────────────────────────────────────────────────
  'Тема': 'Mavzu',
  'Светлая': 'Yorug‘',
  'Классическая': 'Klassik',
  'Тёмная': 'Qorong‘i',
  'Тиффани': 'Tiffani',
  'Бирюзовая': 'Feruza',
  'Ночь': 'Tun',
  'Зелёная тёмная': 'To‘q yashil',
  'Сепия': 'Sepiya',
  'Тёплая': 'Iliq',
  'Аметист': 'Ametist',
  'Фиолетовая': 'Binafsha',
  'Свои цвета': 'O‘z ranglarim',
  'Конструктор': 'Konstruktor',
  'Конструктор темы': 'Mavzu konstruktori',
  'Свои цвета панели': 'Panel uchun o‘z ranglaringiz',
  'Начать с базы': 'Asos sifatida olish',
  'Сбросить': 'Tiklash',
  'Готово': 'Tayyor',
  'Фон и шапка': 'Fon va sarlavha',
  'Фон страницы': 'Sahifa foni',
  'Шапка панели': 'Panel sarlavhasi',
  'Поверхности и окна': 'Sirtlar va oynalar',
  'Карточки и окна': 'Kartochkalar va oynalar',
  'Заливки и поля': 'To‘ldirish va maydonlar',
  'Границы': 'Chegaralar',
  'Текст': 'Matn',
  'Основной текст': 'Asosiy matn',
  'Вторичный текст': 'Ikkinchi darajali matn',
  'Акцент и кнопки': 'Urg‘u va tugmalar',
  'Акцент': 'Urg‘u',
  'Статусы': 'Holatlar',
  'Свободен · деньги': 'Bo‘sh · pul',
  'В сессии': 'Seansda',
  'Опасные действия': 'Xavfli amallar',

  // ── Только в BibAdmin (WPF) — упрощённая страница оператора ─────────────────
  'VIP (безлимит)': 'VIP (cheksiz)',
  'Время (мин)': 'Vaqt (daq)',
  'ID читателя': 'Kitobxon ID',
  'Начать': 'Boshlash',
  'Оказать услугу': 'Xizmat ko‘rsatish',
  'Тип услуги': 'Xizmat turi',
  'Количество': 'Soni',
  'Создать': 'Yaratish',
  'ПК всего': 'Jami kompyuterlar',
  'онлайн': 'onlayn',
  'сессий': 'seanslar',
  'заблок.': 'bloklangan',
  'Имя или ID': 'Ism yoki ID',
  'Заблокирован': 'Bloklangan',
  'Соединение потеряно. Ожидание сервера...': 'Aloqa uzildi. Server kutilmoqda...',
};

// Строки с сервера, в которые уже подставлены значения
const I18N_UZ_PATTERNS = [
  [/^ПК (.+) не найден$/, '{1} kompyuteri topilmadi'],
  [/^Временный №(.+)$/, 'Vaqtinchalik №{1}'],
  [/^Незарег\. (.+)$/, 'Ro‘yxatsiz {1}'],
];

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
  let r = s;
  if (_lang === 'uz' && typeof s === 'string' && Object.prototype.hasOwnProperty.call(I18N_UZ, s)) r = I18N_UZ[s];
  if (vars && typeof r === 'string') r = r.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? vars[k] : m));
  return r;
}

// Перевод строки, пришедшей с сервера (ошибки, подписи в статистике)
function tServer(s) {
  if (_lang !== 'uz' || typeof s !== 'string') return s;
  if (Object.prototype.hasOwnProperty.call(I18N_UZ, s)) return I18N_UZ[s];
  for (const [re, tpl] of I18N_UZ_PATTERNS) {
    const m = s.match(re);
    if (m) return tpl.replace(/\{(\d)\}/g, (x, i) => m[+i]);
  }
  return s;
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
  if (_lang === 'ru' || !root) return;
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
