# Архитектура

## Решение: переписать, а не дорабатывать v0.1

v0.1 — WinForms-монолит (~700 строк): UI напрямую формирует RGB-пакет (`MainForm → StudioLogic.BuildRgbReport → HidInspector.TryWriteRgb`), нет разделения слоёв, нет тестов, GIF показывается покадрово без композиции, редактора слоёв нет. Требования (WPF-интерфейс, mock-транспорт, write-gate, редактор) нельзя добавить без смены структуры, поэтому создан новый проект `KORTY_Keyboard_Studio`. Из v0.1 перенесены только **идеи** (RGB565, 128×128, локальный профиль в AppData); путь записи удалён (`SECURITY_AUDIT.md`).

## Слои (зависимости направлены строго вниз)

```
Korty.App (WPF, MVVM)  ──►  Korty.Rendering (WPF-графика)  ──►  Korty.Core
        │                                                          ▲
        └────────────►  Korty.Hid.Windows (SetupAPI/HID) ──────────┘
Korty.Diag (консоль, read-only)  ──►  Korty.Hid.Windows, Korty.Core
```

| № | Слой задания | Проект / пространство имён | Содержимое |
|---|--------------|----------------------------|-----------|
| 1 | Desktop UI | `Korty.App/Views`, `Controls`, `Themes` | 7 страниц, схема клавиатуры (`KeyboardView`), тёмная тема OLED/графит |
| 2 | Application services | `Korty.App/Services`, `ViewModels`; `Core/Protocol/DeviceCommandService`, `Core/Recovery` | `AppHost` (composition root), предпроверка отправки, планировщик восстановления |
| 3 | Device discovery | `Core/Discovery`, `Hid.Windows/WindowsHidEnumerator` | классификатор кабель/донгл/чужие, монитор подключений с дебаунсом |
| 4 | HID transport | `Core/Hid` (интерфейсы), `Hid.Windows/WindowsHidTransport`, `Core/Simulation` | единственная точка записи; mock = `SimulatedKeyboard` |
| 5 | Protocol adapters | `Core/Protocol`, `Core/Tft/TftPayload.cs` | каталог команд, построители пакетов двух семейств, план загрузки |
| 6 | TFT editor/renderer | `Core/Model/DisplayComposition`, `Rendering/*`, `ViewModels/ScreenEditorViewModel` | слои, GIF-декодер, рендерер, RGB565/дизеринг |
| 7 | RGB management | `Core/Model/RgbSettings`, `Core/Protocol/PacketBuilders`, `ViewModels/RgbViewModel` | настройки, валидация, пакеты, предпросмотр |
| 8 | Profile storage | `Core/Storage` | `ProfileStore` (атомарно), `DesignLibrary`, `LibraryPaths` |
| 9 | Diagnostics/logging | `Core/Diagnostics`, `Korty.Diag` | `DiagLog` (маскирование), снимки, дифференциальный анализ, отчёт |
| 10 | Tests/mocks | `tests/Korty.Tests` | 105 тестов, фейки, мини-писатель GIF |

## Инварианты

1. **UI не знает байтов.** ViewModel вызывает `AppHost.BuildRgbPackets/ BuildDesign` и `DeviceCommandService`; в `Korty.App` нет ни одной константы протокола (проверяется ревизией; сборка пакетов вынесена в Core).
2. **Обнаружение не открывает на запись.** `IHidEnumerator` не имеет методов записи; реализация открывает с `desiredAccess=0`.
3. **Записать можно только `ApprovedWrite`.** Токен выпускает `WriteGate` (internal ctor). Тест `TransportConsumesOnlyApprovedWrites`.
4. **UI не блокируется HID-операциями.** Опрос — `PeriodicTimer` в фоне (`ConnectionMonitor`), отправка — `async` с прогрессом, рендеринг 128×128 — на UI-потоке, но с дебаунсом 16 мс.
5. **Отсутствие клавиатуры — штатное состояние.** Пустая топология, понятные причины в UI (`BlockReason`), ни одного исключения (тесты `NoDeviceMeansNoSend`, `EnumerationFailureNeitherCrashesNorRemovesDevices`).
6. **Чужие HID не затрагиваются.** Другие VID не открываются; VID 0C45 с неизвестным PID — «не клавиатура»; `Resolve` не угадывает интерфейс.

## Потоки данных

```
Редактор (слои) ─► CompositionRenderer ─► TftAnimation(BGRA) ─► TftPayloadBuilder(RGB565+заголовок) ─► TftUploadPlanner ─► [ProtocolPacket…]
                                                                                                         │ dry-run (PacketFormatter)
                                                                                                         ▼
UI-кнопка ─► DeviceCommandService.Check(all) ─► WriteGate.Evaluate(каждый) ─► ApprovedWrite ─► IHidTransport
                                                                                                    ├─ SimulatedKeyboard (разбирает пакеты, декодирует кадры обратно)
                                                                                                    └─ WindowsHidTransport (инертен: реестр пуст)
```

## Хранение (`%LOCALAPPDATA%\KORTY\KeyboardStudio`)

- `profile.json` — настройки + два независимых профиля `usb` / `wireless24` (оформление, RGB, пожелание автовосстановления). Схема версионирована, значения при загрузке ограничиваются допустимыми диапазонами.
- `designs\<id>\composition.json`, `assets\*`, `thumb.png` — библиотека. Идентификаторы и имена файлов валидируются регулярными выражениями (защита от path traversal в подменённом JSON).
- `logs\studio.log` (ротация, 1 МБ × 5), `verified-protocols.json` (создаёт владелец на этапе H1+; по умолчанию отсутствует).

## Восстановление состояния (часть C)

`RestorePlanner` строит прозрачный план (устройство → стабилизация → подсветка → экран → проверка) и для каждого шага сообщает, пройдёт ли он write-gate. Автовосстановление при подключении (опция профиля) выполняется через тот же планировщик с задержкой 3 с на стабилизацию и **никогда не обходит gate**. Сейчас на реальном железе все шаги заблокированы; в симуляторе план выполняется и проверен тестами. Прочитать состояние экрана обратно нельзя (подтверждённой команды чтения нет) — результат оценивает владелец.
