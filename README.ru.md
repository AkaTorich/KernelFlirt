# KernelFlirt

Отладчик уровня ядра Windows с интерфейсом в стиле OllyDbg. Предназначен для исследования безопасности и реверс-инжиниринга в виртуальных машинах.

## Архитектура

```
┌────────────────────────┐     DeviceIoControl      ┌──────────────────────┐
│   KernelFlirt UI       │◄────────────────────────► │  KernelFlirt.sys     │
│   (WPF / C# / .NET 9)  │    \\.\KernelFlirt       │  (WDM Kernel Driver) │
└────────────────────────┘                           └──────────────────────┘
                                                              ▲
┌────────────────────────┐     SCM API                        │
│   KfLoader.exe         │───── CreateService / StartService ─┘
│   (C / Console)        │
└────────────────────────┘
```

Три компонента:

| Компонент | Язык | Описание |
|-----------|------|----------|
| **KernelFlirt.UI** | C# / WPF | Интерфейс отладчика в стиле OllyDbg |
| **KernelFlirt.sys** | C / WDM | Драйвер ядра — память, брейкпоинты, debug-хуки |
| **KfLoader.exe** | C | CLI-утилита для загрузки/выгрузки драйвера через SCM |

## Возможности

### Отладка
- **Программные брейкпоинты** — инъекция INT3 с автоматическим восстановлением байта
- **Аппаратные брейкпоинты** — DR0-DR3 на исполнение (до 4 одновременно)
- **Аппаратные вотчпоинты** — DR0-DR3 на запись и чтение/запись данных с настраиваемой длиной (1/2/4/8 байт)
- **Брейкпоинты на память** — обнаружение доступа к памяти через PAGE_GUARD и ZwProtectVirtualMemory
- **Условные брейкпоинты** — остановка только при истинном выражении (например, `RAX==0`, `RCX!=0`, `RDX>0x100`)
- **Логирующие брейкпоинты** — запись значений регистров/выражений без остановки выполнения
- **Счётчик срабатываний** — каждый брейкпоинт отслеживает количество срабатываний
- **Шаг внутрь** (F7) — манипуляция флагом TF через PsSetContextThread
- **Шаг через** (F8) — временный INT3 на следующей инструкции для пропуска CALL
- **Шаг из функции** (Ctrl+F9) — временный INT3 по адресу возврата [RSP] для выхода из текущей функции
- **Выполнить до курсора** (F4) — временный INT3 по выбранному адресу в дизассемблере
- **Подключение/отключение от процесса** — приостановка главного потока при подключении, возобновление всех потоков при отключении

### Память
- **Чтение/запись памяти процесса** — через MmCopyVirtualMemory (до 1 МБ за чтение)
- **Hex-дамп** — классическое отображение 16 байт/строка hex+ASCII с навигацией по адресу
- **Бинарный поиск** — поиск шаблонов байтов в памяти с поддержкой подстановочного символа `??`
- **Поиск строк** — поиск ASCII и Unicode строк по всем загруженным модулям
- **Межмодульные вызовы** — поиск инструкций CALL, направленных в другие модули (API-вызовы)
- **Отслеживание патчей** — все модификации памяти записываются, можно восстановить по отдельности или все сразу

### Интроспекция
- **Перечисление модулей** — обход PEB->Ldr->InMemoryOrderModuleList для user-mode DLL
- **Перечисление модулей ядра** — ZwQuerySystemInformation(SystemModuleInformation) для загруженных драйверов
- **Перечисление потоков** — полный список потоков с состоянием, приоритетом, стартовым адресом
- **Приостановка/возобновление потоков** — индивидуальное управление потоками
- **Переключение потоков** — переключение контекста отладки на любой поток
- **Чтение/запись регистров** — полный x64 CONTEXT: RAX-R15, RIP, RFLAGS, сегменты, DR0-DR3/DR6/DR7
- **Стек вызовов** — эвристический обход стека с разрешением адресов возврата в символы модуль+смещение
- **Цепочка SEH** — перечисление цепочки обработчиков структурных исключений
- **Закладки** — сохранение именованных адресов для быстрой навигации

### Хук отладки ядра
- **Хук KiDebugRoutine** — перехват ловушек #DB/#BP на уровне ядра, как WinDbg/KD
- **Сканирование паттернов** — поиск указателя KiDebugRoutine путём сканирования KdChangeOption на RIP-относительные инструкции MOV/LEA
- **Инвертированная модель вызовов** — ожидающий IRP завершается при возникновении события отладки
- **Блокировка потока** — сбойный поток блокируется через KeWaitForSingleObject до отправки UI команды CONTINUE
- **Атомарная установка** — InterlockedExchangePointer для безопасной замены хука
- **Типы событий** — Breakpoint, Single Step, HW Breakpoint, HW Watchpoint, Memory BP (PAGE_GUARD)

