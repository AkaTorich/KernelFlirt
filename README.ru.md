# KernelFlirt

Отладчик уровня ядра Windows с интерфейсом в стиле OllyDbg. Предназначен для исследования безопасности и реверс-инжиниринга в виртуальных машинах (VMware).

## Архитектура

```
  Хост-машина                            VM (Windows 10, testsigning)
┌──────────────────┐    TCP:31337    ┌──────────────────┐     IOCTL      ┌──────────────────┐
│  KernelFlirt UI  │◄───────────────►│   KfRelay.exe    │◄──────────────►│ KernelFlirt.sys  │
│  (WPF / .NET 9)  │  CMD+DBG кан.  │   (TCP прокси)   │  DeviceIoCtl   │ (WDM драйвер)   │
└──────────────────┘                 └──────────────────┘                └──────────────────┘
                                     ┌──────────────────┐  SCM API
                                     │  KfLoader.exe    │──────────────────────┘
                                     │  (C / Console)   │  load / unload / status
                                     └──────────────────┘
```

Четыре компонента:

| Компонент | Язык | Описание |
|-----------|------|----------|
| **KernelFlirt.UI** | C# / WPF | Интерфейс отладчика в стиле OllyDbg (работает на хосте) |
| **KernelFlirt.sys** | C / WDM | Драйвер ядра — память, точки останова, inline hook на KdTrap |
| **KfRelay.exe** | C | TCP relay-агент на VM, проксирует IOCTL по сети |
| **KfLoader.exe** | C | CLI для загрузки/выгрузки драйвера через SCM |
| **KernelFlirt.SDK** | C# / .NET 9 | SDK плагинов — интерфейсы для отладчика, памяти, точек останова, символов, UI |

## Как это работает

