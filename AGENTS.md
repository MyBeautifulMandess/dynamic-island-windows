# AGENTS.md — Dynamic Island for Windows

Документ для агентов и для людей, которые скачали репозиторий. Здесь описано,
что это за проект, как его собрать, где лежит каждая функция и по каким правилам
менять код, чтобы не сломать соседние части.

Соседний проект — телефонное приложение **DI Link** (Android):
<https://github.com/MyBeautifulMandess/dilink-android>. Островок и телефон
общаются по своему протоколу; обе стороны должны меняться синхронно.

---

## 1. Что это

«Островок» в духе macOS Dynamic Island для Windows: маленькая пилюля у верхнего
края экрана, которая разворачивается в панель. Панели: центр, календарь,
напоминания, заметки, быстрые файлы, медиа, уведомления, AI-агенты, буфер
обмена, телефон, загрузки, погода.

Технически: WPF, .NET 8, `net8.0-windows10.0.19041.0`, без сторонних
зависимостей кроме `System.Management`. Собственный WebSocket-сервер поверх
`TcpListener` — для связи с телефоном.

## 2. Требования и быстрый старт

| Что | Нужно |
|---|---|
| Сборка | .NET 8 SDK (`dotnet --version` → 8.x) |
| Установщик | Inno Setup 6 (`ISCC.exe` в PATH) |
| ОС | Windows 10 19041 или новее, x64 |
| Связь с телефоном | общая Wi-Fi сеть, [DI Link](https://github.com/MyBeautifulMandess/dilink-android) на Android 7.0+ |

```bash
git clone https://github.com/MyBeautifulMandess/dynamic-island-windows.git
cd dynamic-island-windows
dotnet restore
dotnet run -c Release          # запуск из исходников
```

Портативный файл и установщик:

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o dist/portable

ISCC.exe installer/DynamicIsland.iss
```

Готовые файлы: `dist/portable/DynamicIsland.exe` и `dist/installer/…Setup.exe`.
Папка `dist/` в `.gitignore` — артефакты сборки в репозиторий не попадают.

Установка для проверки (тихая):

```bash
MSYS_NO_PATHCONV=1 cmd.exe /c "dist\installer\DynamicIsland-Setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP-"
```

Автозапуск пишется в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
Проверка: `Get-ItemProperty 'HKCU:\...\Run' | Select-Object *DynamicIsland*`.

## 3. Карта репозитория

```
MainWindow.xaml(.cs)      окно-островок: состояния, анимация, навигация, все сценарии
App.xaml(.cs)             общие стили, ресурсы, запуск приложения
Converters.cs             преобразователи значений для XAML
Models/                   настройки, модели данных, каталог панелей
Services/                 вся логика по одному классу на функцию
installer/DynamicIsland.iss  конфигурация Inno Setup
Assets/app.ico            иконка окна, трея и установщика
```

### 3.1. `Models/`

| Файл | Что внутри |
|---|---|
| `AppSettings.cs` | пользовательские настройки: порядок панелей, включённые панели, положение окна |
| `PhoneSettings.cs` | настройки связи с телефоном: адрес, порт, ключ, автосопряжение |
| `Items.cs` | `ChatMessage`, `MediaState`, `WeatherInfo` — наблюдаемые модели для панелей |
| `ExtraItems.cs` | `CalendarEventItem`, `ReminderItem`, `CalendarDayItem`, `PanelOption`, `QuickFileItem`, `TimeSlot` |
| `ObservableObject.cs` | базовый класс с `INotifyPropertyChanged` |
| `PanelCatalog.cs` | список всех панелей: идентификатор, заголовок, глиф. **Сюда добавляется новая панель** |
| `PlannerRecord.cs` | запись календаря/напоминания в формате, общем с телефоном |

### 3.2. `Services/`

Локальные функции островка:

| Файл | Ответственность |
|---|---|
| `NotificationService.cs`, `NotificationCenter.cs` | системные уведомления Windows, общий центр и раздача подписчикам |
| `MediaService.cs` | медиасессии Windows (плеер, громкость) |
| `ClipboardService.cs` | буфер обмена и синхронизация с телефоном |
| `CalendarService.cs`, `NotesService.cs`, `QuickFilesService.cs`, `DownloadsService.cs`, `WeatherService.cs`, `AiService.cs` | остальные панели |
| `SettingsService.cs` | чтение и запись `AppSettings` |
| `TrayIconService.cs` | иконка в трее и её меню (в т. ч. «Отправить файл на телефон…») |
| `NativeMethods.cs` | Win32: полноэкранный режим, z-порядок, трей, состояние кнопок мыши |
| `SystemControlService.cs` | системные действия (закрыть окно, блокировка и т. п.) |
| `SecretProtector.cs` | шифрование ключа сопряжения на диске |
| `Diagnostics.cs` | журнал `%LOCALAPPDATA%\DynamicIsland\diagnostics.log` |
| `QrCodeGenerator.cs` | генерация QR-кода без сторонних библиотек |

Связь с телефоном:

| Файл | Ответственность |
|---|---|
| `PhoneSocketServer.cs` | WebSocket-сервер поверх `TcpListener`: кадры, handshake, маршрутизация сообщений |
| `PhoneLinkService.cs` | транспорт и сопряжение: QR-данные, проверка ключа, ping/pong, рассылка |
| `PhoneProtocol.cs` | формат сообщений: `Build`, `Parse`, `Clamp` — общий словарь протокола |
| `PhoneDeviceStore.cs` | список известных устройств и их ключи |
| `PhoneLinkCoordinator.cs` | связка транспорта с буфером, файлами, медиа, планером |
| `PhoneFileService.cs` | приём и отправка файлов (`.part` → переименование только после подтверждения) |
| `PhoneMediaService.cs` | состояние плеера телефона → островок |
| `PhoneNotificationService.cs` | уведомления Android: склейка пачек, фильтры исключений |
| `PlannerSyncService.cs` | календарь и напоминания: зеркало, надгробия, слияние «новее — побеждает» |
| `PhonePairingPage.cs` | HTML-страница сопряжения, которую открывает камера телефона |

## 4. Протокол «островок ↔ телефон`

Формат кадра — JSON: `{"type": "...", "id": "...", "payload": {...}}`.
Транспорт — WebSocket, порт **19532**, авторизация общим ключом из QR.
Полный словарь команд — в `Services/PhoneProtocol.cs` и в README раздела
«Как устроено внутри».

Основные типы сообщений:

| Тип | Кто шлёт | Назначение |
|---|---|---|
| `ping` / `pong` | оба | проверка живости канала |
| `clipboard.push` | оба | текст буфера обмена |
| `notification.push` / `notify.dismissed` | телефон | уведомления Android |
| `media.state` / `media.command` | оба | плеер телефона |
| `file.offer` / `file.chunk` / `file.done` / `file.ack` / `file.cancel` | оба | файлы; данные идут бинарными кадрами после `file.chunk` |
| `planner.push` / `planner.sync` | оба | календарь и напоминания; `sync` — полный снимок |

**Правило:** новое сообщение добавляется в обеих реализациях сразу —
`Services/PhoneProtocol.cs` (островок) и `link/Protocol.kt` (DI Link).
Формат полей camelCase, время в миллисекундах Unix.

## 5. Как добавить или изменить функцию

### Новая панель

1. `Models/PanelCatalog.cs` — добавить запись `(id, «Заголовок», «\uE8B7»)` в `All`.
2. `MainWindow.xaml` — блок панели с `x:Name` вида `PanelXxx` внутри общей сетки.
3. `MainWindow.xaml.cs` — обработчик открытия/закрытия панели и наполнение.
4. `App.xaml` — стили, если нужны новые ресурсы; палитра задана в `App.xaml`
   (`AccentBrush`, `MutedBrush`, `CardBrush` и т. п.).

### Новая функция телефона

1. Класс в `Services/`, конструктор принимает `PhoneLinkService`, подписывается на
   `MessageReceived` — окно с сокетом напрямую не работает.
2. Регистрация в `PhoneLinkCoordinator.cs` (создание, `Dispose`, маршрутизация).
3. Сообщение — в `Services/PhoneProtocol.cs` и в `link/Protocol.kt` на стороне телефона.
4. Если меняется структура `PlannerRecord` — синхронно в `Models/PlannerRecord.cs`
   и `planner/PlannerRecord.kt`.

### Синхронизация записей

Слияние всегда по правилу **«новее — побеждает»** (`updated` в миллисекундах).
Удаление — **надгробие** (`deleted = true`), иначе запись вернётся со второй
стороны. Записи, созданные до первого подключения, получают `updated = 1`.

## 6. Правила стиля

- Язык интерфейса и комментариев — русский, тот же тон, что в существующем коде.
- Комментарий объясняет **почему**, а не что делает код; избыточные комментарии
  не пишутся.
- Именование: `Phone*` — телефон, `Panel*` — элементы панели, `*Service` — служба,
  `*Item` — элемент списка.
- Никаких новых NuGet-пакетов без явной причины: проект сознательно без
  зависимостей, свой QR и свой WebSocket написаны своими руками.
- Асинхронные операции с UI — через `Dispatcher`, фоновые — через `Task.Run`.
- Пользовательские данные не пишутся в лог: `Diagnostics.cs` пишет только
  технические сообщения.

## 7. Проверка перед коммитом

1. `dotnet build -c Release` — без предупреждений и ошибок.
2. Запуск из исходников и ручная проверка: островок появляется при наведении,
   панели переключаются, трей работает, автозапуск записан в `Run`.
3. Если трогали связь с телефоном: подключить телефон по QR, проверить передачу
   файла в обе стороны, уведомления, календарь и напоминания.
4. Измерить нагрузку (раздел «Отчёт по нагрузке» в README) и обновить цифры,
   если изменилась работа с памятью.
5. Проверить установщик: собрать `ISCC.exe installer/DynamicIsland.iss`, установить
   в тихом режиме, убедиться, что версия в реестре совпадает с `<Version>` в
   `DynamicIsland.csproj`.

## 8. Релиз

1. Поднять версию в двух местах: `<Version>` в `DynamicIsland.csproj` и
   `#define MyAppVersion` в `installer/DynamicIsland.iss`.
2. Добавить раздел «Что нового в X.Y.Z» в README.
3. Собрать портативную версию и установщик, проверить установку.
4. Закоммитить, отметить тег `vX.Y.Z`, создать релиз на GitHub с файлами
   `DynamicIsland-Portable.exe` и `DynamicIsland-Setup.exe`.
5. Сверить SHA-256 скачанного с GitHub файла и локального.

## 9. Чего делать нельзя

- Не менять формат сообщений протокола в одной из сторон — вторая отвалится.
- Не хранить ключ сопряжения в открытом виде: только `SecretProtector`.
- Не открывать порт наружу: сервер слушает локальную сеть, авторизация — по ключу.
- Не добавлять в панель тяжёлые элементы без виртуализации: список уведомлений
  и заметок намеренно используют виртуальные списки.
- Не забывать про `EmptyWorkingSet` и `GC.ConserveMemory`: приложение живёт
  в фоне часами, лишние страницы памяти заметны.