### Интерфейс (стиль OllyDbg)
- **Тёмная тема** — пользовательская цветовая палитра: тёмный фон (#1E1E1E), синие адреса, жёлтые мнемоники, зелёные регистры, красные переходы
- **Дизассемблер** — подсветка синтаксиса по токенам с маркерами брейкпоинтов (красная точка), подсветка текущей инструкции, формат адреса с обратным апострофом
- **Панель регистров** — изменённые значения выделены красным, правый клик — Follow in Dump/Disasm
- **Панель стека** — отображение относительно RSP, правый клик — Follow/Copy
- **Панель hex-дампа** — 16 байт/строка с ASCII-сайдбаром, правый клик — Copy/Search
- **10 вкладок внизу**: Breakpoints, Modules, Kernel Modules, Threads, Call Stack, Bookmarks, Patches, SEH Chain, Search, Log
- **Контекстные меню на каждой панели** — Follow in Dump, Follow in Disassembler, Copy, Toggle BP, Search и т.д.
- **Диалог выбора процесса** — фильтрация по имени или PID, двойной клик для подключения
- **Диалоги ввода** — для условных брейкпоинтов, выражений логирования, закладок, шаблонов поиска

### Загрузчик
- **Управление сервисами** — Load/Unload/Status/Info через Windows SCM API
- **Обнаружение ВМ** — определение гипервизора через CPUID leaf 0x40000000 (VMware, VirtualBox, Hyper-V, KVM, Xen)
- **Проверка тестовой подписи** — NtQuerySystemInformation(SystemCodeIntegrityInformation) флаг CODEINTEGRITY_OPTION_TESTSIGN

## Горячие клавиши

| Клавиша | Действие |
|---------|----------|
| F2 | Переключить программный брейкпоинт на выбранном адресе |
| F4 | Выполнить до курсора |
| F5 | Продолжить выполнение (возобновить из debug-хука) |
| F7 | Шаг внутрь |
| F8 | Шаг через |
| F9 | Запустить |
| F12 | Пауза (приостановить поток) |
| Ctrl+G | Перейти к RIP |
| Ctrl+F9 | Шаг из функции (выполнить до возврата) |
| Ctrl+F | Поиск бинарного шаблона |

## Макет интерфейса

```
┌──────────────────────────────────────────────────────────────┐
│ File | Debug | Search | View                                 │
│ [Open][Connect] [PID][Attach][Detach] [Run][Pause][F5][F7]   │
│ [F8][Out][F4] [BP][HW][WW][RW][Mem] [Hook][Unhook] [Addr Go]│
├──────────────────────────────┬───────────────────────────────┤
│  Дизассемблер                │  Регистры                     │
│  ● 00007FF6`00401000  ...    │  RAX  0000000000000001        │
│    00007FF6`00401005  ...    │  RBX  0000000000000000        │
│    ПКМ: BP, Follow,         │  ПКМ: Follow, Copy            │
│    Copy, Search, Bookmark    ├───────────────────────────────┤
│                              │  Стек                         │
│                              │  RSP+00  00007FF600401234     │
│                              │  ПКМ: Follow, Copy            │
├──────────────────────────────┴───────────────────────────────┤
│  Hex-дамп  [Адрес: ___________] [Go]                         │
│  00007FF600400000  48 89 5C 24 08 48 89 6C  H.\$.H.l        │
│  ПКМ: Copy, Follow, Search Binary, Search String             │
├──────────────────────────────────────────────────────────────┤
│ Breakpoints│Modules│KernelMod│Threads│CallStack│Bookmarks│   │
│ Patches│SEH Chain│Search│Log                                 │
│ У каждой вкладки есть контекстное меню: Follow, Copy и т.д.  │
└──────────────────────────────────────────────────────────────┘
```

## Протокол IOCTL

Устройство: `\\.\KernelFlirt` — Метод: `METHOD_BUFFERED` — Тип устройства: `0x8000`

| IOCTL | Код | Вход | Выход |
|-------|-----|------|-------|
| READ_MEMORY | 0x800 | PID, Address, Size | byte[] |
| WRITE_MEMORY | 0x801 | PID, Address, Size, Data | NTSTATUS |
| SET_BREAKPOINT | 0x802 | PID, TID, Address, Type, Length | Handle |
| REMOVE_BREAKPOINT | 0x803 | Handle | — |
| SINGLE_STEP | 0x804 | PID, TID | — |
| READ_REGISTERS | 0x810 | PID, TID | KF_REGISTERS |
| WRITE_REGISTERS | 0x811 | PID, TID, KF_REGISTERS | — |
| ENUM_MODULES | 0x820 | PID | KF_MODULE_ENTRY[] |
| ENUM_KERNEL_MODULES | 0x821 | — | KF_KERNEL_MODULE_ENTRY[] |
| ENUM_THREADS | 0x830 | PID | KF_THREAD_ENTRY[] |
| SUSPEND_THREAD | 0x831 | TID | — |
| RESUME_THREAD | 0x832 | TID | — |
| INSTALL_HOOK | 0x840 | — | — |
| REMOVE_HOOK | 0x841 | — | — |
| WAIT_DEBUG_EVENT | 0x842 | — | KF_DEBUG_EVENT |
| CONTINUE_DEBUG_EVENT | 0x843 | — | — |
| PING | 0x8FF | — | Version, Magic |

### Типы брейкпоинтов

| Тип | Код | Механизм |
|-----|-----|----------|
| Программный | 0 | Инъекция байта INT3 (0xCC) |
| Аппаратный на исполнение | 1 | DR0-DR3, condition=00 |
| Аппаратный на запись | 2 | DR0-DR3, condition=01 |
| Аппаратный на чтение/запись | 3 | DR0-DR3, condition=11 |
| На память | 4 | PAGE_GUARD через ZwProtectVirtualMemory |

### Типы событий отладки

| Тип | Код | Триггер |
|-----|-----|---------|
| Breakpoint | 1 | STATUS_BREAKPOINT (INT3) |
| Single Step | 2 | STATUS_SINGLE_STEP (флаг TF) |
| HW Breakpoint | 3 | DR0-3 execute, бит DR6 установлен |
| HW Watchpoint | 4 | DR0-3 write/RW, DR7 condition != 0 |
| Memory BP | 5 | STATUS_GUARD_PAGE_VIOLATION |

## Сборка

### Требования
- Visual Studio 2022 с нагрузкой «Разработка классических приложений на C++»
- Windows Driver Kit (WDK) 10.0.26100.0+
- .NET 9 SDK
- Windows 10/11 x64

### Сборка

```bash
# Драйвер (ядро)
MSBuild src/driver/driver.vcxproj -p:Configuration=Release -p:Platform=x64

# Загрузчик (usermode CLI)
MSBuild src/loader/loader.vcxproj -p:Configuration=Release -p:Platform=x64

# UI (WPF)
dotnet build src/ui/KernelFlirt.UI.csproj -c Release
```

### Артефакты
- `src/driver/build/driver/Release/KernelFlirt.sys`
- `src/loader/build/loader/Release/KfLoader.exe`
- `src/ui/bin/Release/net9.0-windows/KernelFlirt.exe`

## Использование

```bash
# 1. Включить тестовую подпись (требуется перезагрузка)
bcdedit /set testsigning on

# 2. Загрузить драйвер
KfLoader.exe load --path KernelFlirt.sys

# 3. Проверить статус
KfLoader.exe status

# 4. Запустить UI
KernelFlirt.exe

# 5. В интерфейсе:
#    - Нажмите «Connect» для подключения к драйверу
#    - Нажмите «Open» для выбора процесса или введите PID и нажмите «Attach»
#    - Используйте F7/F8/F9 для отладки
#    - Правый клик в любом месте для контекстного меню

# 6. Выгрузить по завершении
KfLoader.exe unload
```

## Структура проекта

```
KernelFlirt/
├── KernelFlirt.sln
├── README.md
├── README.ru.md
├── include/
│   └── kf_shared.h                 # Общие IOCTL-коды и структуры
├── src/
│   ├── driver/                      # Драйвер ядра (C / WDM)
│   │   ├── driver.vcxproj
│   │   ├── main.c                   # DriverEntry, Unload, dispatch
│   │   ├── device.c                 # Создание устройства, символьная ссылка
│   │   ├── ioctl.c                  # Диспетчер IOCTL
│   │   ├── memory.c                 # Чтение/запись MmCopyVirtualMemory
│   │   ├── breakpoint.c             # SW/HW/Memory брейкпоинты, кодирование DR7
│   │   ├── singlestep.c             # Одиночный шаг через флаг TF
│   │   ├── registers.c              # Чтение/запись CONTEXT
│   │   ├── modules.c                # Перечисление модулей через PEB→Ldr
│   │   ├── kmodules.c               # Перечисление модулей ядра
│   │   ├── threads.c                # Перечисление/приостановка/возобновление потоков
│   │   ├── debughook.c              # Хук KiDebugRoutine + обработчик событий отладки
│   │   ├── debughook.h
│   │   └── ntundoc.h                # Объявления недокументированных NT API
│   ├── loader/                      # CLI-загрузчик драйвера (C)
│   │   ├── loader.vcxproj
│   │   ├── main.c                   # Точка входа CLI (load/unload/status/info)
│   │   ├── service.c                # Управление сервисами через SCM
│   │   └── vmdetect.c               # Обнаружение гипервизора + проверка тестовой подписи
│   └── ui/                          # WPF-интерфейс отладчика (C#)
│       ├── KernelFlirt.UI.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml / .cs
│       ├── Themes/Dark.xaml          # Тёмная цветовая схема в стиле OllyDbg
│       ├── Controls/
│       │   └── DisasmView.xaml/.cs   # Дизассемблер с подсветкой синтаксиса + контекстное меню
│       ├── Views/
│       │   └── ProcessPickerDialog.xaml/.cs
│       ├── ViewModels/
│       │   └── MainViewModel.cs      # Все команды отладки + поиск + закладки
│       ├── Models/
│       │   ├── Instruction.cs        # Дизассемблированная инструкция
│       │   ├── Register.cs           # Имя/значение/изменение регистра
│       │   ├── Breakpoint.cs         # BP с условием, выражением лога, счётчиком
│       │   ├── ModuleInfo.cs         # User-mode модуль
│       │   ├── KernelModuleInfo.cs   # Модуль ядра (драйвер)
│       │   ├── ThreadInfo.cs         # Состояние потока
│       │   ├── DebugEvent.cs         # Событие отладки от хука ядра
│       │   ├── CallStackFrame.cs     # Разобранный кадр стека вызовов
│       │   ├── Bookmark.cs           # Именованная закладка на адрес
│       │   ├── Patch.cs              # Запись о патче памяти
│       │   ├── SehEntry.cs           # Запись цепочки SEH
│       │   └── SearchResult.cs       # Результат бинарного/строкового поиска
│       ├── Services/
│       │   ├── DriverComm.cs         # Обёртка DeviceIoControl (все IOCTL)
│       │   ├── Disassembler.cs       # Обёртка Capstone x86-64
│       │   └── Symbols.cs            # Разрешение символов модуль+смещение
│       └── Converters/
│           └── HexValueConverter.cs  # ulong <-> hex строка
```

## Безопасность

- **Только для ВМ** — этот драйвер предназначен для использования в виртуальных машинах с включённой тестовой подписью
- **Валидация входных данных** — все обработчики IOCTL проверяют размеры входных/выходных буферов перед доступом
- **Защита SEH** — `__try/__except` вокруг обращений к user-mode указателям в ядре
- **ProbeForRead/ProbeForWrite** — валидация указателей пользовательского режима
- **Атомарная установка хука** — `InterlockedExchangePointer` для замены KiDebugRoutine
- **Процедуры отмены IRP** — ожидающие WAIT_DEBUG_EVENT IRP корректно отменяемы
- **Блокировка с учётом IRQL** — KeWaitForSingleObject только при IRQL <= APC_LEVEL
- **Защита спинлоком** — глобальная таблица брейкпоинтов и состояние событий отладки защищены KSPIN_LOCK

## Зависимости

| Пакет | Версия | Назначение |
|-------|--------|------------|
| CommunityToolkit.Mvvm | 8.4.0 | MVVM-фреймворк (ObservableObject, RelayCommand) |
| Dirkster.AvalonDock | 4.72.1 | Компоновка док-панелей |
| AvalonEdit | 6.3.0.90 | Компонент текстового редактора |
| Gee.External.Capstone | 2.3.0 | Дизассемблер x86-64 (привязки Capstone) |

## Лицензия

Только для образовательных целей и исследования безопасности. Используйте ответственно в авторизованных средах.




1. Запусти kd на хосте
2. Потом загружай/перезагружай VM
3. kd ловит initial break → g → VM грузится дальше

D:\!GITLOCAL\PsyShoutToolsBundle\bin\Release;srv*C:\Symbols*https://msdl.microsoft.com/download/symbols

srv*C:\Symbols*https://msdl.microsoft.com/download/symbols