Драйвер хукает **KdpStub** (функция диспетчеризации отладки, вызываемая из KdTrap) через inline hook (14-байтный `JMP [addr]` трамплин). Когда возникает отладочное исключение (#BP или #DB), обработчик:

1. Проверяет, принадлежит ли исключение целевому процессу
2. Ищет точку останова в таблице, заполняет `KF_DEBUG_EVENT`
3. Завершает ожидающий `WAIT_DEBUG_EVENT` IRP
4. Блокирует поток через `KeWaitForSingleObject`
5. По `CONTINUE_DEBUG_EVENT` выполняет step-past (восстановить байт -> TF -> вернуть 0xCC) и продолжает

Для процессов, не являющихся целевыми, но попавших на наш INT3 (общие CoW-страницы), обработчик прозрачно выполняет step-past без уведомления UI.

## Быстрый старт

### 1. Настройка VM (один раз)

```cmd
:: Отключить защиты ядра (нужна перезагрузка)
disable_kernel_protection.ps1
```

### 2. Прогрев KD (при необходимости)

Иногда путь обработки отладочных исключений в ядре (KdTrap -> KdpStub) не активен, пока к системе хотя бы раз не подключался ядерный отладчик. Если `INSTALL_HOOK` проходит успешно, но точки останова не срабатывают (`HookCallCount` остается 0), выполните:

```cmd
:: На ХОСТЕ — запустите kd ДО загрузки VM:
.\kd.exe -k com:pipe,port=\\.\pipe\kf_debug,resets=0,reconnect

:: Затем загрузите/перезагрузите VM.
:: kd поймает initial break. Введите 'g' и нажмите Enter для продолжения.
:: После загрузки Windows можно закрыть kd — путь отладки теперь активен.
```

> **Примечание:** Это нужно один раз за загрузку VM. В некоторых конфигурациях работает и без kd — хук сразу ловит исключения. Если не уверены — выполните шаг с kd.

### 3. Развертывание и запуск

```cmd
:: На VM — скопируйте файлы:
::   KernelFlirt.sys, KfLoader.exe, KfRelay.exe

:: Загрузите драйвер
KfLoader.exe load

:: Запустите relay
KfRelay.exe
:: Слушает на 0.0.0.0:31337

:: На ХОСТЕ — запустите UI
KernelFlirt.exe
:: Нажмите Connect -> введите IP VM (например 10.100.102.4)
```

### 4. Отладка процесса

1. **File -> Open** — обзор файловой системы VM, выбор EXE или SYS
2. Процесс создается приостановленным, BP на entry point ставится автоматически
3. **F9** (Run) — попадает на entry point, загружает символы и модули
4. Ставьте точки останова на функции через правый клик или F2
5. **F9** — запуск, **F7** — шаг с заходом, **F8** — шаг через

### 5. Отладка драйвера

KernelFlirt также умеет отлаживать драйверы ядра. Можно ставить точки останова на любые ядерные функции — как в вашем драйвере, так и на импорты ядра (ntoskrnl, HAL и т.д.).

1. Загрузите тестовый драйвер на VM (например через `sc create` + `sc start`)
2. В KernelFlirt присоединитесь к процессу, который вызовет ваш драйвер (или используйте тестовое приложение)
3. Откройте вкладку **Kernel Modules** — найдите ваш драйвер, двойной клик для дизассемблирования
4. Откройте вкладку **Imports** — IAT-записи разрешены в ядерные функции (например `ntoskrnl.exe!DbgPrint`)
5. Ставьте точки останова на функции драйвера или ядерные импорты через правый клик → **Set Breakpoint** или F2
6. **F9** (Run) — вызовите драйвер, точка останова сработает
7. Пошаговая отладка ядерного кода через **F7** / **F8**, инспекция регистров и стека вызовов

> **Примечание:** Программные точки останова на ядерных функциях (INT3) используют MDL-патчинг памяти. BP на общую ядерную функцию (например DbgPrint) будет срабатывать для ВСЕХ вызывающих — вашего драйвера, других драйверов и самого ядра. Хук прозрачно обрабатывает нецелевые срабатывания, но учитывайте это при отладке горячих путей.

## Настройка символов

KernelFlirt использует **dbghelp.dll** для разрешения символов. Путь поиска символов настраивается в UI.

### Рекомендуемый формат пути символов

```
D:\MySoftware\Release;srv*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

Где:
- `D:\MySoftware\Release` — локальная папка с вашими PDB (рядом с EXE)
- `srv*C:\Symbols*https://msdl.microsoft.com/download/symbols` — Microsoft Symbol Server с локальным кэшем в `C:\Symbols`

### Только символьный сервер (без локальных PDB)

```
srv*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

Символы загружаются автоматически: для ядерных модулей — при подключении, для пользовательских модулей — при присоединении к процессу.

## Возможности

### Отладка
- **Программные точки останова** — INT3 с автоматическим восстановлением байта
- **Аппаратные точки останова** — DR0-DR3 на исполнение (до 4 одновременно)
- **Аппаратные watchpoints** — DR0-DR3 на запись и чтение/запись (1/2/4/8 байт)
- **Точки останова на память** — PAGE_GUARD для детекции обращений к памяти
- **Условные точки останова** — Остановка при истинности выражения (`RAX==0`, `RCX!=0`, `RDX>0x100`)
- **Точки останова с логированием** — Логирование значений регистров без остановки
- **Счетчик срабатываний** — Каждая BP считает количество попаданий
- **Step into** (F7) — Пошаговое исполнение через TF
- **Step over** (F8) — Временный INT3 на следующую инструкцию
- **Step out** (Ctrl+F9) — Временный INT3 на адрес возврата [RSP]
- **Run to cursor** (F4) — Временный INT3 на выбранный адрес

### Память
- **Чтение/запись памяти** — Через MmCopyVirtualMemory (до 1 МБ за чтение)
- **Hex dump** — 16 байт/строка с ASCII
- **Поиск по паттерну** — Байтовые паттерны с `??` wildcard
- **Поиск строк** — ASCII и Unicode по всем модулям
- **Межмодульные вызовы** — CALL к другим модулям (API-вызовы)
- **Отслеживание патчей** — Все модификации записываются, можно откатить

### Интроспекция
- **Модули процесса** — Обход PEB->Ldr для DLL
- **Модули ядра** — 177+ драйверов с символами
- **Потоки** — Состояние, приоритет, адрес старта
- **Регистры** — Полный x64 CONTEXT: RAX-R15, RIP, RFLAGS, сегменты, DR0-7
- **Стек вызовов** — Эвристический обход стека с разрешением символов
- **Цепочка SEH** — Обработчики структурных исключений
- **Закладки** — Именованные адреса для быстрой навигации

### Ядерный отладочный хук (KdTrap)
- **Inline hook на KdpStub** — 14-байтный JMP-трамплин
- **Сканирование паттернов** — Поиск KdTrap по сигнатуре (`48 83 EC 38 83 3D...`) в .text ntoskrnl
- **KdDebuggerEnabled/NotPresent** — Патчатся для маршрутизации через KdTrap
- **Пере-утверждение** — KdDebuggerEnabled перезаписывается при каждом ContinueDebugEvent
- **Прозрачный step-past** — Нецелевые процессы на нашем INT3 обрабатываются тихо
- **Инвертированная модель вызовов** — Ожидающий IRP завершается при debug event
- **Блокировка потока** — Через KeWaitForSingleObject до ответа UI

### Интерфейс (стиль OllyDbg)
- **9 встроенных тем** — default-dark, x64dbg, monokai, ollydbg, ollydbg-light, ida-pro, dracula, long_night, sakura
- **Смена темы в реальном времени** — Все цвета меняются через Settings, применяются мгновенно (DynamicResource)
- **Настраиваемые цвета** — Общие, Дизассемблер (14 цветов), Стек (3 цвета), Стиль вкладок, индивидуальные цвета заголовков вкладок
- **Дизассемблер** — Подсветка синтаксиса, маркеры BP, текущая инструкция
- **Панель регистров** — Изменения красным, правый клик Follow
- **Панель стека** — Цветной RSP-относительный вывод (смещение, адрес, аннотация/подсказка)
- **Hex dump** — 16 байт/строка с ASCII
- **14 вкладок**: Disassembly, Breakpoints, Modules, Kernel Modules, Threads, Call Stack, Bookmarks, Patches, Exceptions, Sections, Strings, Search, Imports, Functions, Decompiler, Log — каждая с индивидуальным цветом заголовка
- **Удаленный файловый менеджер** — Обзор ФС виртуальной машины, запуск EXE
- **Выбор процесса** — Фильтр по имени или PID
- **Полноэкранный режим** — F11
- **Система плагинов** — SDK с API для отладчика, памяти, точек останова, символов, UI

## Горячие клавиши

| Клавиша | Действие |
|---------|----------|
| F2 | Вкл/выкл программную точку останова |
| F4 | Выполнить до курсора |
| F5 | Продолжить выполнение |
| F7 | Шаг с заходом |
| F8 | Шаг через |
| F9 | Запуск |
| F12 | Пауза |
| Ctrl+G | Перейти к адресу |
| Ctrl+F9 | Выполнить до возврата |
| Ctrl+F | Поиск по паттерну |
| F11 | Полноэкранный режим |

## Сборка

### Требования
- Visual Studio 2022 с C++ workload
- Windows Driver Kit (WDK) 10.0.26100.0+
- .NET 9 SDK
- Windows 10/11 x64

### Сборка

```powershell
.\build.ps1                          # Release (все компоненты)
.\build.ps1 -Configuration Debug     # Debug
```

### Результат
```
bin/Driver/  KernelFlirt.sys
bin/Loader/  KfLoader.exe
bin/Relay/   KfRelay.exe
bin/UI/      KernelFlirt.exe
```

## Структура проекта

```
KernelFlirt/
├── build.ps1                          # Скрипт сборки
├── sign-driver.ps1                    # Подпись драйвера
├── include/
│   └── kf_shared.h                    # Общие IOCTL-коды и структуры
├── src/
│   ├── driver/                        # Драйвер ядра (C / WDM)
│   │   ├── main.c                     # DriverEntry, Unload, dispatch
│   │   ├── ioctl.c                    # Диспетчер IOCTL
│   │   ├── debughook.c                # KdTrap inline hook + обработчик
│   │   ├── breakpoint.c               # SW/HW/Memory breakpoints
│   │   ├── memory.c                   # Чтение/запись памяти
│   │   ├── registers.c                # Чтение/запись регистров
│   │   ├── threads.c                  # Потоки
│   │   ├── modules.c                  # Модули процесса
│   │   ├── kmodules.c                 # Модули ядра
│   │   ├── process.c                  # Attach/detach процесса
│   │   ├── singlestep.c              # Пошаговое исполнение
│   │   ├── compat.c                   # Совместимость с версиями ОС
│   │   └── ntqsi_hook.c              # Хук NtQuerySystemInformation
│   ├── relay/                         # TCP relay (C)
│   │   └── main.c                     # CMD+DBG каналы, псевдо-IOCTL
│   ├── loader/                        # Загрузчик драйвера (C)
│   │   ├── main.c                     # CLI
│   │   ├── service.c                  # Windows SCM API
│   │   └── vmdetect.c                 # Детекция гипервизора
│   ├── testdriver/                    # Тестовый драйвер (C)
│   │   └── main.c                     # Простой драйвер для тестирования
│   ├── sdk/                           # SDK плагинов (.NET)
│   │   ├── KernelFlirt.SDK.csproj
│   │   ├── IKernelFlirtPlugin.cs      # Интерфейс плагина
│   │   ├── IDebuggerApi.cs            # API отладчика
│   │   ├── IMemoryApi.cs              # API чтения/записи памяти
│   │   ├── IBreakpointApi.cs          # API точек останова
│   │   ├── IProcessApi.cs             # API процессов/модулей
│   │   ├── ISymbolApi.cs              # API символов
│   │   ├── ILogApi.cs                 # API логирования
│   │   ├── IUiApi.cs                  # API интерфейса
│   │   └── Models.cs                  # Общие модели данных
│   └── ui/                            # WPF UI отладчика (C#)
│       ├── MainWindow.xaml/cs         # Главное окно + обработчики
│       ├── SettingsWindow.xaml/cs     # Настройки тем и цветов
│       ├── ColorPickerDialog.xaml/cs  # Выбор цвета с пресетами
│       ├── InputDialog.xaml/cs        # Диалог ввода
│       ├── PluginSettingsWindow.xaml/cs # Настройки плагинов
│       ├── App.xaml/cs                # Точка входа
│       ├── ViewModels/
│       │   └── MainViewModel.cs       # Все команды и состояние отладки
│       ├── Models/                    # Модели данных
│       │   ├── Instruction.cs         # Дизассемблированная инструкция
│       │   ├── Breakpoint.cs          # Точка останова
│       │   ├── StackEntry.cs          # Запись стека (смещение/адрес/подсказка)
│       │   ├── CallStackFrame.cs      # Фрейм стека вызовов
│       │   ├── ModuleInfo.cs          # Модуль процесса
│       │   ├── KernelModuleInfo.cs    # Модуль ядра
│       │   ├── Register.cs            # Регистр CPU
│       │   ├── ThreadInfo.cs          # Информация о потоке
│       │   ├── Bookmark.cs            # Закладка
│       │   ├── Patch.cs               # Патч памяти
│       │   ├── ImportEntry.cs         # IAT-импорт
│       │   ├── FunctionEntry.cs       # Функция
│       │   ├── SectionEntry.cs        # PE-секция
│       │   ├── StringEntry.cs         # Найденная строка
│       │   ├── SearchResult.cs        # Результат поиска
│       │   ├── ExceptionEntry.cs      # Запись цепочки SEH
│       │   └── ProcessInfo.cs         # Процесс
│       ├── Controls/
│       │   ├── DisasmView.xaml/cs     # Вид дизассемблера (AvalonEdit)
│       │   └── HexDumpView.xaml/cs    # Вид hex-дампа
│       ├── Views/
│       │   ├── RemoteFileBrowserDialog.xaml/cs  # Файловый менеджер VM
│       │   └── ProcessPickerDialog.xaml/cs      # Выбор процесса
│       ├── Services/
│       │   ├── DriverComm.cs          # IOCTL обертка (локально + TCP)
│       │   ├── Disassembler.cs        # Capstone x86-64
│       │   ├── Symbols.cs             # Разрешение символов (dbghelp)
│       │   ├── DbgEngService.cs       # Интеграция с WinDbg engine
│       │   ├── PluginManager.cs       # Загрузка и управление плагинами
│       │   ├── PluginApi.cs           # Реализация API плагинов
│       │   └── Interop/
│       │       ├── DbgHelpNative.cs   # P/Invoke dbghelp.dll
│       │       └── DbgEngNative.cs    # P/Invoke dbgeng.dll
│       ├── Converters/
│       │   └── HexValueConverter.cs   # Конвертер hex-значений
│       └── Themes/
│           └── Dark.xaml              # Базовая темная тема + все кисти
├── samples/                           # Примеры плагинов
│   ├── SamplePlugin/                  # Минимальный пример
│   ├── AntiDebugPlugin/               # Обход анти-отладки
│   ├── AntiDebugTest/                 # Тестовая цель для AntiDebugPlugin
│   ├── ThemidaPlugin/                 # Распаковщик Themida
│   └── ApiMonitorPlugin/             # Мониторинг API-вызовов
├── themes/                            # Исходные файлы тем
│   ├── default-dark.txt               # Material Ocean (по умолчанию)
│   ├── x64dbg.txt                     # Стиль x64dbg
│   ├── monokai.txt                    # Monokai
│   ├── ollydbg.txt                    # OllyDbg тёмная
│   ├── ollydbg-light.txt             # OllyDbg классическая светлая
│   ├── ida-pro.txt                    # IDA Pro / IntelliJ
│   ├── dracula.txt                    # Dracula
│   ├── long_night.txt                 # Long Night (IDA)
│   └── sakura.txt                     # Sakura (розово-лавандовая)
├── docs/
│   └── SDK.md                         # Документация Plugin SDK
├── Scripts/
│   └── disable_kernel_protection.ps1  # Отключение защит ядра VM
├── KD/                                # KD отладчик (бинарники)
│   ├── kd.exe, dbgeng.dll, dbghelp.dll, ...
│   └── symsrv.dll
└── bin/                               # Результат сборки
    ├── Driver/  KernelFlirt.sys
    ├── Loader/  KfLoader.exe
    ├── Relay/   KfRelay.exe
    └── UI/      KernelFlirt.exe + themes/
```

## Безопасность

- **Только VM** — Предназначен для виртуальных машин с testsigning
- **Не для продакшна** — Драйвер модифицирует код ядра (inline hook на KdpStub)
- **Валидация входных данных** — Все IOCTL-обработчики проверяют размеры буферов
- **SEH защита** — `__try/__except` при обращении к usermode указателям
- **Отмена IRP** — Ожидающие IRP корректно отменяются
- **Контроль IRQL** — KeWaitForSingleObject только при IRQL <= APC_LEVEL
- **Spin lock** — Таблица BP и состояние отладки под KSPIN_LOCK

## Зависимости

| Пакет | Версия | Назначение |
|-------|--------|------------|
| CommunityToolkit.Mvvm | 8.4.0 | MVVM фреймворк |
| Dirkster.AvalonDock | 4.72.1 | Стыкуемые панели |
| AvalonEdit | 6.3.0.90 | Текстовый редактор |
| Gee.External.Capstone | 2.3.0 | Дизассемблер x86-64 |

## Лицензия

Только для образовательных целей и исследования безопасности. Используйте ответственно в авторизованных окружениях.
