# KernelFlirt -- Полный справочник по скриптингу (C# REPL)

> **KernelFlirt** -- ядерный отладчик (kernel debugger) для Windows x64.  
> Скриптинг-плагин дает полный доступ к API отладчика через **C# REPL** на базе Roslyn.  
> Переменные и состояние сохраняются между запусками скриптов в рамках сессии.  
> Горячие клавиши: **F5** / **Ctrl+Enter** -- выполнить код; выделите фрагмент и нажмите F5, чтобы выполнить только его.

---

## Оглавление

1. [Быстрый старт](#1-быстрый-старт)
2. [Шорткаты (глобальные хелперы)](#2-шорткаты-глобальные-хелперы)
3. [Состояние отладчика (IDebuggerApi)](#3-состояние-отладчика-idebuggerapi)
4. [Память и регистры (IMemoryApi)](#4-память-и-регистры-imemoryapi)
5. [Точки останова (IBreakpointApi)](#5-точки-останова-ibreakpointapi)
6. [Символы и модули (ISymbolApi)](#6-символы-и-модули-isymbolapi)
7. [Процессы и потоки (IProcessApi)](#7-процессы-и-потоки-iprocessapi)
8. [Пользовательский интерфейс (IUiApi)](#8-пользовательский-интерфейс-iuiapi)
9. [Логирование (ILogApi)](#9-логирование-ilogapi)
10. [Управление выполнением](#10-управление-выполнением)
11. [События отладчика](#11-события-отладчика)
12. [Модели данных](#12-модели-данных)
13. [Обход антиотладки](#13-обход-антиотладки)
14. [Именование функций и аннотации](#14-именование-функций-и-аннотации)
15. [Анализ PE-файлов](#15-анализ-pe-файлов)
16. [Работа со стеком](#16-работа-со-стеком)
17. [Расшифровка и декодирование строк](#17-расшифровка-и-декодирование-строк)
18. [Восстановление IAT](#18-восстановление-iat)
19. [Распаковка (Unpacking)](#19-распаковка-unpacking)
20. [Сканирование и поиск в памяти](#20-сканирование-и-поиск-в-памяти)
21. [Декомпиляция](#21-декомпиляция)
22. [Файловый ввод-вывод](#22-файловый-ввод-вывод)
23. [Межплагинное взаимодействие](#23-межплагинное-взаимодействие)
24. [Рецепты (15+ готовых сценариев)](#24-рецепты)
25. [Советы и подводные камни](#25-советы-и-подводные-камни)
26. [REPL -- особенности и приёмы](#26-repl--особенности-и-приёмы)

---

## 1. Быстрый старт

### Открытие консоли

Скриптинг-вкладка появляется автоматически при загрузке плагина **Scripting**. Если вкладка не видна, убедитесь, что файл `ScriptingPlugin.dll` лежит в каталоге плагинов.

### Первый скрипт

```csharp
// Проверяем подключение
print($"Подключен: {api.IsConnected}");
print($"Остановлен: {api.IsBreakState}");
print($"PID: {api.TargetPid}");
print($"TID: {api.SelectedThreadId}");
```

### Минимальный воркфлоу

```csharp
// 1. Смотрим загруженные модули
foreach (var m in api.Symbols.GetModules())
    print($"0x{m.BaseAddress:X016}  {m.Size,8:X}  {m.Name}");

// 2. Читаем регистры
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-4} = 0x{r.Value:X016}");

// 3. Читаем 32 байта по RIP
var code = ReadMem(Reg("RIP"), 32);
print(BitConverter.ToString(code));
```

### Автоматические импорты

Следующие пространства имён импортированы автоматически, дополнительные `using` не нужны:

```
System
System.Collections.Generic
System.IO
System.Linq
System.Text
System.Threading.Tasks
KernelFlirt.SDK
```

---

## 2. Шорткаты (глобальные хелперы)

Шорткаты -- это предопределённые лямбды и переменные, которые инжектируются в REPL-сессию при первом запуске скрипта. Они упрощают частые операции, избавляя от необходимости каждый раз вводить полные вызовы API.

### Таблица шорткатов

| Шорткат | Сигнатура | Возвращает | Описание |
|---------|-----------|------------|----------|
| `api` | -- | `IDebuggerApi` | Корневой объект API отладчика. Через него доступны все подсистемы. |
| `print(text)` | `Action<string>` | `void` | Вывести текст в output-панель скриптинга. |
| `ReadMem(addr, size)` | `Func<ulong, uint, byte[]>` | `byte[]?` | Прочитать `size` байт из памяти отлаживаемого процесса по адресу `addr`. Использует `api.TargetPid` автоматически. |
| `WriteMem(addr, data)` | `Func<ulong, byte[], bool>` | `bool` | Записать массив байт `data` по адресу `addr`. Использует `api.TargetPid`. |
| `ReadString(addr, maxLen)` | `Func<ulong, int, string>` | `string` | Прочитать ASCII-строку (null-terminated) по адресу. `maxLen` -- максимальное количество байт для чтения (по умолчанию указывать явно). Если чтение не удалось, возвращает `"<read failed>"`. |
| `ReadWString(addr, maxLen)` | `Func<ulong, int, string>` | `string` | Прочитать Unicode (UTF-16LE) строку. `maxLen` -- максимальное количество **символов** (байт будет `maxLen * 2`). |
| `ReadPtr(addr)` | `Func<ulong, ulong>` | `ulong` | Прочитать указатель: 8 байт на x64, 4 байта на x86 (WoW64). Автоматически определяет разрядность через `api.Is32Bit`. |
| `ReadU32(addr)` | `Func<ulong, uint>` | `uint` | Прочитать 4 байта как `uint32` (little-endian). |
| `ReadU64(addr)` | `Func<ulong, ulong>` | `ulong` | Прочитать 8 байт как `uint64` (little-endian). |
| `Reg(name)` | `Func<string, ulong>` | `ulong` | Получить значение регистра по имени (регистронезависимо). Примеры: `Reg("RAX")`, `Reg("rsp")`, `Reg("R8")`. |
| `Sym(addr)` | `Func<ulong, string>` | `string?` | Разрешить адрес в имя символа. Возвращает `null`, если символ не найден. Формат: `"module!function+0xOffset"`. |
| `Addr(name)` | `Func<string, ulong>` | `ulong` | Разрешить имя символа в адрес. Формат: `"module!function"` или `"function"`. Возвращает `0`, если символ не найден. |

### Как работают шорткаты внутри

Шорткаты определены как локальные переменные типа `Func<...>` в преамбуле скрипта (см. `ScriptEngine.cs`). Они инжектируются один раз при первом запуске и живут в REPL-состоянии до вызова **Reset State**.

```csharp
// Так определён ReadMem внутри:
Func<ulong, uint, byte[]> ReadMem = (addr, size) =>
    api.Memory.ReadMemory(api.TargetPid, addr, size);
```

### Примеры использования шорткатов

```csharp
// Прочитать 16 байт по текущему RIP
var bytes = ReadMem(Reg("RIP"), 16);
print(BitConverter.ToString(bytes));
// => "48-89-5C-24-08-48-89-6C-24-10-48-89-74-24-18"

// Прочитать ASCII-строку по указателю в RCX
var s = ReadString(Reg("RCX"), 256);
print(s);
// => "C:\Windows\System32\ntdll.dll"

// Прочитать Unicode-строку по указателю в RDX
var ws = ReadWString(Reg("RDX"), 260);
print(ws);

// Прочитать цепочку указателей (разыменование)
var ptr1 = ReadPtr(Reg("RCX"));
var ptr2 = ReadPtr(ptr1 + 0x10);
var ptr3 = ReadPtr(ptr2 + 0x08);
print($"0x{ptr3:X016}  =>  {Sym(ptr3) ?? "???"}");

// Значение из DWORD-поля структуры
var flags = ReadU32(Reg("RCX") + 0x2C);
print($"Flags: 0x{flags:X08}");

// Быстрая проверка символа
print(Sym(Reg("RIP")));
// => "ntdll!LdrInitializeThunk+0x14"

// Адрес экспортированной функции
var createFile = Addr("kernel32!CreateFileW");
print($"CreateFileW @ 0x{createFile:X016}");
```

---

## 3. Состояние отладчика (IDebuggerApi)

Интерфейс `IDebuggerApi` -- корневой объект API, доступный как глобальная переменная `api`. Предоставляет свойства состояния отладчика и ссылки на все подсистемы.

### Свойства

| Свойство | Тип | Описание |
|----------|-----|----------|
| `api.IsConnected` | `bool` | `true`, если отладчик подключён к целевой системе. |
| `api.IsBreakState` | `bool` | `true`, если процесс остановлен (break state). Скрипты, читающие/пишущие память, должны проверять это свойство. |
| `api.TargetPid` | `uint` | PID отлаживаемого процесса. Используется в большинстве вызовов `IMemoryApi`. |
| `api.SelectedThreadId` | `uint` | TID выбранного потока. Используется для чтения регистров. |
| `api.Is32Bit` | `bool` | `true`, если отлаживаемый процесс 32-битный (WoW64). Влияет на размер указателя в `ReadPtr`. |

### Подсистемы

| Свойство | Тип | Описание |
|----------|-----|----------|
| `api.Memory` | `IMemoryApi` | Чтение/запись памяти, регистры, аллокация. |
| `api.Breakpoints` | `IBreakpointApi` | Точки останова (software, hardware, memory). |
| `api.Symbols` | `ISymbolApi` | Символы, модули, именование функций. |
| `api.Process` | `IProcessApi` | Перечисление процессов/потоков, anti-debug. |
| `api.Log` | `ILogApi` | Логирование (Info/Warning/Error). |
| `api.UI` | `IUiApi` | Навигация, аннотации, декомпиляция, панели. |

### Примеры

```csharp
// Полная диагностика состояния отладчика
print($"=== Состояние отладчика ===");
print($"Подключён:      {api.IsConnected}");
print($"Break state:    {api.IsBreakState}");
print($"Target PID:     {api.TargetPid}");
print($"Selected TID:   {api.SelectedThreadId}");
print($"32-bit процесс: {api.Is32Bit}");

if (api.IsBreakState)
{
    var rip = Reg("RIP");
    print($"RIP:            0x{rip:X016}");
    print($"Символ:         {Sym(rip) ?? "<неизвестен>"}");
}
```

```csharp
// Безопасное чтение памяти с проверкой состояния
if (!api.IsConnected)
{
    print("ОШИБКА: отладчик не подключён!");
}
else if (!api.IsBreakState)
{
    print("ОШИБКА: процесс выполняется, сначала нажмите Pause (F12)!");
}
else
{
    var data = ReadMem(Reg("RIP"), 64);
    if (data == null)
        print("ОШИБКА: не удалось прочитать память");
    else
        print($"Прочитано {data.Length} байт");
}
```

---

## 4. Память и регистры (IMemoryApi)

Интерфейс `IMemoryApi` (доступен как `api.Memory`) предоставляет низкоуровневый доступ к памяти отлаживаемого процесса, регистрам CPU, управлению защитой страниц и аллокации виртуальной памяти.

### Методы

#### `ReadMemory`

```csharp
byte[]? ReadMemory(uint pid, ulong address, uint size)
```

Читает `size` байт из адресного пространства процесса `pid`, начиная с адреса `address`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса (обычно `api.TargetPid`). |
| `address` | `ulong` | Виртуальный адрес начала чтения. |
| `size` | `uint` | Количество байт для чтения. |

**Возвращает:** `byte[]?` -- массив прочитанных байт или `null` при ошибке (невалидный адрес, страница не замаплена, процесс недоступен).

**Заметки:**
- Максимальный размер чтения ограничен буфером драйвера (обычно 64 KB за один вызов). Для больших блоков читайте частями.
- Для ядерных адресов используйте `pid = 4` (System) или `pid = api.TargetPid`.

---

#### `WriteMemory`

```csharp
bool WriteMemory(uint pid, ulong address, byte[] data)
```

Записывает массив `data` в адресное пространство процесса `pid` по адресу `address`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `address` | `ulong` | Виртуальный адрес начала записи. |
| `data` | `byte[]` | Данные для записи. |

**Возвращает:** `bool` -- `true` при успешной записи, `false` при ошибке.

**Заметки:**
- Запись в страницы с защитой `PAGE_EXECUTE_READ` или `PAGE_READONLY` завершится ошибкой. Используйте `ProtectMemory` для временного изменения защиты.
- KernelFlirt использует ядерное чтение/запись -- стандартные пользовательские защиты (PAGE_NOACCESS) преодолеваются.

---

#### `ReadRegisters`

```csharp
IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid)
```

Читает все регистры указанного потока.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `tid` | `uint` | Идентификатор потока. |

**Возвращает:** `IReadOnlyList<PluginRegister>` -- список регистров (RAX, RBX, ..., RIP, RSP, RFLAGS, сегментные регистры, DR0-DR7 и флаги).

Каждый `PluginRegister` содержит:
- `Name` (string) -- имя регистра ("RAX", "RIP", "CF", ...)
- `Value` (ulong) -- значение
- `IsFlag` (bool) -- `true` для флагов (CF, ZF, SF, OF, ...)

---

#### `WriteRip`

```csharp
bool WriteRip(uint pid, uint tid, ulong newRip)
```

Изменяет значение регистра RIP (instruction pointer) для указанного потока.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `tid` | `uint` | Идентификатор потока. |
| `newRip` | `ulong` | Новое значение RIP. |

**Возвращает:** `bool` -- `true` при успехе.

**Важно:** Изменение RIP без корректировки стека может привести к краху процесса. Для одновременного изменения RIP и RSP используйте `WriteRipAndRsp`.

---

#### `WriteRipAndRsp`

```csharp
bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp)
```

Атомарно изменяет RIP и RSP для указанного потока.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока. **Обратите внимание:** в этом методе нет параметра `pid` (в отличие от `WriteRip`). |
| `newRip` | `ulong` | Новое значение RIP. |
| `newRsp` | `ulong` | Новое значение RSP. |

**Возвращает:** `bool` -- `true` при успехе.

**Типичное использование:** перенаправление выполнения из обработчика IAT-тремполина, восстановление контекста после перехвата.

---

#### `ProtectMemory`

```csharp
(bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
```

Изменяет защиту страниц памяти (аналог `VirtualProtectEx`).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `address` | `ulong` | Адрес начала региона (выравнивается по странице). |
| `size` | `uint` | Размер региона в байтах. |
| `newProtection` | `uint` | Новая защита. Константы: `0x02` = `PAGE_READONLY`, `0x04` = `PAGE_READWRITE`, `0x10` = `PAGE_EXECUTE`, `0x20` = `PAGE_EXECUTE_READ`, `0x40` = `PAGE_EXECUTE_READWRITE`. |

**Возвращает:** кортеж `(bool ok, uint oldProtection)` -- успех и предыдущее значение защиты.

---

#### `AllocateMemory`

```csharp
ulong AllocateMemory(uint pid, ulong size)
```

Выделяет виртуальную память в адресном пространстве процесса (MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `size` | `ulong` | Размер блока в байтах. |

**Возвращает:** `ulong` -- адрес выделенного блока, или `0` при ошибке.

---

#### `FreeMemory`

```csharp
bool FreeMemory(uint pid, ulong address)
```

Освобождает ранее выделенный блок памяти.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `address` | `ulong` | Адрес блока, полученный из `AllocateMemory`. |

**Возвращает:** `bool` -- `true` при успехе.

### Примеры: Память и регистры

```csharp
// Показать все общие регистры (без флагов)
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-6} = 0x{r.Value:X016}");
```

```csharp
// Показать только флаги
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
var flags = regs.Where(r => r.IsFlag && r.Value != 0)
    .Select(r => r.Name);
print("Установленные флаги: " + string.Join(", ", flags));
```

```csharp
// Прочитать 256 байт и вывести hexdump
var addr = Reg("RSP");
var data = ReadMem(addr, 256);
for (int i = 0; i < data.Length; i += 16)
{
    var hex = BitConverter.ToString(data, i, Math.Min(16, data.Length - i))
        .Replace("-", " ");
    var ascii = new string(data.Skip(i).Take(16)
        .Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
    print($"0x{addr + (ulong)i:X016}  {hex,-48}  {ascii}");
}
```

```csharp
// Записать NOP-ы (5 штук) по адресу
var target = Reg("RIP") + 0x10;

// Сохраняем оригинальные байты
var original = ReadMem(target, 5);
print($"Оригинал: {BitConverter.ToString(original)}");

// Патчим
WriteMem(target, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
print("Записано 5 NOP-ов");
```

```csharp
// Изменить защиту страницы для записи в .text секцию
var textBase = api.Symbols.GetModules()[0].BaseAddress + 0x1000UL;
var (ok, oldProt) = api.Memory.ProtectMemory(api.TargetPid, textBase, 0x1000, 0x40);
if (ok)
{
    print($"Старая защита: 0x{oldProt:X}, новая: PAGE_EXECUTE_READWRITE");
    WriteMem(textBase + 0x100, new byte[] { 0xC3 }); // RET
    // Восстанавливаем защиту
    api.Memory.ProtectMemory(api.TargetPid, textBase, 0x1000, oldProt);
}
```

```csharp
// Выделить память, записать шеллкод, перенаправить RIP
var shellcode = new byte[] {
    0x48, 0xC7, 0xC0, 0x01, 0x00, 0x00, 0x00,  // mov rax, 1
    0xC3                                          // ret
};
var mem = api.Memory.AllocateMemory(api.TargetPid, 4096);
if (mem != 0)
{
    WriteMem(mem, shellcode);
    print($"Шеллкод записан @ 0x{mem:X016}");

    // Перенаправляем выполнение
    api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, mem);
    print("RIP перенаправлен на шеллкод");
}
```

```csharp
// Перенаправить RIP + RSP одновременно (для IAT-трассировки)
var newRip = Addr("kernel32!CreateFileW");
var newRsp = Reg("RSP") + 8; // убираем return address
api.Memory.WriteRipAndRsp(api.SelectedThreadId, newRip, newRsp);
```

---

## 5. Точки останова (IBreakpointApi)

Интерфейс `IBreakpointApi` (доступен как `api.Breakpoints`) управляет точками останова всех типов: программные (INT3), аппаратные (DR0-DR3), и memory breakpoints (PAGE_GUARD).

### Методы

#### `SetBreakpoint`

```csharp
uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1)
```

Устанавливает точку останова.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |
| `tid` | `uint` | Идентификатор потока. Для software BP обычно `0` (все потоки). Для hardware BP указывает конкретный поток (DR-регистры per-thread). |
| `address` | `ulong` | Адрес точки останова. |
| `type` | `PluginBreakpointType` | Тип точки останова (см. перечисление ниже). |
| `length` | `uint` | Длина отслеживаемого региона для hardware watchpoints (1, 2, 4 или 8 байт). По умолчанию `1`. Игнорируется для software BP. |

**Возвращает:** `uint?` -- handle точки останова для последующего удаления, или `null` при ошибке.

---

#### `RemoveBreakpoint`

```csharp
bool RemoveBreakpoint(uint handle)
```

Удаляет точку останова по её handle.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `handle` | `uint` | Handle, полученный из `SetBreakpoint`. |

**Возвращает:** `bool` -- `true` при успешном удалении.

---

#### `GetAll`

```csharp
IReadOnlyList<PluginBreakpoint> GetAll()
```

Возвращает список всех установленных точек останова.

**Возвращает:** `IReadOnlyList<PluginBreakpoint>` -- коллекция с информацией о каждой точке останова (handle, адрес, тип, состояние, количество срабатываний, оригинальный байт).

---

#### `ToggleBreakpoint`

```csharp
void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software)
```

Переключает точку останова через UI. Если по адресу уже есть BP -- удаляет его. Если нет -- добавляет. Обновляет список BP, маркеры в дизассемблере и драйвер.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес точки останова. |
| `type` | `PluginBreakpointType` | Тип (по умолчанию `Software`). |

### Типы точек останова (PluginBreakpointType)

| Значение | Имя | Описание |
|----------|-----|----------|
| `0` | `Software` | Программная BP (INT3). Подменяет первый байт инструкции на `0xCC`. Неограниченное количество. |
| `1` | `Hardware` | Аппаратная BP на выполнение (DR0-DR3). Максимум 4 штуки на поток. Не модифицирует код. |
| `2` | `HwWrite` | Аппаратный watchpoint на запись. Срабатывает при записи в указанный адрес. |
| `3` | `HwReadWrite` | Аппаратный watchpoint на чтение/запись. Срабатывает при любом обращении к адресу. |
| `4` | `Memory` | Memory BP (PAGE_GUARD). Срабатывает при доступе к странице. Покрывает всю страницу (4 KB). |

### Примеры: Точки останова

```csharp
// Установить software BP на функцию
var h = api.Breakpoints.SetBreakpoint(
    api.TargetPid, 0,
    Addr("kernel32!CreateFileW"),
    PluginBreakpointType.Software);
print($"BP handle: {h}");
```

```csharp
// Установить hardware watchpoint на запись (4 байта)
var dataAddr = Addr("myapp!g_GlobalFlag");
var h = api.Breakpoints.SetBreakpoint(
    api.TargetPid,
    api.SelectedThreadId,
    dataAddr,
    PluginBreakpointType.HwWrite,
    4);    // отслеживать запись в 4 байта
print($"HW write watchpoint: {h}");
```

```csharp
// Показать все активные BP
foreach (var bp in api.Breakpoints.GetAll())
{
    print($"[{bp.Handle}] 0x{bp.Address:X016}  {bp.Type,-12}  " +
          $"Enabled={bp.Enabled}  Hits={bp.HitCount}  " +
          $"OrigByte=0x{bp.OriginalByte:X02}");
}
```

```csharp
// Удалить все точки останова
foreach (var bp in api.Breakpoints.GetAll())
{
    api.Breakpoints.RemoveBreakpoint(bp.Handle);
    print($"Удалён BP @ 0x{bp.Address:X016}");
}
```

```csharp
// Toggle BP через UI (если есть -- удалит, если нет -- поставит)
api.Breakpoints.ToggleBreakpoint(Reg("RIP"));
```

```csharp
// Логирующий бряк: записать аргументы CreateFileW при каждом вызове
var target = Addr("kernel32!CreateFileW");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != target) return false;

    // RCX = lpFileName (первый аргумент в x64 calling convention)
    var fileName = ReadWString(Reg("RCX"), 260);
    var access   = ReadU32(Reg("RSP") + 0x28); // dwDesiredAccess (5-й аргумент, на стеке)
    print($"CreateFileW(\"{fileName}\", 0x{access:X08})");

    api.Continue();
    return true; // подавить показ в UI, автопродолжение
};
```

```csharp
// Условный бряк: остановиться только когда RAX == 0
var addr = api.Symbols.GetModules()[0].BaseAddress + 0x1234UL;
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != addr) return false;
    if (Reg("RAX") != 0)
    {
        api.Continue(); // RAX != 0, пропускаем
        return true;
    }
    print($"RAX == 0! Остановлены @ 0x{addr:X016}");
    return false; // показать в UI
};
```

```csharp
// Memory breakpoint на секцию данных
var mod = api.Symbols.GetModules()[0];
var dataSection = mod.BaseAddress + 0x5000UL; // условный адрес .data
api.Breakpoints.SetBreakpoint(
    api.TargetPid, 0, dataSection,
    PluginBreakpointType.Memory);
print("Memory BP установлен на .data секцию");
```

---

## 6. Символы и модули (ISymbolApi)

Интерфейс `ISymbolApi` (доступен как `api.Symbols`) предоставляет доступ к таблице символов, перечислению загруженных модулей (user-mode и kernel-mode), а также именованию пользовательских функций.

### Методы

#### `ResolveAddress`

```csharp
string? ResolveAddress(ulong address)
```

Разрешает виртуальный адрес в символьное имя.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Виртуальный адрес. |

**Возвращает:** `string?` -- имя символа в формате `"module!function+0xOffset"` или `null`, если символ не найден.

**Заметки:**
- Также возвращает имена, зарегистрированные через `RegisterFunction`.
- Для адресов внутри зарегистрированной функции возвращает `"name+0xOffset"`.

---

#### `ResolveNameToAddress`

```csharp
ulong ResolveNameToAddress(string name)
```

Разрешает символьное имя в виртуальный адрес.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `name` | `string` | Имя символа. Формат: `"module!function"` или `"function"`. |

**Возвращает:** `ulong` -- виртуальный адрес символа. `0`, если символ не найден.

---

#### `GetModules`

```csharp
IReadOnlyList<PluginModuleInfo> GetModules()
```

Возвращает список загруженных user-mode модулей (DLL/EXE) целевого процесса.

**Возвращает:** `IReadOnlyList<PluginModuleInfo>` -- список модулей (BaseAddress, Size, Name).

---

#### `GetKernelModules`

```csharp
IReadOnlyList<PluginKernelModuleInfo> GetKernelModules()
```

Возвращает список загруженных модулей ядра (ntoskrnl, драйверы).

**Возвращает:** `IReadOnlyList<PluginKernelModuleInfo>` -- список ядерных модулей (BaseAddress, Size, LoadOrder, Name).

---

#### `RegisterFunction`

```csharp
void RegisterFunction(ulong address, string? name, uint size = 0)
```

Регистрирует пользовательское имя функции по указанному адресу.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес начала функции. |
| `name` | `string?` | Имя функции. Если `null` -- удаляет ранее зарегистрированное имя. |
| `size` | `uint` | Размер функции в байтах. Если `> 0`, `ResolveAddress` будет возвращать `"name+0xOffset"` для адресов в диапазоне `[address, address + size)`. **Рекомендуется всегда указывать размер.** По умолчанию `0`. |

**Заметки:**
- Зарегистрированные имена имеют приоритет над системными символами.
- Используйте для именования функций в анализируемом коде (как IDA Pro "Rename function").
- Без указания `size` разрешение будет работать только для точного адреса.

---

#### `GetRegisteredFunctions`

```csharp
IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions()
```

Возвращает список всех пользовательских функций, зарегистрированных через `RegisterFunction`.

**Возвращает:** `IReadOnlyList<PluginFunctionEntry>` -- список записей (Address, Name, Size).

### Примеры: Символы и модули

```csharp
// Показать все user-mode модули
foreach (var m in api.Symbols.GetModules())
    print($"0x{m.BaseAddress:X016}  {m.Size,10:X}  {m.Name}");
```

```csharp
// Показать ядерные модули
foreach (var km in api.Symbols.GetKernelModules())
    print($"[{km.LoadOrder,3}]  0x{km.BaseAddress:X016}  {km.Size,10:X}  {km.Name}");
```

```csharp
// Найти модуль по имени
var ntdll = api.Symbols.GetModules()
    .FirstOrDefault(m => m.Name.Contains("ntdll", StringComparison.OrdinalIgnoreCase));
if (ntdll != null)
    print($"ntdll @ 0x{ntdll.BaseAddress:X016}, размер: 0x{ntdll.Size:X}");
```

```csharp
// Именовать функцию с размером
api.Symbols.RegisterFunction(0x7FF6A0001000, "DecryptPayload", 0x150);
print(Sym(0x7FF6A0001000));    // => "DecryptPayload"
print(Sym(0x7FF6A0001050));    // => "DecryptPayload+0x50"
```

```csharp
// Именовать несколько функций из IDA-экспорта
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
var functions = new Dictionary<uint, (string name, uint size)>
{
    { 0x1000, ("main", 0x230) },
    { 0x1230, ("InitConfig", 0x80) },
    { 0x12B0, ("ParseArgs", 0x110) },
    { 0x13C0, ("DecryptString", 0x90) },
    { 0x1450, ("CheckLicense", 0x1A0) },
};
foreach (var (rva, (name, size)) in functions)
{
    api.Symbols.RegisterFunction(baseAddr + rva, name, size);
    print($"  0x{rva:X04} => {name} (size=0x{size:X})");
}
print($"Зарегистрировано {functions.Count} функций");
```

```csharp
// Показать все зарегистрированные функции
foreach (var f in api.Symbols.GetRegisteredFunctions())
    print($"0x{f.Address:X016}  {f.Name,-30}  size=0x{f.Size:X}");
```

```csharp
// Удалить имя функции
api.Symbols.RegisterFunction(0x7FF6A0001000, null);
print("Имя удалено");
```

```csharp
// Поиск модуля ядра по имени
var win32k = api.Symbols.GetKernelModules()
    .FirstOrDefault(m => m.Name.Contains("win32k", StringComparison.OrdinalIgnoreCase));
if (win32k != null)
    print($"win32k @ 0x{win32k.BaseAddress:X016}  size=0x{win32k.Size:X}");
```

---

## 7. Процессы и потоки (IProcessApi)

Интерфейс `IProcessApi` (доступен как `api.Process`) предоставляет перечисление процессов и потоков, управление потоками, а также методы обхода антиотладочных проверок.

### Методы

#### `EnumProcesses`

```csharp
IReadOnlyList<PluginProcessInfo> EnumProcesses()
```

Перечисляет все процессы в системе (аналог `NtQuerySystemInformation(SystemProcessInformation)`).

**Возвращает:** `IReadOnlyList<PluginProcessInfo>` -- список процессов (ProcessId, SessionId, Name).

---

#### `EnumThreads`

```csharp
IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid)
```

Перечисляет все потоки процесса.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |

**Возвращает:** `IReadOnlyList<PluginThreadInfo>` -- список потоков (ThreadId, StartAddress, State, Priority).

---

#### `SuspendThread`

```csharp
bool SuspendThread(uint tid)
```

Приостанавливает поток (увеличивает suspend count).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока. |

**Возвращает:** `bool` -- `true` при успехе.

---

#### `ResumeThread`

```csharp
bool ResumeThread(uint tid)
```

Возобновляет поток (уменьшает suspend count).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока. |

**Возвращает:** `bool` -- `true` при успехе.

---

#### `GetPebAddress`

```csharp
(ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid)
```

Получает адрес PEB (Process Environment Block) процесса.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |

**Возвращает:** кортеж `(ulong PebAddress, ulong Peb32Address)`:
- `PebAddress` -- адрес 64-битного PEB.
- `Peb32Address` -- адрес 32-битного PEB (для WoW64-процессов). `0` для нативных 64-битных процессов.

---

#### `ClearDebugPort`

```csharp
bool ClearDebugPort(uint pid)
```

Обнуляет поле `DebugPort` в структуре `EPROCESS` ядра. Это скрывает факт отладки от `NtQueryInformationProcess(ProcessDebugPort)` и `CheckRemoteDebuggerPresent`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |

**Возвращает:** `bool` -- `true` при успехе.

---

#### `ClearThreadHide`

```csharp
bool ClearThreadHide(uint pid)
```

Снимает флаг `ThreadHideFromDebugger` со всех потоков процесса. Антиотладочная техника: после вызова `NtSetInformationThread(ThreadHideFromDebugger)` исключения в потоке не доставляются отладчику.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. |

**Возвращает:** `bool` -- `true` при успехе.

---

#### `InstallNtQsiHook`

```csharp
bool InstallNtQsiHook()
```

Устанавливает хук на `NtQuerySystemInformation` в ядре. Скрывает целевой процесс из списка процессов (от `Task Manager`, `Process Explorer` и подобных).

**Возвращает:** `bool` -- `true` при успешной установке.

---

#### `RemoveNtQsiHook`

```csharp
bool RemoveNtQsiHook()
```

Удаляет хук `NtQuerySystemInformation`, восстанавливая оригинальное поведение.

**Возвращает:** `bool` -- `true` при успешном удалении.

---

#### `ProbeNtQsiHook`

```csharp
string ProbeNtQsiHook()
```

Проверяет текущее состояние хука `NtQuerySystemInformation`.

**Возвращает:** `string` -- строка с описанием статуса хука.

---

#### `SetSpoofSharedUserData`

```csharp
bool SetSpoofSharedUserData(bool enable)
```

Включает/выключает подмену `KUSER_SHARED_DATA`. Скрывает отладочные маркеры (`KdDebuggerEnabled`, `KernelDebugger` поля) от user-mode проверок.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `enable` | `bool` | `true` -- включить подмену, `false` -- выключить. |

**Возвращает:** `bool` -- `true` при успехе.

### Примеры: Процессы и потоки

```csharp
// Список процессов
foreach (var p in api.Process.EnumProcesses())
    print($"[{p.ProcessId,5}]  Session={p.SessionId}  {p.Name}");
```

```csharp
// Потоки целевого процесса
foreach (var t in api.Process.EnumThreads(api.TargetPid))
    print($"TID={t.ThreadId,6}  Start=0x{t.StartAddress:X016}  " +
          $"State={t.State}  Priority={t.Priority}");
```

```csharp
// Найти процесс по имени
var target = api.Process.EnumProcesses()
    .FirstOrDefault(p => p.Name.Contains("notepad", StringComparison.OrdinalIgnoreCase));
if (target != null)
    print($"Найден: PID={target.ProcessId}  {target.Name}");
```

```csharp
// Заморозить все потоки кроме главного
var threads = api.Process.EnumThreads(api.TargetPid);
var mainThread = threads.OrderBy(t => t.ThreadId).First();
foreach (var t in threads.Where(t => t.ThreadId != mainThread.ThreadId))
{
    api.Process.SuspendThread(t.ThreadId);
    print($"Приостановлен TID={t.ThreadId}");
}
```

```csharp
// Прочитать PEB
var (peb, peb32) = api.Process.GetPebAddress(api.TargetPid);
print($"PEB   = 0x{peb:X016}");
print($"PEB32 = 0x{peb32:X016}");

// Прочитать ImageBaseAddress из PEB (смещение 0x10)
var imageBase = ReadPtr(peb + 0x10);
print($"ImageBase = 0x{imageBase:X016}");

// Прочитать BeingDebugged из PEB (смещение 0x02)
var beingDebugged = ReadMem(peb + 2, 1);
print($"BeingDebugged = {beingDebugged[0]}");
```

```csharp
// Полный anti-debug bypass
api.Process.ClearDebugPort(api.TargetPid);
api.Process.ClearThreadHide(api.TargetPid);
api.Process.SetSpoofSharedUserData(true);
print("Anti-debug bypass активирован");
```

---

## 8. Пользовательский интерфейс (IUiApi)

Интерфейс `IUiApi` (доступен как `api.UI`) управляет визуальными элементами отладчика: навигация по дизассемблеру, аннотации, декомпиляция, панели плагинов, и данные для межплагинного взаимодействия.

### Методы

#### `NavigateDisassembly`

```csharp
void NavigateDisassembly(ulong address)
```

Переходит в дизассемблере к указанному адресу.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес для навигации. |

---

#### `DisasmGoBack`

```csharp
void DisasmGoBack()
```

Возвращается к предыдущему адресу навигации (undo для `NavigateDisassembly`).

---

#### `AddMenuItem`

```csharp
void AddMenuItem(string header, Action callback)
```

Добавляет пункт меню в главное меню отладчика.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `header` | `string` | Текст пункта меню. |
| `callback` | `Action` | Действие при нажатии. |

---

#### `AddToolPanel`

```csharp
void AddToolPanel(string title, object wpfContent)
```

Добавляет новую панель инструментов (вкладку) в интерфейс.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `title` | `string` | Заголовок вкладки. |
| `wpfContent` | `object` | WPF UserControl для содержимого вкладки. |

---

#### `AddUnpackedModule`

```csharp
void AddUnpackedModule(ulong peBase, string name)
```

Добавляет динамически распакованный PE-модуль в список и обновляет все связанные view (секции, импорты, строки, функции).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `peBase` | `ulong` | Базовый адрес PE в памяти процесса. |
| `name` | `string` | Имя модуля для отображения. |

---

#### `RefreshModulesAndSections`

```csharp
void RefreshModulesAndSections()
```

Принудительно обновляет список модулей и вкладку секций.

---

#### `AddModuleSections`

```csharp
void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections)
```

Предоставляет информацию о секциях модуля напрямую (без парсинга PE-заголовка). Используйте, когда пакер обнулил PE-заголовок (anti-dump).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `moduleName` | `string` | Имя модуля (должно совпадать с именем в списке модулей). |
| `sections` | `IReadOnlyList<PluginSectionInfo>` | Список секций. |

---

#### `DecompileFunction`

```csharp
void DecompileFunction(ulong address)
```

Запускает декомпиляцию функции по указанному адресу (через RetDec). Операция асинхронная.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес функции для декомпиляции. |

**Заметки:** Результат появится во вкладке декомпилятора. Для получения текста из скрипта вызовите `GetDecompiledCode()` с задержкой.

---

#### `GetDecompiledCode`

```csharp
string GetDecompiledCode()
```

Возвращает текущий декомпилированный код (C-псевдокод от RetDec).

**Возвращает:** `string` -- текст декомпиляции. Пустая строка, если декомпиляция не выполнялась.

---

#### `SetAddressAnnotation`

```csharp
void SetAddressAnnotation(ulong address, string? annotation)
```

Устанавливает текстовую аннотацию (комментарий) для адреса. Отображается как `"; comment"` в дизассемблере.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес. |
| `annotation` | `string?` | Текст комментария. `null` или пустая строка -- удаляет аннотацию. |

---

#### `GetAddressAnnotation`

```csharp
string? GetAddressAnnotation(ulong address)
```

Возвращает аннотацию для адреса.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес. |

**Возвращает:** `string?` -- текст аннотации или `null`, если аннотации нет.

---

#### `GetAllAnnotations`

```csharp
IReadOnlyDictionary<ulong, string> GetAllAnnotations()
```

Возвращает все аннотации в виде словаря адрес-текст.

**Возвращает:** `IReadOnlyDictionary<ulong, string>`.

---

#### `RefreshDisassembly`

```csharp
void RefreshDisassembly()
```

Обновляет view дизассемблера для отображения изменённых аннотаций.

---

#### `SetPluginData`

```csharp
void SetPluginData(string key, object? value)
```

Сохраняет произвольные данные под строковым ключом. Данные хранятся в памяти и доступны из любого плагина.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `key` | `string` | Строковый ключ. |
| `value` | `object?` | Значение (любой объект). `null` -- удаляет данные. |

---

#### `GetPluginData`

```csharp
object? GetPluginData(string key)
```

Получает данные, сохранённые через `SetPluginData`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `key` | `string` | Строковый ключ. |

**Возвращает:** `object?` -- сохранённое значение или `null`.

---

#### События UI

```csharp
event Action<ulong, string>? OnNoteAdded;    // Пользователь добавил заметку через контекстное меню
event Action<ulong, string>? OnNoteEdited;   // Пользователь отредактировал заметку
event Action<ulong>?         OnNoteRemoved;  // Пользователь удалил заметку
```

### Примеры: Пользовательский интерфейс

```csharp
// Перейти к точке входа главного модуля
var main = api.Symbols.GetModules()[0];
var ep = main.BaseAddress + ReadU32(main.BaseAddress + 0x3C) + 0x28;
// Это наивный пример: AddressOfEntryPoint находится в PE-опциональном заголовке
var rvaEP = ReadU32(main.BaseAddress + ReadU32(main.BaseAddress + 0x3C) + 0x28);
api.UI.NavigateDisassembly(main.BaseAddress + rvaEP);
```

```csharp
// Добавить комментарий к текущему RIP
api.UI.SetAddressAnnotation(Reg("RIP"), "<<< Мы здесь >>>");
api.UI.RefreshDisassembly();
```

```csharp
// Экспортировать все аннотации в файл
var annotations = api.UI.GetAllAnnotations();
var lines = annotations.Select(kv => $"0x{kv.Key:X016}: {kv.Value}");
File.WriteAllLines(@"C:\Temp\annotations.txt", lines);
print($"Экспортировано {annotations.Count} аннотаций");
```

```csharp
// Импортировать аннотации из файла
foreach (var line in File.ReadAllLines(@"C:\Temp\annotations.txt"))
{
    var parts = line.Split(": ", 2);
    if (parts.Length == 2)
    {
        var addr = Convert.ToUInt64(parts[0].Trim(), 16);
        api.UI.SetAddressAnnotation(addr, parts[1]);
    }
}
api.UI.RefreshDisassembly();
print("Аннотации импортированы");
```

```csharp
// Декомпилировать функцию и вывести результат
api.UI.DecompileFunction(Reg("RIP"));
await Task.Delay(3000); // ждём декомпиляцию
var code = api.UI.GetDecompiledCode();
print(code);
```

```csharp
// Подписаться на события заметок
api.UI.OnNoteAdded += (addr, text) =>
    api.Log.Info($"[Note] Добавлена @ 0x{addr:X}: {text}");
api.UI.OnNoteEdited += (addr, text) =>
    api.Log.Info($"[Note] Изменена @ 0x{addr:X}: {text}");
api.UI.OnNoteRemoved += (addr) =>
    api.Log.Info($"[Note] Удалена @ 0x{addr:X}");
```

```csharp
// Межплагинное взаимодействие: сохранить и прочитать данные
api.UI.SetPluginData("myPlugin.lastOEP", Reg("RIP"));
// В другом скрипте или плагине:
var oep = (ulong?)api.UI.GetPluginData("myPlugin.lastOEP");
if (oep != null) print($"OEP = 0x{oep:X016}");
```

---

## 9. Логирование (ILogApi)

Интерфейс `ILogApi` (доступен как `api.Log`) -- простое API для вывода сообщений во вкладку **Log** главного окна отладчика.

### Методы

#### `Info`

```csharp
void Info(string message)
```

Выводит информационное сообщение.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `message` | `string` | Текст сообщения. |

---

#### `Warning`

```csharp
void Warning(string message)
```

Выводит предупреждение (жёлтый цвет).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `message` | `string` | Текст предупреждения. |

---

#### `Error`

```csharp
void Error(string message)
```

Выводит сообщение об ошибке (красный цвет).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `message` | `string` | Текст ошибки. |

### Разница между `print` и `api.Log`

| | `print(msg)` | `api.Log.Info(msg)` |
|--|--------------|---------------------|
| Куда | Output-панель скриптинга | Вкладка Log |
| Видимость | Только во вкладке Scripting | Глобальная вкладка Log |
| Использование | Отладочный вывод скриптов | Структурированное логирование |

### Примеры: Логирование

```csharp
// Базовое логирование
api.Log.Info("Скрипт запущен");
api.Log.Warning("Внимание: защита страницы не поддерживается");
api.Log.Error("Критическая ошибка: не удалось прочитать память!");
```

```csharp
// Логирование с форматированием
var rip = Reg("RIP");
var sym = Sym(rip) ?? "<unknown>";
api.Log.Info($"[MyScript] RIP = 0x{rip:X016} ({sym})");
```

```csharp
// Логирование в обработчике событий
api.OnDebugEventFilter += evt =>
{
    api.Log.Info($"[Tracer] Event: {evt.Type} @ 0x{evt.Address:X016}  " +
                 $"PID={evt.ProcessId} TID={evt.ThreadId}");
    return false;
};
```

---

## 10. Управление выполнением

Методы управления потоком выполнения отлаживаемого процесса. Все методы определены в `IDebuggerApi` (объект `api`).

### Методы

#### `Continue`

```csharp
void Continue()
```

Продолжает выполнение процесса (эквивалент **F9** / **Run**). Может вызываться из обработчика `OnDebugEventFilter` для автоматического продолжения.

---

#### `SingleStep`

```csharp
void SingleStep()
```

Выполняет одну инструкцию (**F7** / **Step Into**). Следует внутрь CALL-инструкций.

---

#### `StepOver`

```csharp
void StepOver()
```

Шаг через инструкцию (**F8**). Для CALL -- устанавливает временный BP на следующую инструкцию и выполняет CALL до возврата. Для остальных инструкций -- эквивалентно `SingleStep`.

---

#### `StepOut`

```csharp
void StepOut()
```

Выход из текущей функции (**Ctrl+F9**). Читает адрес возврата из `[RSP]` и выполняет до него.

---

#### `RunToCursor`

```csharp
void RunToCursor(ulong address)
```

Выполняет процесс до достижения указанного адреса (**F4** / **Run to Cursor**). Устанавливает временный BP и возобновляет выполнение.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Целевой адрес. |

---

#### `SkipInstruction`

```csharp
void SkipInstruction()
```

Пропускает текущую инструкцию без выполнения (**Ctrl+F8**). Перемещает RIP за текущую инструкцию.

**Внимание:** Пропуск инструкций может нарушить состояние стека и регистров. Используйте осторожно.

---

#### `Pause`

```csharp
void Pause()
```

Приостанавливает выполнение процесса (**F12** / **Break**). Останавливает все потоки.

### Примеры: Управление выполнением

```csharp
// Выполнить до адреса
api.RunToCursor(Addr("kernel32!CreateFileW"));
```

```csharp
// Пропустить вызов IsDebuggerPresent (он возвращает в EAX)
// Стоим на CALL IsDebuggerPresent:
api.SkipInstruction();
// Теперь стоим на инструкции после CALL.
// Устанавливаем RAX = 0 (не отлаживается)
api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, Reg("RIP"));
// Лучше записать регистр напрямую через шорткат, но WriteRip -- единственный
// доступный способ менять RIP. Для RAX можно патчить стек или использовать шеллкод.
```

```csharp
// Автотрассировка: шагать пока не дойдём до нужного модуля
var ntdll = api.Symbols.GetModules()
    .First(m => m.Name.Contains("ntdll", StringComparison.OrdinalIgnoreCase));

api.OnDebugEventFilter += evt =>
{
    if (evt.Type != PluginDebugEventType.SingleStep) return false;
    var rip = evt.Address;
    if (rip >= ntdll.BaseAddress && rip < ntdll.BaseAddress + ntdll.Size)
    {
        print($"Вошли в ntdll @ 0x{rip:X016}  {Sym(rip)}");
        return false; // остановиться
    }
    api.SingleStep(); // продолжаем трассировку
    return true;
};
api.SingleStep(); // начинаем
```

```csharp
// Step over в цикле (трассировка N инструкций)
int stepCount = 20;
for (int i = 0; i < stepCount; i++)
{
    var rip = Reg("RIP");
    var sym = Sym(rip) ?? "";
    var bytes = ReadMem(rip, 1);
    print($"#{i,3} 0x{rip:X016}  {sym}");

    api.StepOver();
    await Task.Delay(100); // ждём остановки
}
```

---

## 11. События отладчика

Система событий позволяет реагировать на изменения состояния отладчика, перехватывать отладочные события и автоматизировать анализ.

### Обзор событий

| Событие | Сигнатура | Описание |
|---------|-----------|----------|
| `api.OnDebugEvent` | `Action<PluginDebugEvent>` | Срабатывает при КАЖДОМ отладочном событии (после обработки UI). Для наблюдения, не для подавления. |
| `api.OnDebugEventFilter` | `Func<PluginDebugEvent, bool>` | Фильтр событий, вызывается ДО обработки UI. Верните `true`, чтобы подавить UI-обработку (плагин сам обрабатывает). Верните `false`, чтобы UI обработал нормально. |
| `api.OnConnected` | `Action` | Отладчик подключился к целевой системе. |
| `api.OnDisconnected` | `Action` | Отладчик отключился. |
| `api.OnBreakStateEntered` | `Action` | Процесс остановлен (вошли в break state). |
| `api.OnBreakStateExited` | `Action` | Процесс возобновлён (вышли из break state). |
| `api.OnBeforeRun` | `Action` | Процесс вот-вот будет возобновлён (Run/F9/Continue). Плагины могут установить breakpoints здесь. |

### PluginDebugEvent -- поля

| Поле | Тип | Описание |
|------|-----|----------|
| `Type` | `PluginDebugEventType` | Тип события. |
| `ProcessId` | `uint` | PID процесса. |
| `ThreadId` | `uint` | TID потока. |
| `Address` | `ulong` | Адрес события (RIP). |
| `IsKernelMode` | `bool` | `true` если событие в ядре. |
| `ExceptionCode` | `uint` | Код исключения Windows (для AV и пр.). |
| `FaultAddress` | `ulong` | Адрес доступа (для AccessViolation). |
| `AccessType` | `uint` | Тип доступа (для AV): `0`=чтение, `1`=запись, `8`=исполнение. |
| `ContinueMode` | `uint` | Режим продолжения (устанавливается плагином): `0`=Run, `1`=StepPast, `2`=StepInto, `3`=Handled, `4`=Trace. |
| `NewRip` | `ulong` | Если ненулевой -- перенаправляет RIP перед продолжением. |
| `NewRsp` | `ulong` | Если ненулевой -- перенаправляет RSP перед продолжением. |
| `TraceRangeBase` | `ulong` | Начало диапазона для ContinueMode=4 (Trace). |
| `TraceRangeEnd` | `ulong` | Конец диапазона для Trace. |
| `TraceMaxSteps` | `uint` | Максимальное количество шагов для Trace. |

### PluginDebugEventType -- значения

| Значение | Имя | Описание |
|----------|-----|----------|
| `1` | `Breakpoint` | Программная точка останова (INT3). |
| `2` | `SingleStep` | Единичный шаг (trap flag). |
| `3` | `HwBreakpoint` | Аппаратная точка останова (DR). |
| `4` | `HwWatchpoint` | Аппаратный watchpoint (DR, запись/чтение). |
| `5` | `MemoryBp` | Memory breakpoint (PAGE_GUARD). |
| `6` | `AccessViolation` | Нарушение доступа (AV). |

### ContinueMode -- значения

| Значение | Описание |
|----------|----------|
| `0` | **Run** (по умолчанию). Продолжить выполнение. |
| `1` | **StepPast**. Шагнуть мимо BP (восстановить оригинальный байт, single-step, восстановить INT3). |
| `2` | **StepInto**. Один шаг с trap flag. |
| `3` | **Handled**. Подавить исключение (для AV) и single-step. |
| `4` | **Trace**. Внутренний трейс драйвером: шагать пока RIP в `[TraceRangeBase, TraceRangeEnd)`, остановиться при выходе или `TraceMaxSteps`. |

### Примеры: События

```csharp
// Базовый обработчик событий
api.OnDebugEvent += evt =>
{
    print($"[Event] {evt.Type} @ 0x{evt.Address:X016}  " +
          $"PID={evt.ProcessId} TID={evt.ThreadId} Kernel={evt.IsKernelMode}");
};
```

```csharp
// Подписка на изменение состояния
api.OnBreakStateEntered += () => print(">>> Процесс остановлен");
api.OnBreakStateExited += () => print(">>> Процесс запущен");
api.OnConnected += () => print(">>> Подключение установлено");
api.OnDisconnected += () => print(">>> Отключение");
```

```csharp
// Фильтр: пропустить все BP в ntdll (автопродолжение)
var ntdll = api.Symbols.GetModules()
    .First(m => m.Name.Contains("ntdll", StringComparison.OrdinalIgnoreCase));

api.OnDebugEventFilter += evt =>
{
    if (evt.Address >= ntdll.BaseAddress &&
        evt.Address < ntdll.BaseAddress + ntdll.Size)
    {
        api.Continue();
        return true; // подавить UI
    }
    return false;
};
```

```csharp
// Перехват AccessViolation с информацией
api.OnDebugEventFilter += evt =>
{
    if (evt.Type != PluginDebugEventType.AccessViolation) return false;

    string accessStr = evt.AccessType switch
    {
        0 => "READ",
        1 => "WRITE",
        8 => "EXECUTE",
        _ => $"UNKNOWN({evt.AccessType})"
    };
    print($"AV: {accessStr} @ 0x{evt.FaultAddress:X016}  " +
          $"RIP=0x{evt.Address:X016}  Code=0x{evt.ExceptionCode:X08}");
    return false;
};
```

```csharp
// OnBeforeRun: автоматически ставить BP при каждом продолжении
api.OnBeforeRun += () =>
{
    var targetAddr = Addr("ntdll!NtCreateFile");
    if (targetAddr != 0)
    {
        // Проверяем, есть ли уже BP
        var existing = api.Breakpoints.GetAll()
            .Any(bp => bp.Address == targetAddr);
        if (!existing)
            api.Breakpoints.SetBreakpoint(
                api.TargetPid, 0, targetAddr,
                PluginBreakpointType.Software);
    }
};
```

```csharp
// Перенаправление RIP через ContinueMode
api.OnDebugEventFilter += evt =>
{
    if (evt.Address == Addr("myapp!CheckLicense"))
    {
        // Перенаправляем выполнение, минуя проверку лицензии
        evt.NewRip = Addr("myapp!CheckLicense") + 0x50; // после проверки
        evt.ContinueMode = 0; // Run
        return true;
    }
    return false;
};
```

```csharp
// Трассировка внутри диапазона (ContinueMode=4)
api.OnDebugEventFilter += evt =>
{
    if (evt.Address == Addr("myapp!DecryptFunction"))
    {
        // Трассировать всю функцию
        evt.ContinueMode = 4; // Trace
        evt.TraceRangeBase = evt.Address;
        evt.TraceRangeEnd = evt.Address + 0x200;
        evt.TraceMaxSteps = 10000;
        return true;
    }
    if (evt.Type == PluginDebugEventType.SingleStep)
    {
        // Трассировка завершена (вышли из диапазона или лимит шагов)
        print($"Trace закончился @ 0x{evt.Address:X016}  {Sym(evt.Address)}");
        return false;
    }
    return false;
};
```

---

## 12. Модели данных

Полное описание всех классов и перечислений SDK.

### PluginRegister

Регистр процессора.

```csharp
public class PluginRegister
{
    public string Name { get; set; }   // Имя: "RAX", "RIP", "CF", "ZF" и т.д.
    public ulong Value { get; set; }   // Значение (64-бит)
    public bool IsFlag { get; set; }   // true для флагов (CF, ZF, SF, OF, PF, AF, DF, IF, TF)
}
```

**Заметки:** Для флагов `Value` обычно `0` или `1`. Для сегментных регистров (CS, DS, ES, FS, GS, SS) `Value` содержит селектор.

---

### PluginModuleInfo

Загруженный user-mode модуль (DLL/EXE).

```csharp
public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }  // Базовый адрес загрузки
    public uint Size { get; set; }          // Размер модуля в памяти (SizeOfImage)
    public string Name { get; set; }        // Имя файла (без пути): "ntdll.dll", "kernel32.dll"
}
```

---

### PluginKernelModuleInfo

Модуль ядра (драйвер, ntoskrnl и пр.).

```csharp
public class PluginKernelModuleInfo
{
    public ulong BaseAddress { get; set; }  // Базовый адрес в kernel-space
    public uint Size { get; set; }          // Размер модуля
    public ushort LoadOrder { get; set; }   // Порядковый номер загрузки (0 = ntoskrnl)
    public string Name { get; set; }        // Имя файла
}
```

---

### PluginProcessInfo

Информация о процессе.

```csharp
public class PluginProcessInfo
{
    public uint ProcessId { get; set; }     // PID
    public uint SessionId { get; set; }     // ID сессии (0 = системная, 1+ = интерактивная)
    public string Name { get; set; }        // Имя процесса: "explorer.exe", "svchost.exe"
}
```

---

### PluginThreadInfo

Информация о потоке.

```csharp
public class PluginThreadInfo
{
    public uint ThreadId { get; set; }      // TID
    public ulong StartAddress { get; set; } // Адрес стартовой функции потока
    public uint State { get; set; }         // Состояние: 0=Initialized, 1=Ready, 2=Running, 5=Waiting
    public uint Priority { get; set; }      // Приоритет потока
}
```

---

### PluginBreakpoint

Точка останова.

```csharp
public class PluginBreakpoint
{
    public uint Handle { get; set; }                  // Уникальный идентификатор
    public ulong Address { get; set; }                // Адрес
    public PluginBreakpointType Type { get; set; }    // Тип (Software/Hardware/...)
    public bool Enabled { get; set; }                 // Активна ли
    public string? Condition { get; set; }            // Условие (для условных BP)
    public uint HitCount { get; set; }                // Количество срабатываний
    public byte OriginalByte { get; set; }            // Оригинальный байт (для Software BP, заменённый на 0xCC)
}
```

---

### PluginBreakpointType

Перечисление типов точек останова.

```csharp
public enum PluginBreakpointType
{
    Software    = 0,   // INT3 (0xCC)
    Hardware    = 1,   // DR execute
    HwWrite     = 2,   // DR write
    HwReadWrite = 3,   // DR read/write
    Memory      = 4    // PAGE_GUARD
}
```

---

### PluginDebugEvent

Отладочное событие.

```csharp
public class PluginDebugEvent
{
    public PluginDebugEventType Type { get; set; }  // Тип события
    public uint ProcessId { get; set; }             // PID
    public uint ThreadId { get; set; }              // TID
    public ulong Address { get; set; }              // RIP на момент события
    public bool IsKernelMode { get; set; }          // Событие в kernel mode
    public uint ExceptionCode { get; set; }         // Windows exception code
    public ulong FaultAddress { get; set; }         // Адрес обращения (для AV)
    public uint AccessType { get; set; }            // 0=read, 1=write, 8=execute (для AV)

    // Управление продолжением (устанавливается плагином):
    public uint ContinueMode { get; set; }          // 0=Run, 1=StepPast, 2=StepInto, 3=Handled, 4=Trace
    public ulong NewRip { get; set; }               // Перенаправить RIP (0 = не менять)
    public ulong NewRsp { get; set; }               // Перенаправить RSP (0 = не менять)

    // Для ContinueMode=4 (Trace):
    public ulong TraceRangeBase { get; set; }       // Начало диапазона трассировки
    public ulong TraceRangeEnd { get; set; }        // Конец диапазона
    public uint TraceMaxSteps { get; set; }         // Максимум шагов
}
```

---

### PluginDebugEventType

Перечисление типов отладочных событий.

```csharp
public enum PluginDebugEventType
{
    Breakpoint      = 1,   // Software BP (INT3)
    SingleStep      = 2,   // Trap flag (шаг)
    HwBreakpoint    = 3,   // Hardware BP (DR execute)
    HwWatchpoint    = 4,   // Hardware watchpoint (DR write/rw)
    MemoryBp        = 5,   // Memory BP (PAGE_GUARD)
    AccessViolation = 6    // Access violation
}
```

---

### PluginSectionInfo

Секция PE-модуля.

```csharp
public class PluginSectionInfo
{
    public string Name { get; set; }             // Имя секции: ".text", ".data", ".rdata"
    public ulong VirtualAddress { get; set; }    // RVA секции
    public uint VirtualSize { get; set; }        // Размер в памяти
    public uint Characteristics { get; set; }    // Флаги: 0x20=CODE, 0x40=INITIALIZED_DATA,
                                                 //        0x20000000=EXECUTE, 0x40000000=READ,
                                                 //        0x80000000=WRITE
}
```

---

### PluginFunctionEntry

Запись пользовательской функции (из `RegisterFunction`).

```csharp
public class PluginFunctionEntry
{
    public ulong Address { get; set; }  // Адрес начала функции
    public string Name { get; set; }    // Имя функции
    public uint Size { get; set; }      // Размер функции в байтах
}
```

---

### PluginScriptHost

Хост для Roslyn-скриптов. Определяет глобальные переменные, доступные в REPL.

```csharp
public class PluginScriptHost
{
    public IDebuggerApi api { get; set; }       // Корневой API отладчика
    public Action<string> print { get; set; }   // Функция вывода
}
```

---

## 13. Обход антиотладки

KernelFlirt предоставляет мощные ядерные инструменты для обхода антиотладочных защит. Все методы работают на уровне ядра, что делает их невидимыми для user-mode проверок.

### Комплексный обход

```csharp
// Полный anti-debug bypass -- выполнить после аттача к процессу
void AntiAntiDebug()
{
    var pid = api.TargetPid;

    // 1. Обнулить DebugPort в EPROCESS
    //    Защищает от: NtQueryInformationProcess(ProcessDebugPort),
    //    CheckRemoteDebuggerPresent, ProcessDebugObjectHandle
    api.Process.ClearDebugPort(pid);
    print("[+] DebugPort обнулён");

    // 2. Снять ThreadHideFromDebugger со всех потоков
    //    Защищает от: NtSetInformationThread(ThreadHideFromDebugger)
    api.Process.ClearThreadHide(pid);
    print("[+] ThreadHideFromDebugger снят");

    // 3. Подменить KUSER_SHARED_DATA
    //    Защищает от: проверок KdDebuggerEnabled / KernelDebugger
    //    через SharedUserData (0x7FFE0000)
    api.Process.SetSpoofSharedUserData(true);
    print("[+] SharedUserData spoofed");

    // 4. Скрыть процесс из NtQuerySystemInformation
    //    Защищает от: перечисления процессов (Task Manager, антивирусы)
    api.Process.InstallNtQsiHook();
    print("[+] NtQSI хук установлен");

    print("[*] Anti-debug bypass полностью активирован");
}
AntiAntiDebug();
```

### Обнуление BeingDebugged в PEB

```csharp
// Патч PEB.BeingDebugged напрямую
var (peb, _) = api.Process.GetPebAddress(api.TargetPid);
var beingDebugged = ReadMem(peb + 2, 1);
print($"PEB.BeingDebugged до: {beingDebugged[0]}");

WriteMem(peb + 2, new byte[] { 0 });
print("PEB.BeingDebugged обнулён");

// Обнулить NtGlobalFlag (PEB + 0x68 на x64)
// Антиотладочные значения: 0x70 (FLG_HEAP_ENABLE_TAIL_CHECK |
//                                 FLG_HEAP_ENABLE_FREE_CHECK |
//                                 FLG_HEAP_VALIDATE_PARAMETERS)
WriteMem(peb + 0x68, new byte[] { 0, 0, 0, 0 });
print("PEB.NtGlobalFlag обнулён");
```

### Обход проверки на TimeTick / RDTSC

```csharp
// Патч RDTSC-тайминг-проверки: заменить JA/JB на NOP/JMP
// Пример: jbe loc_... (0x76 XX) -> jmp loc_... (0xEB XX)
var addr = 0x7FF6A0001234UL; // адрес условного перехода после RDTSC-проверки
var currentBytes = ReadMem(addr, 2);
if (currentBytes[0] == 0x76 || currentBytes[0] == 0x77) // JBE или JA
{
    WriteMem(addr, new byte[] { 0xEB }); // JMP short (безусловный)
    print($"Тайминг-проверка обойдена @ 0x{addr:X016}");
}
```

### Обход IsDebuggerPresent

```csharp
// Хук на IsDebuggerPresent: при каждом вызове устанавливаем возврат = 0
var idp = Addr("kernel32!IsDebuggerPresent");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, idp, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != idp) return false;

    // Читаем return address
    var retAddr = ReadPtr(Reg("RSP"));

    // Устанавливаем RAX = 0 (не отлаживается) и RIP на return address
    // RSP += 8 (убираем return address)
    evt.NewRip = retAddr;
    evt.NewRsp = Reg("RSP") + 8;
    evt.ContinueMode = 0; // Run

    // Нужно ещё занулить RAX. Поскольку у нас нет WriteRegister,
    // инжектим шеллкод:
    var shellcode = api.Memory.AllocateMemory(api.TargetPid, 4096);
    WriteMem(shellcode, new byte[] {
        0x48, 0x31, 0xC0,  // xor rax, rax
        0xFF, 0x25, 0x00, 0x00, 0x00, 0x00  // jmp [rip+0]
    });
    // Записываем адрес возврата после jmp [rip+0]
    WriteMem(shellcode + 9, BitConverter.GetBytes(retAddr));
    evt.NewRip = shellcode;
    evt.NewRsp = Reg("RSP") + 8;

    print($"IsDebuggerPresent -> 0 (return to 0x{retAddr:X016})");
    return true;
};
```

### Проверка состояния хука

```csharp
// Проверить статус NtQSI хука
var status = api.Process.ProbeNtQsiHook();
print($"NtQSI Hook: {status}");
```

### Снятие anti-debug при завершении

```csharp
// Не забудьте снять хуки при завершении анализа!
api.Process.RemoveNtQsiHook();
api.Process.SetSpoofSharedUserData(false);
print("Anti-debug bypass деактивирован");
```

---

## 14. Именование функций и аннотации

Именование функций и комментарии в дизассемблере -- ключевые инструменты для статического анализа. KernelFlirt поддерживает оба механизма.

### RegisterFunction vs SetAddressAnnotation

| | `RegisterFunction` | `SetAddressAnnotation` |
|--|---------------------|------------------------|
| Назначение | Имя функции | Комментарий к адресу |
| Влияет на `ResolveAddress` | Да | Нет |
| Отображение | В столбце символов | Как `"; comment"` |
| Поддержка размера | Да (`size`) | Нет |
| Хранение | В таблице символов | В словаре аннотаций |

### Автоименование функций из E8 CALL-ов

```csharp
// Сканирование всех CALL-инструкций (E8 xx xx xx xx) в модуле
// и автоименование неизвестных целей как sub_XXXXXXXX
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) { print("Ошибка чтения"); return; }

int count = 0;
var known = new HashSet<ulong>();

for (uint i = 0; i < data.Length - 5; i++)
{
    if (data[i] != 0xE8) continue; // CALL rel32

    int rel = BitConverter.ToInt32(data, (int)i + 1);
    ulong target = mod.BaseAddress + i + 5 + (ulong)(long)rel;

    // Проверяем, что target внутри модуля
    if (target < mod.BaseAddress || target >= mod.BaseAddress + mod.Size) continue;
    if (known.Contains(target)) continue;
    known.Add(target);

    // Проверяем, есть ли уже имя
    var existing = Sym(target);
    if (existing != null && !existing.Contains("+0x")) continue;

    // Определяем размер функции (наивно: до следующей RET или следующей функции)
    uint funcSize = 0;
    for (uint j = 0; j < 0x1000 && (target - mod.BaseAddress + j) < (ulong)data.Length; j++)
    {
        var off = (int)(target - mod.BaseAddress + j);
        if (data[off] == 0xC3 || data[off] == 0xC2) // RET / RET imm16
        {
            funcSize = j + 1;
            break;
        }
    }
    if (funcSize == 0) funcSize = 0x10; // fallback

    api.Symbols.RegisterFunction(target, $"sub_{target:X}", funcSize);
    count++;
}

print($"Зарегистрировано {count} функций");
```

### Массовые аннотации

```csharp
// Аннотировать все вызовы WinAPI
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);

for (uint i = 0; i < data.Length - 6; i++)
{
    // FF 15 xx xx xx xx = CALL [rip + disp32] (импорт)
    if (data[i] != 0xFF || data[i + 1] != 0x15) continue;

    int disp = BitConverter.ToInt32(data, (int)i + 2);
    ulong iatEntry = mod.BaseAddress + i + 6 + (ulong)(long)disp;
    ulong funcAddr = ReadPtr(iatEntry);

    if (funcAddr == 0) continue;
    var sym = Sym(funcAddr);
    if (sym != null)
    {
        var callAddr = mod.BaseAddress + i;
        api.UI.SetAddressAnnotation(callAddr, $"call {sym}");
    }
}
api.UI.RefreshDisassembly();
print("Аннотации IAT-вызовов добавлены");
```

### Экспорт/Импорт именованных функций

```csharp
// Экспорт всех зарегистрированных функций в CSV
var funcs = api.Symbols.GetRegisteredFunctions();
var lines = funcs.Select(f => $"0x{f.Address:X016},{f.Name},{f.Size}");
File.WriteAllLines(@"C:\Temp\functions.csv", lines);
print($"Экспортировано {funcs.Count} функций в functions.csv");
```

```csharp
// Импорт функций из CSV
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
foreach (var line in File.ReadAllLines(@"C:\Temp\functions.csv"))
{
    var parts = line.Split(',');
    if (parts.Length < 3) continue;
    var addr = Convert.ToUInt64(parts[0].Trim(), 16);
    var name = parts[1].Trim();
    var size = Convert.ToUInt32(parts[2].Trim());
    api.Symbols.RegisterFunction(addr, name, size);
}
print("Функции импортированы");
```

---

## 15. Анализ PE-файлов

Скрипты для парсинга PE-заголовков, секций, импортов и экспортов прямо из памяти процесса.

### Парсинг PE-заголовка

```csharp
// Полный парсинг PE-заголовка из памяти
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
var dos = ReadMem(baseAddr, 0x40);
if (dos[0] != 0x4D || dos[1] != 0x5A) { print("Не PE!"); return; }

var peOffset = BitConverter.ToUInt32(dos, 0x3C);
var pe = ReadMem(baseAddr + peOffset, 0x200);

// Сигнатура "PE\0\0"
if (pe[0] != 0x50 || pe[1] != 0x45) { print("Некорректный PE-заголовок"); return; }

// COFF Header (offset 4 от PE signature)
var machine = BitConverter.ToUInt16(pe, 4);
var numSections = BitConverter.ToUInt16(pe, 6);
var timeDateStamp = BitConverter.ToUInt32(pe, 8);
var sizeOptHeader = BitConverter.ToUInt16(pe, 20);
var characteristics = BitConverter.ToUInt16(pe, 22);

print($"Machine:          0x{machine:X04} ({(machine == 0x8664 ? "AMD64" : machine == 0x14C ? "i386" : "??")})");
print($"Sections:         {numSections}");
print($"TimeDateStamp:    0x{timeDateStamp:X08} ({DateTimeOffset.FromUnixTimeSeconds(timeDateStamp):yyyy-MM-dd HH:mm:ss})");
print($"Characteristics:  0x{characteristics:X04}");

// Optional Header (offset 24)
var magic = BitConverter.ToUInt16(pe, 24);
bool isPE32Plus = magic == 0x20B;
print($"Optional Magic:   0x{magic:X04} ({(isPE32Plus ? "PE32+" : "PE32")})");

int epOffset = isPE32Plus ? 40 : 40;
var entryPoint = BitConverter.ToUInt32(pe, 24 + 16);
var imageBase = isPE32Plus
    ? BitConverter.ToUInt64(pe, 24 + 24)
    : BitConverter.ToUInt32(pe, 24 + 28);
var sizeOfImage = BitConverter.ToUInt32(pe, 24 + 56);

print($"AddressOfEntryPoint: 0x{entryPoint:X08} (VA: 0x{baseAddr + entryPoint:X016})");
print($"ImageBase (header):  0x{imageBase:X016}");
print($"SizeOfImage:         0x{sizeOfImage:X08}");

// Навигация к Entry Point
api.UI.NavigateDisassembly(baseAddr + entryPoint);
```

### Парсинг таблицы секций

```csharp
// Перечислить секции PE-модуля
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
var peOffset = ReadU32(baseAddr + 0x3C);
var numSections = BitConverter.ToUInt16(ReadMem(baseAddr + peOffset + 6, 2), 0);
var sizeOptHeader = BitConverter.ToUInt16(ReadMem(baseAddr + peOffset + 20, 2), 0);

// Секции начинаются после Optional Header
var sectionsStart = baseAddr + peOffset + 24 + sizeOptHeader;

print($"{"Имя",-10} {"VirtAddr",10} {"VirtSize",10} {"RawSize",10} {"Chars",10}");
print(new string('-', 55));

for (int i = 0; i < numSections; i++)
{
    var sec = ReadMem(sectionsStart + (ulong)(i * 40), 40);
    var name = Encoding.ASCII.GetString(sec, 0, 8).TrimEnd('\0');
    var virtSize = BitConverter.ToUInt32(sec, 8);
    var virtAddr = BitConverter.ToUInt32(sec, 12);
    var rawSize = BitConverter.ToUInt32(sec, 16);
    var chars = BitConverter.ToUInt32(sec, 36);

    string flags = "";
    if ((chars & 0x20000000) != 0) flags += "X";
    if ((chars & 0x40000000) != 0) flags += "R";
    if ((chars & 0x80000000) != 0) flags += "W";

    print($"{name,-10} 0x{virtAddr:X08} 0x{virtSize:X08} 0x{rawSize:X08} 0x{chars:X08} [{flags}]");
}
```

### Парсинг таблицы импортов

```csharp
// Перечислить импортируемые DLL и функции
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
var peOffset = ReadU32(baseAddr + 0x3C);

// Data Directory[1] = Import Table
int ddOffset = 24 + 104; // PE32+: Optional Header offset + Import Table DD offset
if (BitConverter.ToUInt16(ReadMem(baseAddr + peOffset + 24, 2), 0) != 0x20B)
    ddOffset = 24 + 96; // PE32

var importRVA = ReadU32(baseAddr + peOffset + (uint)ddOffset);
var importSize = ReadU32(baseAddr + peOffset + (uint)ddOffset + 4);

if (importRVA == 0) { print("Нет таблицы импортов"); return; }

var importAddr = baseAddr + importRVA;

// Import Directory Table -- массив IMAGE_IMPORT_DESCRIPTOR (20 байт каждый)
int dllCount = 0;
int funcCount = 0;

for (uint i = 0; ; i += 20)
{
    var desc = ReadMem(importAddr + i, 20);
    var iltRVA = BitConverter.ToUInt32(desc, 0);   // OriginalFirstThunk
    var nameRVA = BitConverter.ToUInt32(desc, 12);  // Name
    var iatRVA = BitConverter.ToUInt32(desc, 16);   // FirstThunk

    if (iltRVA == 0 && nameRVA == 0) break; // конец

    var dllName = ReadString(baseAddr + nameRVA, 256);
    print($"\n=== {dllName} ===");
    dllCount++;

    // Перечислить функции через ILT (OriginalFirstThunk)
    var thunkAddr = baseAddr + (iltRVA != 0 ? iltRVA : iatRVA);
    for (uint j = 0; ; j += 8) // 8 байт на x64
    {
        var thunk = ReadU64(thunkAddr + j);
        if (thunk == 0) break;

        if ((thunk & 0x8000000000000000) != 0)
        {
            // Импорт по ординалу
            print($"  [{funcCount}] Ordinal #{thunk & 0xFFFF}");
        }
        else
        {
            // Импорт по имени
            var hintName = ReadMem(baseAddr + (uint)(thunk & 0x7FFFFFFF), 256);
            var hint = BitConverter.ToUInt16(hintName, 0);
            var funcName = Encoding.ASCII.GetString(hintName, 2,
                Array.IndexOf(hintName, (byte)0, 2) - 2);
            print($"  [{funcCount}] {funcName} (hint={hint})");
        }
        funcCount++;
    }
}
print($"\nИтого: {dllCount} DLL, {funcCount} функций");
```

### Парсинг таблицы экспортов

```csharp
// Перечислить экспорты модуля
void DumpExports(ulong baseAddr)
{
    var peOffset = ReadU32(baseAddr + 0x3C);
    int ddOffset = 24 + 88; // PE32+: Export Table DD offset
    var exportRVA = ReadU32(baseAddr + peOffset + (uint)ddOffset);

    if (exportRVA == 0) { print("Нет таблицы экспортов"); return; }

    var expDir = ReadMem(baseAddr + exportRVA, 40);
    var numFunctions = BitConverter.ToUInt32(expDir, 20);
    var numNames = BitConverter.ToUInt32(expDir, 24);
    var funcRVA = BitConverter.ToUInt32(expDir, 28);
    var nameRVA = BitConverter.ToUInt32(expDir, 32);
    var ordRVA = BitConverter.ToUInt32(expDir, 36);
    var ordBase = BitConverter.ToUInt32(expDir, 16);

    var dllName = ReadString(baseAddr + BitConverter.ToUInt32(expDir, 12), 256);
    print($"Экспорты {dllName}: {numNames} по имени, {numFunctions} всего");
    print($"{"Ord",5} {"RVA",10} {"VA",18} {"Имя"}");
    print(new string('-', 60));

    for (uint i = 0; i < numNames; i++)
    {
        var namePtr = ReadU32(baseAddr + nameRVA + i * 4);
        var name = ReadString(baseAddr + namePtr, 256);
        var ordIdx = BitConverter.ToUInt16(ReadMem(baseAddr + ordRVA + i * 2, 2), 0);
        var funcAddr = ReadU32(baseAddr + funcRVA + (uint)ordIdx * 4);

        print($"{ordBase + ordIdx,5} 0x{funcAddr:X08} 0x{baseAddr + funcAddr:X016} {name}");
    }
}

var mod = api.Symbols.GetModules().First(m =>
    m.Name.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
DumpExports(mod.BaseAddress);
```

### Предоставление секций при обнулённом заголовке

```csharp
// Пакер обнулил PE-заголовок? Задайте секции вручную.
var sections = new List<PluginSectionInfo>
{
    new PluginSectionInfo { Name = ".text",  VirtualAddress = 0x1000, VirtualSize = 0x5000,
                            Characteristics = 0x60000020 }, // CODE|EXECUTE|READ
    new PluginSectionInfo { Name = ".rdata", VirtualAddress = 0x6000, VirtualSize = 0x2000,
                            Characteristics = 0x40000040 }, // INITIALIZED_DATA|READ
    new PluginSectionInfo { Name = ".data",  VirtualAddress = 0x8000, VirtualSize = 0x1000,
                            Characteristics = 0xC0000040 }, // INITIALIZED_DATA|READ|WRITE
};
api.UI.AddModuleSections("packed.exe", sections);
api.UI.RefreshModulesAndSections();
print("Секции добавлены вручную");
```

---

## 16. Работа со стеком

Скрипты для анализа стека вызовов, параметров функций и локальных переменных.

### Дамп стека (raw)

```csharp
// Дамп 32 указателей со стека с разрешением символов
var rsp = Reg("RSP");
print($"RSP = 0x{rsp:X016}\n");
print($"{"Offset",8}  {"Адрес",18}  {"Значение",18}  {"Символ"}");
print(new string('-', 75));

for (int i = 0; i < 32; i++)
{
    var stackAddr = rsp + (ulong)(i * 8);
    var value = ReadPtr(stackAddr);
    var sym = Sym(value);

    print($"+0x{i * 8:X04}    0x{stackAddr:X016}  0x{value:X016}  {sym ?? ""}");
}
```

### Построение callstack

```csharp
// Наивный callstack (следуем по return addresses на стеке)
var rsp = Reg("RSP");
var rip = Reg("RIP");

print($"#0  0x{rip:X016}  {Sym(rip) ?? "???"}");

int frame = 1;
for (ulong offset = 0; offset < 0x10000; offset += 8)
{
    var value = ReadPtr(rsp + offset);
    if (value == 0) continue;

    // Проверяем, может ли это быть return address
    // (должен указывать в один из модулей)
    var sym = Sym(value);
    if (sym != null && !sym.Contains("+0x0"))
    {
        // Проверяем, что перед этим адресом стоит CALL
        var prevBytes = ReadMem(value - 5, 5);
        if (prevBytes != null && (prevBytes[0] == 0xE8 ||        // CALL rel32
            (prevBytes[3] == 0xFF && (prevBytes[4] & 0x38) == 0x10))) // CALL [reg]
        {
            print($"#{frame}  0x{value:X016}  {sym}");
            frame++;
            if (frame > 30) break;
        }
    }
}
```

### Анализ параметров функции (x64 ABI)

```csharp
// В x64 Windows calling convention:
// RCX = 1-й параметр, RDX = 2-й, R8 = 3-й, R9 = 4-й
// 5-й и далее -- на стеке (RSP+0x28, RSP+0x30, ...)

print("=== Параметры текущей функции (x64) ===");
print($"Arg1 (RCX) = 0x{Reg("RCX"):X016}");
print($"Arg2 (RDX) = 0x{Reg("RDX"):X016}");
print($"Arg3 (R8)  = 0x{Reg("R8"):X016}");
print($"Arg4 (R9)  = 0x{Reg("R9"):X016}");

// Аргументы на стеке (5+)
var rsp = Reg("RSP");
for (int i = 0; i < 8; i++)
{
    var argAddr = rsp + 0x28UL + (ulong)(i * 8);
    var argVal = ReadPtr(argAddr);
    print($"Arg{i + 5} [RSP+0x{0x28 + i * 8:X02}] = 0x{argVal:X016}");
}
```

### Return address и caller

```csharp
// Узнать, кто вызвал текущую функцию
var retAddr = ReadPtr(Reg("RSP"));
print($"Return address: 0x{retAddr:X016}");
print($"Caller:         {Sym(retAddr) ?? "???"}");

// Перейти к коду вызывающей функции
api.UI.NavigateDisassembly(retAddr - 5); // CALL обычно 5 байт
```

---

## 17. Расшифровка и декодирование строк

Техники извлечения и расшифровки обфусцированных строк из памяти.

### XOR-расшифровка строк

```csharp
// XOR-расшифровка блока данных одним ключом (1 байт)
byte[] XorDecrypt(byte[] data, byte key)
{
    var result = new byte[data.Length];
    for (int i = 0; i < data.Length; i++)
        result[i] = (byte)(data[i] ^ key);
    return result;
}

// Пример: расшифровать строку по адресу
var encrypted = ReadMem(0x7FF6A0003000, 32);
for (byte key = 1; key < 0xFF; key++)
{
    var decrypted = XorDecrypt(encrypted, key);
    // Проверяем, есть ли печатные ASCII символы
    if (decrypted.All(b => b == 0 || (b >= 0x20 && b < 0x7F)))
    {
        int end = Array.IndexOf(decrypted, (byte)0);
        if (end > 3) // минимум 4 символа
        {
            var str = Encoding.ASCII.GetString(decrypted, 0, end);
            print($"Key=0x{key:X02}: \"{str}\"");
        }
    }
}
```

### XOR с многобайтовым ключом

```csharp
// XOR с ключом произвольной длины
byte[] XorDecryptMulti(byte[] data, byte[] key)
{
    var result = new byte[data.Length];
    for (int i = 0; i < data.Length; i++)
        result[i] = (byte)(data[i] ^ key[i % key.Length]);
    return result;
}

var encrypted = ReadMem(0x7FF6A0005000, 64);
var key = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
var decrypted = XorDecryptMulti(encrypted, key);
print(Encoding.ASCII.GetString(decrypted).TrimEnd('\0'));
```

### ROT13 / Caesar-сдвиг

```csharp
// Caesar-сдвиг (обобщённый ROT)
string CaesarDecrypt(byte[] data, int shift)
{
    var sb = new StringBuilder();
    foreach (var b in data)
    {
        if (b == 0) break;
        if (b >= 'A' && b <= 'Z')
            sb.Append((char)('A' + (b - 'A' + shift) % 26));
        else if (b >= 'a' && b <= 'z')
            sb.Append((char)('a' + (b - 'a' + shift) % 26));
        else
            sb.Append((char)b);
    }
    return sb.ToString();
}

var data = ReadMem(0x7FF6A0006000, 128);
for (int shift = 1; shift < 26; shift++)
{
    var decrypted = CaesarDecrypt(data, shift);
    if (decrypted.Contains("http") || decrypted.Contains("dll") || decrypted.Contains("exe"))
        print($"ROT{shift}: {decrypted}");
}
```

### Base64-декодирование

```csharp
// Прочитать и декодировать Base64-строку из памяти
var b64str = ReadString(Reg("RCX"), 4096);
try
{
    var decoded = Convert.FromBase64String(b64str);
    // Попробуем как UTF-8 строку
    var text = Encoding.UTF8.GetString(decoded);
    print($"Base64 decoded: {text}");

    // Или дамп бинарный
    print($"Hex: {BitConverter.ToString(decoded).Replace("-", " ")}");
}
catch
{
    print($"Не Base64: {b64str.Substring(0, Math.Min(64, b64str.Length))}...");
}
```

### Массовый поиск зашифрованных строк

```csharp
// Поиск потенциально зашифрованных строк
// (блоки непечатных байт, за которыми следует обращение из кода)
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);

// Ищем блоки подозрительных данных (не нули, не код, 8+ байт)
var candidates = new List<(ulong addr, int len)>();
int start = -1;
for (int i = 0; i < data.Length; i++)
{
    bool suspicious = data[i] > 0 && data[i] < 0x20 || data[i] > 0x7E;
    if (suspicious && start == -1) start = i;
    if ((!suspicious || i == data.Length - 1) && start != -1)
    {
        int len = i - start;
        if (len >= 8 && len <= 256)
            candidates.Add((mod.BaseAddress + (ulong)start, len));
        start = -1;
    }
}
print($"Найдено {candidates.Count} подозрительных блоков");

// Пробуем XOR с популярными ключами
foreach (var (addr, len) in candidates.Take(20))
{
    var block = ReadMem(addr, (uint)len);
    foreach (byte key in new byte[] { 0x55, 0xAA, 0xFF, 0x37, 0x42 })
    {
        var dec = XorDecrypt(block, key);
        if (dec.Count(b => b >= 0x20 && b < 0x7F) > len * 0.7)
        {
            int end = Array.IndexOf(dec, (byte)0);
            if (end < 0) end = dec.Length;
            var str = Encoding.ASCII.GetString(dec, 0, end);
            print($"0x{addr:X016} [key=0x{key:X02}]: \"{str}\"");
        }
    }
}
```

---

## 18. Восстановление IAT

Восстановление Import Address Table (IAT) -- ключевой шаг при распаковке протекторов (UPX, Themida, VMProtect и пр.).

### Дамп текущей IAT

```csharp
// Дамп IAT (массив указателей на функции)
var mod = api.Symbols.GetModules()[0];
var peOffset = ReadU32(mod.BaseAddress + 0x3C);

// IAT Data Directory (индекс 12 в Data Directory)
int ddBase = 24 + (BitConverter.ToUInt16(ReadMem(mod.BaseAddress + peOffset + 24, 2), 0) == 0x20B ? 112 : 96);
// Import Table DD = индекс 1, но нам нужна IAT DD = индекс 12
var iatDD = ddBase + 12 * 8;
var iatRVA = ReadU32(mod.BaseAddress + peOffset + (uint)iatDD);
var iatSize = ReadU32(mod.BaseAddress + peOffset + (uint)iatDD + 4);

if (iatRVA == 0) { print("IAT не найдена"); return; }

var iatBase = mod.BaseAddress + iatRVA;
print($"IAT @ 0x{iatBase:X016}, размер: 0x{iatSize:X08}\n");

for (ulong off = 0; off < iatSize; off += 8)
{
    var funcAddr = ReadPtr(iatBase + off);
    if (funcAddr == 0) continue;

    var sym = Sym(funcAddr);
    print($"IAT[0x{off:X04}] = 0x{funcAddr:X016}  {sym ?? "???"}");
}
```

### Восстановление IAT после распаковки

```csharp
// Восстановление IAT: заменить адреса тремполинов на реальные адреса API
// Предположим: пакер заменил IAT-записи на адреса своих обёрток

var mod = api.Symbols.GetModules()[0];
var iatBase = mod.BaseAddress + 0x8000UL; // подставьте реальный RVA IAT
var iatEntries = 100; // подставьте реальное количество

int fixed = 0;
for (int i = 0; i < iatEntries; i++)
{
    var entryAddr = iatBase + (ulong)(i * 8);
    var funcAddr = ReadPtr(entryAddr);
    if (funcAddr == 0) continue;

    // Проверяем, указывает ли на известный модуль
    var sym = Sym(funcAddr);
    if (sym != null) continue; // уже ОК

    // Читаем первые байты по адресу -- ищем JMP [xxx] или JMP rel32
    var bytes = ReadMem(funcAddr, 16);
    if (bytes == null) continue;

    ulong realFunc = 0;

    // JMP QWORD PTR [rip + disp32] = FF 25 xx xx xx xx
    if (bytes[0] == 0xFF && bytes[1] == 0x25)
    {
        int disp = BitConverter.ToInt32(bytes, 2);
        realFunc = ReadPtr(funcAddr + 6 + (ulong)(long)disp);
    }
    // JMP rel32 = E9 xx xx xx xx
    else if (bytes[0] == 0xE9)
    {
        int rel = BitConverter.ToInt32(bytes, 1);
        realFunc = funcAddr + 5 + (ulong)(long)rel;
    }

    if (realFunc != 0)
    {
        var realSym = Sym(realFunc);
        if (realSym != null)
        {
            // Записываем реальный адрес в IAT
            WriteMem(entryAddr, BitConverter.GetBytes(realFunc));
            print($"IAT[{i}]: 0x{funcAddr:X016} -> 0x{realFunc:X016} ({realSym})");
            fixed++;
        }
    }
}
print($"\nВосстановлено {fixed} IAT-записей");
```

### Трассировка IAT-вызовов

```csharp
// Поставить BP на каждую IAT-запись для логирования вызовов API
var mod = api.Symbols.GetModules()[0];
var iatBase = mod.BaseAddress + 0x8000UL;
var handles = new List<uint>();

for (ulong off = 0; off < 0x800; off += 8)
{
    var funcAddr = ReadPtr(iatBase + off);
    if (funcAddr == 0) continue;

    var sym = Sym(funcAddr);
    if (sym == null) continue;

    var h = api.Breakpoints.SetBreakpoint(
        api.TargetPid, 0, funcAddr,
        PluginBreakpointType.Software);
    if (h != null) handles.Add(h.Value);
}

print($"Установлено {handles.Count} BP на IAT-функции");

// Обработчик: логировать имя функции и продолжить
api.OnDebugEventFilter += evt =>
{
    var sym = Sym(evt.Address);
    if (sym != null && sym.Contains("!"))
    {
        print($"[IAT] {sym}  RCX=0x{Reg("RCX"):X016} RDX=0x{Reg("RDX"):X016}");
        api.Continue();
        return true;
    }
    return false;
};
```

---

## 19. Распаковка (Unpacking)

Сценарии для автоматической и полуавтоматической распаковки упакованных PE-файлов.

### Определение OEP (Original Entry Point)

```csharp
// Метод 1: Отслеживание записи в секцию .text
// Пакеры распаковывают код в .text, затем прыгают туда
var mod = api.Symbols.GetModules()[0];
var textBase = mod.BaseAddress + 0x1000UL; // .text обычно 0x1000 RVA

// Ставим memory BP на .text
api.Breakpoints.SetBreakpoint(
    api.TargetPid, 0, textBase,
    PluginBreakpointType.Memory);

print($"Memory BP на .text @ 0x{textBase:X016}");
print("Запустите выполнение (F9). При переходе на OEP сработает BP.");
```

```csharp
// Метод 2: Эвристика "pushad/popad"
// Многие пакеры (UPX) начинают с PUSHAD и заканчивают POPAD + JMP OEP
// Ставим hardware BP на ESP после PUSHAD

// Предполагаем, что мы стоим на entry point пакера
var rsp = Reg("RSP");
// После PUSHAD RSP уменьшится на 0x40 (8 регистров * 8 байт)
api.Breakpoints.SetBreakpoint(
    api.TargetPid, api.SelectedThreadId,
    rsp, // BP на текущий RSP
    PluginBreakpointType.HwReadWrite,
    8); // 8 байт

print($"HW BP на RSP (0x{rsp:X016})");
print("После POPAD сработает BP. Следующая инструкция -- JMP OEP.");
```

```csharp
// Метод 3: Ожидание выхода из секции пакера
var mod = api.Symbols.GetModules()[0];
var codeStart = mod.BaseAddress + 0x1000UL;
var codeEnd = codeStart + 0x5000UL; // размер .text

api.OnDebugEventFilter += evt =>
{
    if (evt.Type != PluginDebugEventType.SingleStep) return false;

    if (evt.Address >= codeStart && evt.Address < codeEnd)
    {
        // Мы в секции .text -- это OEP!
        print($"=== OEP найден: 0x{evt.Address:X016} ===");
        api.UI.NavigateDisassembly(evt.Address);
        api.Symbols.RegisterFunction(evt.Address, "OEP", 0x100);
        return false; // остановиться в UI
    }

    api.SingleStep();
    return true;
};
api.SingleStep(); // начинаем трассировку
```

### Дамп распакованного модуля

```csharp
// Дамп PE из памяти в файл (после распаковки)
var mod = api.Symbols.GetModules()[0];
var dump = ReadMem(mod.BaseAddress, mod.Size);
if (dump == null)
{
    print("Ошибка: не удалось прочитать весь модуль");
    return;
}

// Фиксим PE-заголовок: SizeOfImage, секции
// (минимальный фикс: записываем реальный ImageBase)
var peOffset = BitConverter.ToUInt32(dump, 0x3C);
if (BitConverter.ToUInt16(dump, (int)peOffset + 24) == 0x20B)
{
    // PE32+: ImageBase @ Optional Header + 24
    BitConverter.GetBytes(mod.BaseAddress).CopyTo(dump, (int)peOffset + 24 + 24);
}

File.WriteAllBytes(@"C:\Temp\unpacked.exe", dump);
print($"Дамп сохранён: C:\\Temp\\unpacked.exe ({dump.Length} байт)");
```

### Добавление распакованного модуля в UI

```csharp
// После нахождения OEP -- добавить распакованный модуль для анализа
var oep = Reg("RIP");
var mod = api.Symbols.GetModules()[0];

api.UI.AddUnpackedModule(mod.BaseAddress, "unpacked_main");
print($"Модуль добавлен. OEP = 0x{oep:X016}");

// Также именуем OEP
api.Symbols.RegisterFunction(oep, "OEP_main", 0x200);
```

### UPX-распаковка (полный сценарий)

```csharp
// Полный UPX unpack workflow
print("=== UPX Unpacker ===");
var mod = api.Symbols.GetModules()[0];
print($"Модуль: {mod.Name} @ 0x{mod.BaseAddress:X016}");

// 1. Ищем секцию UPX1 (куда распаковываются данные)
var peOffset = ReadU32(mod.BaseAddress + 0x3C);
var numSections = BitConverter.ToUInt16(ReadMem(mod.BaseAddress + peOffset + 6, 2), 0);
var sizeOptHeader = BitConverter.ToUInt16(ReadMem(mod.BaseAddress + peOffset + 20, 2), 0);
var sectStart = mod.BaseAddress + peOffset + 24 + sizeOptHeader;

ulong upx0Base = 0, upx0Size = 0;
for (int i = 0; i < numSections; i++)
{
    var sec = ReadMem(sectStart + (ulong)(i * 40), 40);
    var name = Encoding.ASCII.GetString(sec, 0, 8).TrimEnd('\0');
    var va = BitConverter.ToUInt32(sec, 12);
    var vs = BitConverter.ToUInt32(sec, 8);
    print($"  Section: {name,-8}  VA=0x{va:X08}  Size=0x{vs:X08}");

    if (name == "UPX0" || i == 0)
    {
        upx0Base = mod.BaseAddress + va;
        upx0Size = vs;
    }
}

if (upx0Base != 0)
{
    // 2. Ставим memory BP на UPX0 (секция куда распаковывается код)
    api.Breakpoints.SetBreakpoint(api.TargetPid, 0, upx0Base, PluginBreakpointType.Memory);
    print($"\n[+] Memory BP на UPX0 @ 0x{upx0Base:X016}");
    print("Нажмите F9. При переходе на OEP сработает breakpoint.");
}
```

---

## 20. Сканирование и поиск в памяти

Поиск байтовых паттернов, строк и структур в памяти процесса.

### Поиск байтового паттерна

```csharp
// Поиск паттерна в памяти (с wildcard "??")
List<ulong> ScanPattern(ulong start, uint size, string pattern)
{
    var results = new List<ulong>();
    var data = ReadMem(start, size);
    if (data == null) return results;

    var parts = pattern.Split(' ');
    var bytes = new byte[parts.Length];
    var mask = new bool[parts.Length];

    for (int i = 0; i < parts.Length; i++)
    {
        if (parts[i] == "??" || parts[i] == "?")
        {
            mask[i] = false;
            bytes[i] = 0;
        }
        else
        {
            mask[i] = true;
            bytes[i] = Convert.ToByte(parts[i], 16);
        }
    }

    for (int i = 0; i <= data.Length - parts.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < parts.Length; j++)
        {
            if (mask[j] && data[i + j] != bytes[j])
            {
                match = false;
                break;
            }
        }
        if (match)
            results.Add(start + (ulong)i);
    }
    return results;
}

// Пример: поиск "mov rax, [rcx+0x??]" (48 8B 41 ??)
var mod = api.Symbols.GetModules()[0];
var hits = ScanPattern(mod.BaseAddress, mod.Size, "48 8B 41 ??");
print($"Найдено {hits.Count} совпадений:");
foreach (var addr in hits.Take(20))
    print($"  0x{addr:X016}  {Sym(addr) ?? ""}");
```

### Поиск строки в памяти

```csharp
// Поиск ASCII строки в памяти
List<ulong> ScanString(ulong start, uint size, string searchStr)
{
    var results = new List<ulong>();
    var data = ReadMem(start, size);
    if (data == null) return results;

    var pattern = Encoding.ASCII.GetBytes(searchStr);

    for (int i = 0; i <= data.Length - pattern.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < pattern.Length; j++)
        {
            if (data[i + j] != pattern[j]) { match = false; break; }
        }
        if (match) results.Add(start + (ulong)i);
    }
    return results;
}

// Поиск строки "password" во всех модулях
foreach (var mod in api.Symbols.GetModules())
{
    var hits = ScanString(mod.BaseAddress, mod.Size, "password");
    foreach (var addr in hits)
        print($"0x{addr:X016} [{mod.Name}] \"{ReadString(addr, 64)}\"");
}
```

### Поиск Unicode строки

```csharp
// Поиск Unicode строки
List<ulong> ScanWString(ulong start, uint size, string searchStr)
{
    var results = new List<ulong>();
    var data = ReadMem(start, size);
    if (data == null) return results;

    var pattern = Encoding.Unicode.GetBytes(searchStr);

    for (int i = 0; i <= data.Length - pattern.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < pattern.Length; j++)
        {
            if (data[i + j] != pattern[j]) { match = false; break; }
        }
        if (match) results.Add(start + (ulong)i);
    }
    return results;
}

// Пример:
var hits = ScanWString(
    api.Symbols.GetModules()[0].BaseAddress,
    api.Symbols.GetModules()[0].Size,
    "kernel32.dll");
foreach (var addr in hits)
    print($"0x{addr:X016}: \"{ReadWString(addr, 64)}\"");
```

### Поиск указателей на известные функции

```csharp
// Поиск xref'ов: где в модуле хранится указатель на данную функцию
ulong targetFunc = Addr("kernel32!VirtualAlloc");
var targetBytes = BitConverter.GetBytes(targetFunc);

var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

for (int i = 0; i <= data.Length - 8; i++)
{
    if (data[i]   == targetBytes[0] && data[i+1] == targetBytes[1] &&
        data[i+2] == targetBytes[2] && data[i+3] == targetBytes[3] &&
        data[i+4] == targetBytes[4] && data[i+5] == targetBytes[5] &&
        data[i+6] == targetBytes[6] && data[i+7] == targetBytes[7])
    {
        var addr = mod.BaseAddress + (ulong)i;
        print($"Xref to VirtualAlloc @ 0x{addr:X016}  (RVA 0x{i:X08})");
    }
}
```

### Сканирование с чанками (для больших регионов)

```csharp
// Сканирование больших регионов памяти чанками по 64 KB
List<ulong> ScanLargeRegion(ulong start, ulong totalSize, byte[] pattern)
{
    var results = new List<ulong>();
    uint chunkSize = 0x10000; // 64 KB
    ulong overlap = (ulong)pattern.Length - 1;

    for (ulong offset = 0; offset < totalSize; offset += chunkSize - overlap)
    {
        uint readSize = (uint)Math.Min(chunkSize, totalSize - offset);
        var data = ReadMem(start + offset, readSize);
        if (data == null) continue;

        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) results.Add(start + offset + (ulong)i);
        }
    }
    return results;
}
```

### Поиск изменённых байт (сравнение снимков)

```csharp
// Снимок памяти -> запуск -> сравнение
var mod = api.Symbols.GetModules()[0];
var snapshot = ReadMem(mod.BaseAddress, mod.Size);
print($"Снимок сделан ({snapshot.Length} байт). Нажмите F9, сделайте действие, F12.");

// После остановки:
// var now = ReadMem(mod.BaseAddress, mod.Size);
// for (int i = 0; i < snapshot.Length; i++)
//     if (snapshot[i] != now[i])
//         print($"+0x{i:X06}: 0x{snapshot[i]:X02} -> 0x{now[i]:X02}");
```

---

## 21. Декомпиляция

KernelFlirt интегрирует декомпилятор (RetDec) для получения C-псевдокода из бинарного кода.

### Базовая декомпиляция

```csharp
// Декомпилировать функцию по текущему RIP
api.UI.DecompileFunction(Reg("RIP"));
print("Декомпиляция запущена...");

// Подождать и получить результат
await Task.Delay(3000);
var code = api.UI.GetDecompiledCode();
if (string.IsNullOrEmpty(code))
{
    print("Декомпиляция ещё не завершена или не удалась");
}
else
{
    print("=== Декомпилированный код ===");
    print(code);
}
```

### Декомпиляция нескольких функций

```csharp
// Декомпилировать все зарегистрированные функции и сохранить в файл
var sb = new StringBuilder();
foreach (var func in api.Symbols.GetRegisteredFunctions())
{
    sb.AppendLine($"// ====== {func.Name} @ 0x{func.Address:X016} ======");
    api.UI.DecompileFunction(func.Address);
    await Task.Delay(3000); // ждём декомпиляцию
    var code = api.UI.GetDecompiledCode();
    sb.AppendLine(code);
    sb.AppendLine();
}
File.WriteAllText(@"C:\Temp\decompiled.c", sb.ToString());
print("Все функции декомпилированы и сохранены в C:\\Temp\\decompiled.c");
```

### Декомпиляция + автоименование

```csharp
// Декомпилировать функцию, проанализировать строковые ссылки, дать имя
var rip = Reg("RIP");
api.UI.DecompileFunction(rip);
await Task.Delay(3000);
var code = api.UI.GetDecompiledCode();

// Ищем строковые литералы в декомпилированном коде
var strings = new List<string>();
int idx = 0;
while ((idx = code.IndexOf('"', idx)) >= 0)
{
    int end = code.IndexOf('"', idx + 1);
    if (end > idx)
    {
        strings.Add(code.Substring(idx + 1, end - idx - 1));
        idx = end + 1;
    }
    else break;
}

if (strings.Count > 0)
{
    // Используем первую значимую строку для имени
    var firstStr = strings.First(s => s.Length > 3);
    var funcName = "fn_" + new string(firstStr
        .Where(c => char.IsLetterOrDigit(c) || c == '_')
        .Take(30).ToArray());
    api.Symbols.RegisterFunction(rip, funcName, 0x100);
    print($"Функция @ 0x{rip:X016} названа: {funcName}");
    print($"Найденные строки: {string.Join(", ", strings.Select(s => $"\"{s}\""))}");
}
```

---

## 22. Файловый ввод-вывод

В скриптах доступны все классы `System.IO`. Пространство имён `System.IO` импортировано автоматически.

### Дамп памяти в файл

```csharp
// Дамп региона памяти
var addr = Reg("RIP") & ~0xFFFUL; // выровнять по странице
var data = ReadMem(addr, 0x1000); // 4 KB
File.WriteAllBytes(@"C:\Temp\page_dump.bin", data);
print($"Дамп страницы 0x{addr:X016} сохранён");
```

### Загрузка патча из файла

```csharp
// Загрузить бинарный файл и записать в память
var patch = File.ReadAllBytes(@"C:\Temp\shellcode.bin");
var mem = api.Memory.AllocateMemory(api.TargetPid, (ulong)patch.Length + 0x100);
WriteMem(mem, patch);
print($"Загружено {patch.Length} байт @ 0x{mem:X016}");
```

### Полный дамп модуля

```csharp
// Дамп модуля чанками (для больших модулей)
var mod = api.Symbols.GetModules()[0];
uint chunkSize = 0x10000; // 64 KB
using var fs = File.Create(@"C:\Temp\full_dump.bin");

for (ulong offset = 0; offset < mod.Size; offset += chunkSize)
{
    uint readSize = (uint)Math.Min(chunkSize, mod.Size - (uint)offset);
    var chunk = ReadMem(mod.BaseAddress + offset, readSize);
    if (chunk != null)
        fs.Write(chunk, 0, chunk.Length);
    else
        fs.Write(new byte[readSize], 0, (int)readSize); // нули для нечитаемых страниц
}
print($"Дамп {mod.Name} завершён ({mod.Size} байт)");
```

### Логирование в файл

```csharp
// Логирование трассировки в файл
var logFile = @"C:\Temp\trace.log";
var sw = new StreamWriter(logFile, append: true);

api.OnDebugEvent += evt =>
{
    sw.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {evt.Type,-15}  " +
                 $"0x{evt.Address:X016}  {Sym(evt.Address) ?? ""}");
    sw.Flush();
};
print($"Трассировка пишется в {logFile}");
```

### Загрузка скрипта из файла

```csharp
// Загрузить и выполнить скрипт из файла
var scriptPath = @"C:\Temp\my_analysis.csx";
var scriptCode = File.ReadAllText(scriptPath);
print($"Загружен скрипт: {scriptPath} ({scriptCode.Length} символов)");
// Скопируйте код в REPL и выполните, или используйте MCP execute_script
```

---

## 23. Межплагинное взаимодействие

Плагины могут обмениваться данными через `api.UI.SetPluginData` / `GetPluginData`. Это единственный способ взаимодействия между плагинами.

### Сохранение данных

```csharp
// Сохранить результаты анализа для другого плагина/скрипта
var analysisResult = new Dictionary<ulong, string>
{
    { 0x7FF6A0001000, "DecryptPayload" },
    { 0x7FF6A0002000, "CheckLicense" },
    { 0x7FF6A0003000, "AntiDebug" },
};
api.UI.SetPluginData("analysis.functions", analysisResult);
print("Данные сохранены для межплагинного обмена");
```

### Чтение данных

```csharp
// Прочитать данные от другого плагина
var functions = api.UI.GetPluginData("analysis.functions")
    as Dictionary<ulong, string>;
if (functions != null)
{
    foreach (var (addr, name) in functions)
        print($"0x{addr:X016}: {name}");
}
else
{
    print("Данные не найдены");
}
```

### Вызов скрипта через MCP (из AI-ассистента)

```csharp
// Scripting-плагин сохраняет функцию ScriptExecute для MCP:
// api.UI.SetPluginData("ScriptExecute", executeScript);

// AI-ассистент может вызвать скрипт через MCP-инструмент execute_script.
// Это позволяет AI анализировать бинарник, используя C# скрипты.
```

### Флаги и состояния

```csharp
// Использование plugin data как флагов
api.UI.SetPluginData("unpacker.oepFound", true);
api.UI.SetPluginData("unpacker.oepAddress", Reg("RIP"));

// В другом скрипте:
var found = (bool?)api.UI.GetPluginData("unpacker.oepFound") ?? false;
if (found)
{
    var oep = (ulong)api.UI.GetPluginData("unpacker.oepAddress");
    print($"OEP уже найден: 0x{oep:X016}");
}
```

---

## 24. Рецепты

Готовые сценарии для типичных задач реверс-инжиниринга.

### Рецепт 1: Полный дамп информации о процессе

```csharp
// Всё о целевом процессе в одном скрипте
print("=== Информация о процессе ===");
print($"PID:        {api.TargetPid}");
print($"TID:        {api.SelectedThreadId}");
print($"Is32Bit:    {api.Is32Bit}");

var (peb, peb32) = api.Process.GetPebAddress(api.TargetPid);
print($"PEB:        0x{peb:X016}");

var threads = api.Process.EnumThreads(api.TargetPid);
print($"Потоков:    {threads.Count}");

var modules = api.Symbols.GetModules();
print($"Модулей:    {modules.Count}");
print($"\n--- Модули ---");
foreach (var m in modules)
    print($"  0x{m.BaseAddress:X016}  {m.Size,10:X}  {m.Name}");

print($"\n--- Потоки ---");
foreach (var t in threads)
    print($"  TID={t.ThreadId,6}  Start=0x{t.StartAddress:X016}  State={t.State}  Prio={t.Priority}");

print($"\n--- Регистры (текущий поток) ---");
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"  {r.Name,-6} = 0x{r.Value:X016}");

var flagStr = string.Join(" ", regs.Where(r => r.IsFlag && r.Value != 0).Select(r => r.Name));
print($"  Флаги: {flagStr}");
```

### Рецепт 2: Мониторинг CreateFile

```csharp
// Логирование всех вызовов CreateFileW
var target = Addr("kernel32!CreateFileW");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != target) return false;

    var fileName = ReadWString(Reg("RCX"), 260);
    var access   = (uint)Reg("RDX");
    var share    = (uint)Reg("R8");
    var creation = ReadU32(Reg("RSP") + 0x30); // 5-й параметр

    string accessStr = "";
    if ((access & 0x80000000) != 0) accessStr += "GENERIC_READ ";
    if ((access & 0x40000000) != 0) accessStr += "GENERIC_WRITE ";

    string createStr = creation switch {
        1 => "CREATE_NEW", 2 => "CREATE_ALWAYS", 3 => "OPEN_EXISTING",
        4 => "OPEN_ALWAYS", 5 => "TRUNCATE_EXISTING", _ => $"0x{creation:X}"
    };

    print($"CreateFileW(\"{fileName}\", {accessStr.Trim()}, Share=0x{share:X}, {createStr})");

    api.Continue();
    return true;
};
```

### Рецепт 3: Мониторинг RegOpenKey

```csharp
// Логирование доступа к реестру
var regOpen = Addr("advapi32!RegOpenKeyExW");
if (regOpen == 0) regOpen = Addr("kernelbase!RegOpenKeyExW");

api.Breakpoints.SetBreakpoint(api.TargetPid, 0, regOpen, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != regOpen) return false;
    var keyName = ReadWString(Reg("RDX"), 512);
    print($"RegOpenKeyExW: {keyName}");
    api.Continue();
    return true;
};
```

### Рецепт 4: Мониторинг сетевых соединений

```csharp
// Логирование connect()
var connectAddr = Addr("ws2_32!connect");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, connectAddr, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != connectAddr) return false;

    var sockaddr = Reg("RDX");
    var family = BitConverter.ToUInt16(ReadMem(sockaddr, 2), 0);

    if (family == 2) // AF_INET
    {
        var port = (ushort)((ReadMem(sockaddr + 2, 2)[0] << 8) | ReadMem(sockaddr + 2, 2)[1]);
        var ip = ReadMem(sockaddr + 4, 4);
        print($"connect() -> {ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}:{port}");
    }
    else
    {
        print($"connect() -> family={family}");
    }

    api.Continue();
    return true;
};
```

### Рецепт 5: Мониторинг VirtualAlloc

```csharp
// Отслеживание выделения памяти (часто используется пакерами)
var va = Addr("kernel32!VirtualAlloc");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, va, PluginBreakpointType.Software);

var allocations = new List<(ulong addr, ulong size, uint prot)>();

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != va) return false;

    var size = Reg("RDX");
    var allocType = (uint)Reg("R8");
    var protect = (uint)Reg("R9");

    string protStr = protect switch {
        0x04 => "RW", 0x10 => "X", 0x20 => "RX", 0x40 => "RWX", _ => $"0x{protect:X}"
    };
    print($"VirtualAlloc(size=0x{size:X}, type=0x{allocType:X}, prot={protStr})");

    // Записываем для последующего анализа
    allocations.Add((0, size, protect));

    api.Continue();
    return true;
};
```

### Рецепт 6: Мониторинг VirtualProtect

```csharp
// Отслеживание изменений защиты памяти
var vp = Addr("kernel32!VirtualProtect");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, vp, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != vp) return false;

    var addr = Reg("RCX");
    var size = Reg("RDX");
    var newProt = (uint)Reg("R8");

    string protStr = newProt switch {
        0x04 => "RW", 0x10 => "X", 0x20 => "RX", 0x40 => "RWX",
        0x02 => "RO", 0x01 => "NOACCESS", _ => $"0x{newProt:X}"
    };

    print($"VirtualProtect(0x{addr:X016}, 0x{size:X}, {protStr})");

    // RWX на .text? Вероятно self-modifying code или распаковка
    if (newProt == 0x40)
        api.Log.Warning($"RWX protect на 0x{addr:X016} -- возможна распаковка!");

    api.Continue();
    return true;
};
```

### Рецепт 7: Мониторинг LoadLibrary

```csharp
// Отслеживание загрузки DLL
var ll = Addr("kernel32!LoadLibraryW");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, ll, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != ll) return false;
    var dllName = ReadWString(Reg("RCX"), 260);
    print($"LoadLibraryW(\"{dllName}\")");
    api.Continue();
    return true;
};

// И LoadLibraryA
var lla = Addr("kernel32!LoadLibraryA");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, lla, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != lla) return false;
    var dllName = ReadString(Reg("RCX"), 260);
    print($"LoadLibraryA(\"{dllName}\")");
    api.Continue();
    return true;
};
```

### Рецепт 8: Мониторинг GetProcAddress

```csharp
// Отслеживание динамического разрешения функций
var gpa = Addr("kernel32!GetProcAddress");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, gpa, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != gpa) return false;

    var hModule = Reg("RCX");
    var procName = Reg("RDX");

    string name;
    if (procName < 0x10000) // ординал
        name = $"Ordinal #{procName}";
    else
        name = ReadString(procName, 256);

    // Пробуем определить модуль
    var mod = api.Symbols.GetModules()
        .FirstOrDefault(m => hModule >= m.BaseAddress && hModule < m.BaseAddress + m.Size);
    var modName = mod?.Name ?? $"0x{hModule:X016}";

    print($"GetProcAddress({modName}, \"{name}\")");
    api.Continue();
    return true;
};
```

### Рецепт 9: Дамп vtable с именами

```csharp
// Дамп виртуальной таблицы COM/C++ объекта
var objPtr = Reg("RCX"); // указатель на объект
var vtable = ReadPtr(objPtr);
print($"Объект @ 0x{objPtr:X016}");
print($"VTable @ 0x{vtable:X016}\n");

print($"{"Индекс",7}  {"Адрес",18}  {"Символ"}");
print(new string('-', 60));

for (int i = 0; i < 30; i++)
{
    var funcAddr = ReadPtr(vtable + (ulong)(i * 8));
    if (funcAddr == 0) break;

    var sym = Sym(funcAddr) ?? "???";
    print($"[{i,4}]   0x{funcAddr:X016}  {sym}");

    // Именуем неизвестные методы
    if (Sym(funcAddr) == null)
        api.Symbols.RegisterFunction(funcAddr, $"vfunc_{i}", 0x50);
}
```

### Рецепт 10: Поиск строк во всех модулях

```csharp
// Найти все ASCII строки (8+ символов) в главном модуле
var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);
if (data == null) return;

var strings = new List<(ulong addr, string str)>();
int start = -1;

for (int i = 0; i < data.Length; i++)
{
    bool printable = data[i] >= 0x20 && data[i] < 0x7F;
    if (printable && start == -1) start = i;
    if ((!printable || i == data.Length - 1) && start != -1)
    {
        int len = i - start;
        if (len >= 8)
        {
            var s = Encoding.ASCII.GetString(data, start, len);
            strings.Add((mod.BaseAddress + (ulong)start, s));
        }
        start = -1;
    }
}

print($"Найдено {strings.Count} строк (>= 8 символов):\n");
foreach (var (addr, s) in strings.Take(100))
    print($"0x{addr:X016}: \"{s}\"");
```

### Рецепт 11: Обнаружение self-modifying code

```csharp
// Обнаружение самомодифицирующегося кода через hardware watchpoint
var mod = api.Symbols.GetModules()[0];
var textAddr = mod.BaseAddress + 0x1000; // .text секция

// Ставим HW write watchpoint на начало .text
var h = api.Breakpoints.SetBreakpoint(
    api.TargetPid, api.SelectedThreadId,
    textAddr, PluginBreakpointType.HwWrite, 8);

api.OnDebugEventFilter += evt =>
{
    if (evt.Type != PluginDebugEventType.HwWatchpoint) return false;

    print($"!!! Запись в .text @ 0x{evt.Address:X016}");
    print($"    RIP: 0x{Reg("RIP"):X016}  {Sym(Reg("RIP")) ?? "???"}");
    print($"    Caller вероятно: пакер/протектор");
    return false; // остановить в UI
};
```

### Рецепт 12: Перехват MessageBox

```csharp
// Перехват и модификация MessageBox (изменить текст)
var mb = Addr("user32!MessageBoxW");
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, mb, PluginBreakpointType.Software);

api.OnDebugEventFilter += evt =>
{
    if (evt.Address != mb) return false;

    var hwnd = Reg("RCX");
    var text = ReadWString(Reg("RDX"), 1024);
    var caption = ReadWString(Reg("R8"), 256);
    var type = (uint)Reg("R9");

    print($"MessageBoxW(\"{caption}\", \"{text}\", type=0x{type:X})");

    // Можно подменить текст:
    // var newText = Encoding.Unicode.GetBytes("Patched!\0");
    // var mem = api.Memory.AllocateMemory(api.TargetPid, (ulong)newText.Length + 2);
    // WriteMem(mem, newText);
    // // RDX = указатель на новый текст... но WriteRegister нет,
    // // поэтому используем шеллкод или NewRip+NewRsp

    return false; // показать в UI
};
```

### Рецепт 13: Подсчёт вызовов функции

```csharp
// Статистика вызовов API
var counters = new Dictionary<string, int>();
var apiList = new[] {
    "kernel32!CreateFileW", "kernel32!ReadFile", "kernel32!WriteFile",
    "kernel32!CloseHandle", "kernel32!VirtualAlloc", "kernel32!VirtualFree",
    "ntdll!NtAllocateVirtualMemory", "ntdll!NtFreeVirtualMemory"
};

foreach (var name in apiList)
{
    var addr = Addr(name);
    if (addr == 0) continue;
    counters[name] = 0;
    api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
}

api.OnDebugEventFilter += evt =>
{
    var sym = Sym(evt.Address);
    if (sym != null && counters.ContainsKey(sym))
    {
        counters[sym]++;
        api.Continue();
        return true;
    }
    return false;
};

// Для просмотра статистики в любой момент:
// foreach (var (name, count) in counters.OrderByDescending(kv => kv.Value))
//     print($"{name,-40} {count,6}");
```

### Рецепт 14: Поиск crypto-констант

```csharp
// Поиск известных криптографических констант в памяти
var cryptoPatterns = new Dictionary<string, byte[]>
{
    // AES S-Box (первые 16 байт)
    ["AES S-Box"] = new byte[] { 0x63, 0x7C, 0x77, 0x7B, 0xF2, 0x6B, 0x6F, 0xC5,
                                  0x30, 0x01, 0x67, 0x2B, 0xFE, 0xD7, 0xAB, 0x76 },
    // SHA-256 init values (первые 4 байта первого значения)
    ["SHA-256 H0"] = BitConverter.GetBytes(0x6A09E667U),
    // RC4 key schedule init
    ["RC4 init"] = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                                 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F },
    // RSA public exponent
    ["RSA e=65537"] = new byte[] { 0x01, 0x00, 0x01, 0x00 },
};

var mod = api.Symbols.GetModules()[0];
var data = ReadMem(mod.BaseAddress, mod.Size);

foreach (var (name, pattern) in cryptoPatterns)
{
    for (int i = 0; i <= data.Length - pattern.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < pattern.Length; j++)
        {
            if (data[i + j] != pattern[j]) { match = false; break; }
        }
        if (match)
        {
            var addr = mod.BaseAddress + (ulong)i;
            print($"[CRYPTO] {name} найден @ 0x{addr:X016}");
            api.UI.SetAddressAnnotation(addr, $"[CRYPTO] {name}");
        }
    }
}
api.UI.RefreshDisassembly();
```

### Рецепт 15: Автоматический анализ TLS Callbacks

```csharp
// TLS Callbacks выполняются до entry point
var mod = api.Symbols.GetModules()[0];
var peOffset = ReadU32(mod.BaseAddress + 0x3C);

// TLS Directory -- Data Directory[9]
int ddBase = 24 + (BitConverter.ToUInt16(ReadMem(mod.BaseAddress + peOffset + 24, 2), 0) == 0x20B ? 112 : 96);
var tlsDD = ddBase + 9 * 8;
var tlsRVA = ReadU32(mod.BaseAddress + peOffset + (uint)tlsDD);

if (tlsRVA == 0) { print("TLS Directory не найден"); return; }

var tlsDir = ReadMem(mod.BaseAddress + tlsRVA, 40);
// AddressOfCallBacks (offset 24 на PE32+)
var callbacksPtr = BitConverter.ToUInt64(tlsDir, 24);
print($"TLS Callbacks array @ 0x{callbacksPtr:X016}");

for (int i = 0; i < 10; i++)
{
    var cbAddr = ReadPtr(callbacksPtr + (ulong)(i * 8));
    if (cbAddr == 0) break;

    print($"TLS Callback [{i}] @ 0x{cbAddr:X016}  {Sym(cbAddr) ?? ""}");
    api.Symbols.RegisterFunction(cbAddr, $"TlsCallback_{i}", 0x100);

    // Ставим BP чтобы перехватить
    api.Breakpoints.SetBreakpoint(api.TargetPid, 0, cbAddr, PluginBreakpointType.Software);
}
```

### Рецепт 16: Сравнение двух дампов памяти

```csharp
// Сравнить два файла-дампа (например, до и после распаковки)
var dump1 = File.ReadAllBytes(@"C:\Temp\before.bin");
var dump2 = File.ReadAllBytes(@"C:\Temp\after.bin");

int minLen = Math.Min(dump1.Length, dump2.Length);
int diffCount = 0;

print($"Размер 1: {dump1.Length}  Размер 2: {dump2.Length}");
print($"{"Смещение",10}  {"До",5}  {"После",5}  {"Контекст"}");
print(new string('-', 50));

for (int i = 0; i < minLen; i++)
{
    if (dump1[i] != dump2[i])
    {
        // Контекст: 4 байта до и после
        var before = BitConverter.ToString(dump1, Math.Max(0, i - 2), Math.Min(5, minLen - i));
        var after = BitConverter.ToString(dump2, Math.Max(0, i - 2), Math.Min(5, minLen - i));
        print($"0x{i:X08}  0x{dump1[i]:X02}  0x{dump2[i]:X02}  [{before}] -> [{after}]");
        diffCount++;

        if (diffCount > 100) { print("... (показаны первые 100)"); break; }
    }
}
print($"\nВсего различий: {diffCount}");
```

### Рецепт 17: Перечисление обработчиков SEH

```csharp
// Обход цепочки SEH (Structured Exception Handlers) на x64
// На x64 SEH основан на таблицах (.pdata), а не на стеке

var mod = api.Symbols.GetModules()[0];
var peOffset = ReadU32(mod.BaseAddress + 0x3C);

// Exception Directory -- Data Directory[3]
int ddBase = 24 + 112; // PE32+
var excDD = ddBase + 3 * 8;
var excRVA = ReadU32(mod.BaseAddress + peOffset + (uint)excDD);
var excSize = ReadU32(mod.BaseAddress + peOffset + (uint)excDD + 4);

if (excRVA == 0) { print("Exception directory не найден"); return; }

var numEntries = excSize / 12; // RUNTIME_FUNCTION = 12 байт
print($"Exception handlers: {numEntries} записей\n");
print($"{"#",5}  {"BeginRVA",10}  {"EndRVA",10}  {"UnwindRVA",10}  {"Символ"}");
print(new string('-', 65));

for (uint i = 0; i < Math.Min(numEntries, 50); i++)
{
    var entry = ReadMem(mod.BaseAddress + excRVA + i * 12, 12);
    var beginRVA = BitConverter.ToUInt32(entry, 0);
    var endRVA = BitConverter.ToUInt32(entry, 4);
    var unwindRVA = BitConverter.ToUInt32(entry, 8);

    var sym = Sym(mod.BaseAddress + beginRVA) ?? "";
    print($"{i,5}  0x{beginRVA:X08}  0x{endRVA:X08}  0x{unwindRVA:X08}  {sym}");
}
```

### Рецепт 18: Автоменю -- добавление пунктов меню

```csharp
// Добавить пункт меню "Dump Module" в главное меню
api.UI.AddMenuItem("Dump Module", () =>
{
    var mod = api.Symbols.GetModules()[0];
    var data = api.Memory.ReadMemory(api.TargetPid, mod.BaseAddress, mod.Size);
    if (data != null)
    {
        var path = $@"C:\Temp\{mod.Name}_dump.bin";
        File.WriteAllBytes(path, data);
        api.Log.Info($"Модуль {mod.Name} сдамплен в {path}");
    }
});
print("Пункт меню добавлен");
```

---

## 25. Советы и подводные камни

### 1. Проверяйте IsBreakState

Чтение/запись памяти и регистров работают **только** когда процесс остановлен.

```csharp
// Всегда проверяйте:
if (!api.IsBreakState) { print("Процесс не остановлен!"); return; }
```

### 2. ReadMem может вернуть null

```csharp
var data = ReadMem(addr, size);
if (data == null)
{
    print($"Не удалось прочитать 0x{addr:X016}");
    return;
}
```

### 3. Не забывайте размер при RegisterFunction

Без размера `ResolveAddress` не сможет определить, что адрес внутри функции:

```csharp
// ПЛОХО: размер не указан
api.Symbols.RegisterFunction(0x1000, "MyFunc");
Sym(0x1010); // => null (адрес не внутри функции!)

// ХОРОШО: размер указан
api.Symbols.RegisterFunction(0x1000, "MyFunc", 0x100);
Sym(0x1010); // => "MyFunc+0x10"
```

### 4. Осторожно с OnDebugEventFilter

- Верните `true` **только** если вы обработали событие (вызвали `Continue()` / `SingleStep()` и т.д.).
- Если вернёте `true` без вызова continue-метода, процесс останется приостановленным навсегда.

```csharp
// ПЛОХО: return true без Continue
api.OnDebugEventFilter += evt =>
{
    print("breakpoint!");
    return true; // процесс заблокирован навсегда!
};

// ХОРОШО:
api.OnDebugEventFilter += evt =>
{
    print("breakpoint!");
    api.Continue(); // продолжить выполнение
    return true;
};
```

### 5. Множественные подписки на события

Каждый вызов `api.OnDebugEventFilter += ...` добавляет НОВЫЙ обработчик. Предыдущие НЕ удаляются.

```csharp
// После 5 запусков скрипта у вас будет 5 обработчиков!
// Решение: используйте переменные для контроля
bool handlerInstalled = false;
if (!handlerInstalled)
{
    api.OnDebugEventFilter += evt => { ... };
    handlerInstalled = true;
}
```

Или используйте **Reset State** для сброса всего.

### 6. Ограничения на чтение больших блоков

Драйвер может иметь ограничение буфера. Читайте чанками:

```csharp
// Чтение большого региона чанками по 64 KB
var fullData = new List<byte>();
uint chunkSize = 0x10000;
for (ulong offset = 0; offset < totalSize; offset += chunkSize)
{
    var chunk = ReadMem(baseAddr + offset, 
        (uint)Math.Min(chunkSize, totalSize - offset));
    if (chunk == null) break;
    fullData.AddRange(chunk);
}
```

### 7. WriteRip vs WriteRipAndRsp

`WriteRip` принимает `pid` и `tid`, а `WriteRipAndRsp` -- только `tid` (без `pid`). Не перепутайте сигнатуры.

```csharp
// WriteRip: 3 параметра (pid, tid, newRip)
api.Memory.WriteRip(api.TargetPid, api.SelectedThreadId, newRip);

// WriteRipAndRsp: 3 параметра (tid, newRip, newRsp) -- БЕЗ pid!
api.Memory.WriteRipAndRsp(api.SelectedThreadId, newRip, newRsp);
```

### 8. Порядок событий при break

```
1. OnDebugEventFilter (плагин может подавить)
2. OnDebugEvent (наблюдение)
3. OnBreakStateEntered (UI обновляется)
```

### 9. Async/await поддерживается

```csharp
// Можно использовать async/await
await Task.Delay(1000);
print("Прошла секунда");

// Но не делайте так в OnDebugEventFilter -- он синхронный!
```

### 10. Console.WriteLine перенаправлен

```csharp
// Console.WriteLine работает и выводит в output-панель скриптинга
Console.WriteLine("Hello from Console");
// Эквивалентно print("Hello from Console")
```

### 11. Возвращаемые значения показываются автоматически

```csharp
// Последнее выражение показывается в output
Reg("RAX")
// => 0x00007FF6A0001234

ReadMem(Reg("RIP"), 4)
// => "48 89 5C 24"
```

### 12. Типизация byte[]

Шорткат `ReadMem` возвращает `byte[]?`. Если нужен гарантированный результат:

```csharp
var data = ReadMem(addr, 16) ?? throw new Exception("Ошибка чтения");
```

### 13. Множественные BP на одном адресе

Не ставьте несколько software BP на один адрес -- они конфликтуют (один удалит другой).

### 14. Kernel vs User mode адреса

```csharp
// Ядерные адреса: 0xFFFFF... (верхняя половина)
// User-mode: 0x00007FF... (нижняя половина)
bool isKernel = Reg("RIP") > 0x7FFFFFFFFFFF;
print($"Режим: {(isKernel ? "Kernel" : "User")}");
```

### 15. Работа с указателями

При разыменовании цепочки указателей проверяйте каждый уровень:

```csharp
var ptr = ReadPtr(addr);
if (ptr == 0) { print("Null pointer!"); return; }
var ptr2 = ReadPtr(ptr + 0x10);
if (ptr2 == 0) { print("Null at +0x10!"); return; }
// и т.д.
```

---

## 26. REPL -- особенности и приёмы

### Персистентность переменных

Переменные объявленные в одном запуске живут до конца сессии или **Reset State**:

```csharp
// Запуск 1:
var counter = 0;
var addresses = new List<ulong>();
```

```csharp
// Запуск 2 (переменные доступны):
counter++;
addresses.Add(Reg("RIP"));
print($"Counter: {counter}, Addresses: {addresses.Count}");
```

### Выделение и выполнение фрагмента

Выделите часть кода мышью и нажмите **F5** -- выполнится только выделенный фрагмент. Удобно для:
- Тестирования отдельных строк
- Повторного выполнения одной операции
- Инкрементальной разработки скрипта

### Reset State

**Reset State** (кнопка или команда) сбрасывает:
- Все переменные
- Шорткаты (будут переинициализированы при следующем запуске)
- REPL-состояние Roslyn

Не сбрасывает:
- Установленные breakpoints
- Обработчики событий (они привязаны к `api`, который живёт в плагине)
- Аннотации и зарегистрированные функции

### Обработка ошибок

Ошибки компиляции и runtime-ошибки выводятся в output:

```
Compilation error:
(1,5): error CS0246: The type or namespace name 'xxx' could not be found

Runtime error: NullReferenceException: Object reference not set...
```

### Полезные однострочники

```csharp
// Текущий RIP и символ
$"0x{Reg("RIP"):X016} {Sym(Reg("RIP"))}"

// Быстрый hexdump 16 байт по RIP
BitConverter.ToString(ReadMem(Reg("RIP"), 16))

// Имена всех модулей
string.Join("\n", api.Symbols.GetModules().Select(m => m.Name))

// Количество потоков
api.Process.EnumThreads(api.TargetPid).Count

// Значение по указателю в RCX+0x10
$"0x{ReadPtr(Reg("RCX") + 0x10):X016}"

// Текущий стек (5 значений)
string.Join("\n", Enumerable.Range(0, 5).Select(i => {
    var v = ReadPtr(Reg("RSP") + (ulong)(i*8));
    return $"[RSP+0x{i*8:X02}] = 0x{v:X016} {Sym(v) ?? ""}";
}))
```

### Шаблон скрипта для анализа

```csharp
// === Шаблон скрипта для анализа бинарника ===

// 1. Проверяем подключение
if (!api.IsConnected || !api.IsBreakState)
{
    print("Отладчик не готов!");
    return;
}

// 2. Информация о таргете
var mod = api.Symbols.GetModules()[0];
print($"Модуль: {mod.Name} @ 0x{mod.BaseAddress:X016}  Size=0x{mod.Size:X}");
print($"RIP: 0x{Reg("RIP"):X016}  {Sym(Reg("RIP")) ?? ""}");

// 3. Анализ
// ... ваш код здесь ...

// 4. Результаты
print("\n=== Готово ===");
```

---

## Приложение A: Константы защиты памяти (PAGE_*)

| Константа | Значение | Описание |
|-----------|----------|----------|
| `PAGE_NOACCESS` | `0x01` | Нет доступа |
| `PAGE_READONLY` | `0x02` | Только чтение |
| `PAGE_READWRITE` | `0x04` | Чтение + запись |
| `PAGE_WRITECOPY` | `0x08` | Copy-on-write |
| `PAGE_EXECUTE` | `0x10` | Только выполнение |
| `PAGE_EXECUTE_READ` | `0x20` | Выполнение + чтение |
| `PAGE_EXECUTE_READWRITE` | `0x40` | Полный доступ (RWX) |
| `PAGE_EXECUTE_WRITECOPY` | `0x80` | Execute + copy-on-write |
| `PAGE_GUARD` | `0x100` | Guard page (модификатор) |

## Приложение B: Коды исключений Windows

| Константа | Значение | Описание |
|-----------|----------|----------|
| `STATUS_BREAKPOINT` | `0x80000003` | INT3 (software breakpoint) |
| `STATUS_SINGLE_STEP` | `0x80000004` | Trap flag (single step) |
| `STATUS_ACCESS_VIOLATION` | `0xC0000005` | Access violation |
| `STATUS_GUARD_PAGE_VIOLATION` | `0x80000001` | Guard page |
| `STATUS_STACK_OVERFLOW` | `0xC00000FD` | Переполнение стека |
| `STATUS_INTEGER_DIVIDE_BY_ZERO` | `0xC0000094` | Деление на ноль |
| `STATUS_PRIVILEGED_INSTRUCTION` | `0xC0000096` | Привилегированная инструкция |

## Приложение C: Полная таблица горячих клавиш

| Комбинация | Действие |
|------------|----------|
| **F5** / **Ctrl+Enter** | Выполнить скрипт (или выделенный фрагмент) |
| **F7** | Step Into (шаг в) |
| **F8** | Step Over (шаг через) |
| **F9** | Run / Continue (продолжить) |
| **Ctrl+F8** | Skip Instruction (пропустить) |
| **Ctrl+F9** | Step Out (выйти из функции) |
| **F4** | Run to Cursor |
| **F12** | Pause / Break |

---

*KernelFlirt Scripting Reference -- (c) 2024-2026. Полный справочник по C# скриптингу для ядерного отладчика KernelFlirt.*
