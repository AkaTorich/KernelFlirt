# KernelFlirt Scripting Reference

Scripting plugin — C# REPL с полным доступом к API отладчика.  
Язык: **C#** (Roslyn). Переменные сохраняются между запусками.  
Горячие клавиши: **F5** / **Ctrl+Enter** = выполнить, можно выделить фрагмент и выполнить только его.

---

## Шорткаты (глобальные переменные)

Доступны без префикса, напрямую:

| Шорткат | Тип | Описание |
|---------|-----|----------|
| `api` | `IDebuggerApi` | Полный API отладчика |
| `print("text")` | `void` | Вывести текст в output панель |
| `ReadMem(addr, size)` | `byte[]?` | Прочитать `size` байт по адресу |
| `WriteMem(addr, data)` | `bool` | Записать байты по адресу |
| `ReadString(addr)` | `string` | ASCII строка (null-terminated, макс 256) |
| `ReadString(addr, 1024)` | `string` | ASCII строка с указанным лимитом |
| `ReadWString(addr)` | `string` | Unicode (UTF-16) строка |
| `ReadPtr(addr)` | `ulong` | Прочитать указатель (8 байт x64 / 4 байта x86) |
| `ReadU32(addr)` | `uint` | Прочитать 4 байта как uint32 |
| `ReadU64(addr)` | `ulong` | Прочитать 8 байт как uint64 |
| `Reg("RAX")` | `ulong` | Значение регистра по имени |
| `RIP` | `ulong` | Текущий RIP (instruction pointer) |
| `RSP` | `ulong` | Текущий RSP (stack pointer) |
| `Sym(addr)` | `string?` | Имя символа по адресу (null если нет) |
| `Addr("module!func")` | `ulong` | Адрес по имени символа |

---

## Полный API (`api.*`)

### api — состояние отладчика

```csharp
api.IsConnected      // bool — подключен ли к таргету
api.IsBreakState     // bool — процесс остановлен
api.TargetPid        // uint — PID отлаживаемого процесса
api.SelectedThreadId // uint — выбранный TID
api.Is32Bit          // bool — 32-битный процесс (WoW64)
```

### api.Memory — память и регистры

```csharp
api.Memory.ReadMemory(pid, addr, size)          // byte[]? — прочитать память
api.Memory.WriteMemory(pid, addr, data)         // bool — записать память
api.Memory.ReadRegisters(pid, tid)              // List<PluginRegister> — все регистры
api.Memory.WriteRip(pid, tid, newRip)           // bool — изменить RIP
api.Memory.WriteRipAndRsp(tid, newRip, newRsp)  // bool — изменить RIP и RSP
api.Memory.ProtectMemory(pid, addr, size, prot) // (bool, uint) — изменить защиту
api.Memory.AllocateMemory(pid, size)            // ulong — выделить память
api.Memory.FreeMemory(pid, addr)                // bool — освободить память
```

### api.Breakpoints — точки останова

```csharp
api.Breakpoints.SetBreakpoint(pid, tid, addr, type)  // uint? — установить BP, вернёт handle
api.Breakpoints.RemoveBreakpoint(handle)              // bool — удалить BP
api.Breakpoints.GetAll()                              // List<PluginBreakpoint> — все BP

// Типы:
PluginBreakpointType.Software   // INT3
PluginBreakpointType.Hardware   // DR0-3 execute
PluginBreakpointType.HwWrite    // DR0-3 write watch
PluginBreakpointType.HwReadWrite // DR0-3 read/write watch
PluginBreakpointType.Memory     // PAGE_GUARD
```

### api.Symbols — символы

```csharp
api.Symbols.ResolveAddress(addr)         // string? — имя символа
api.Symbols.ResolveNameToAddress(name)   // ulong — адрес по имени ("kernel32!CreateFileW")
api.Symbols.GetModules()                 // List<PluginModuleInfo> — user-mode модули
api.Symbols.GetKernelModules()           // List<PluginKernelModuleInfo> — kernel модули
```

### api.Process — процессы и потоки

```csharp
api.Process.EnumProcesses()              // List<PluginProcessInfo>
api.Process.EnumThreads(pid)             // List<PluginThreadInfo>
api.Process.SuspendThread(tid)           // bool
api.Process.ResumeThread(tid)            // bool
api.Process.GetPebAddress(pid)           // (ulong PEB, ulong PEB32)
api.Process.ClearDebugPort(pid)          // bool — anti-debug bypass
api.Process.ClearThreadHide(pid)         // bool — anti-debug bypass
api.Process.InstallNtQsiHook()           // bool — скрыть процесс от NtQuerySystemInformation
api.Process.RemoveNtQsiHook()            // bool
```

### api.UI — интерфейс

```csharp
api.UI.NavigateDisassembly(addr)                   // перейти в дизассемблере
api.UI.SetAddressAnnotation(addr, "comment")       // поставить комментарий
api.UI.SetAddressAnnotation(addr, null)            // удалить комментарий
api.UI.GetAddressAnnotation(addr)                  // string? — получить комментарий
api.UI.GetAllAnnotations()                         // Dictionary<ulong, string>
api.UI.RefreshDisassembly()                        // обновить дизассемблер
api.UI.DecompileFunction(addr)                     // запустить декомпиляцию
api.UI.GetDecompiledCode()                         // string — результат RetDec
api.UI.DisasmGoBack()                              // вернуться назад
api.UI.AddUnpackedModule(peBase, name)             // добавить распакованный модуль
api.UI.RefreshModulesAndSections()                 // обновить списки модулей
```

### api.Log — логирование

```csharp
api.Log.Info("message")     // в Log вкладку
api.Log.Warning("message")
api.Log.Error("message")
```

### api — управление выполнением

```csharp
api.Continue()          // продолжить (F5/F9)
api.SingleStep()        // шаг в (F7)
api.StepOver()          // шаг через (F8)
api.StepOut()           // выйти из функции (Ctrl+F9)
api.RunToCursor(addr)   // выполнить до адреса (F4)
api.SkipInstruction()   // пропустить инструкцию (Ctrl+F8)
api.Pause()             // пауза (F12)
```

### api — события

```csharp
api.OnDebugEvent += (PluginDebugEvent evt) => { ... };
api.OnConnected += () => { ... };
api.OnDisconnected += () => { ... };
api.OnBreakStateEntered += () => { ... };
api.OnBreakStateExited += () => { ... };
api.OnBeforeRun += () => { ... };

// Фильтр событий — return true чтобы подавить обработку UI
api.OnDebugEventFilter += (PluginDebugEvent evt) => {
    print($"BP at 0x{evt.Address:X}");
    return false; // false = показать в UI
};
```

---

## Доступные using (автоматически импортированы)

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

## Примеры

### Базовые

```csharp
// Показать все регистры
var regs = api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId);
foreach (var r in regs.Where(r => !r.IsFlag))
    print($"{r.Name,-4} = 0x{r.Value:X016}");
```

```csharp
// Прочитать 16 байт по RIP
var bytes = ReadMem(RIP, 16);
print(BitConverter.ToString(bytes));
```

```csharp
// Показать модули
foreach (var m in api.Symbols.GetModules())
    print($"0x{m.BaseAddress:X} {m.Size,8:X} {m.Name}");
```

### Аннотации

```csharp
// Дать имя функции по офсету от базы модуля
var baseAddr = api.Symbols.GetModules()[0].BaseAddress;
api.UI.SetAddressAnnotation(baseAddr + 0x538, "ParseConfig");
api.UI.RefreshDisassembly();
```

```csharp
// Аннотировать все неизвестные call-таргеты как sub_XXXX
var main = api.Symbols.GetModules()[0];
var data = ReadMem(main.BaseAddress, main.Size);
int count = 0;
for (uint i = 0; i < data.Length - 5; i++) {
    if (data[i] != 0xE8) continue;
    int rel = BitConverter.ToInt32(data, (int)i + 1);
    ulong target = main.BaseAddress + i + 5 + (ulong)rel;
    if (target < main.BaseAddress || target >= main.BaseAddress + main.Size) continue;
    if (Sym(target) == null || Sym(target).Contains("+0x")) {
        if (api.UI.GetAddressAnnotation(target) == null) {
            api.UI.SetAddressAnnotation(target, $"sub_{target:X}");
            count++;
        }
    }
}
api.UI.RefreshDisassembly();
print($"Annotated {count} functions");
```

### Память и структуры

```csharp
// Обход связного списка (LIST_ENTRY)
var head = Addr("ntdll!PebLdr") + 0x10;
var entry = ReadPtr(head);
while (entry != head && entry != 0) {
    var baseAddr = ReadPtr(entry + 0x30);
    var namePtr = ReadPtr(entry + 0x48 + 8);
    print($"0x{baseAddr:X016}  {ReadWString(namePtr)}");
    entry = ReadPtr(entry);
}
```

```csharp
// Дамп vtable
var vtable = ReadPtr(Reg("RCX"));
for (int i = 0; i < 20; i++) {
    var func = ReadPtr(vtable + (ulong)(i * 8));
    print($"[{i,2}] 0x{func:X}  {Sym(func) ?? "???"}");
}
```

```csharp
// Snapshot памяти → сравнение
var snap = ReadMem(0x7FF612340, 0x1000);
// ... (Run, Break) ...
var now = ReadMem(0x7FF612340, 0x1000);
for (int i = 0; i < snap.Length; i++)
    if (snap[i] != now[i])
        print($"+0x{i:X3}: {snap[i]:X2} -> {now[i]:X2}");
```

### Breakpoints и трассировка

```csharp
// Логирующий бряк на функцию
var target = Addr("ws2_32!send");
api.OnDebugEventFilter += evt => {
    if (evt.Address != target) return false;
    var buf = ReadPtr(Reg("RDX"));
    var len = (int)Reg("R8");
    var data = ReadMem(buf, (uint)Math.Min(len, 128));
    print($"send({len}): {Encoding.ASCII.GetString(data)}");
    return false;
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, target, PluginBreakpointType.Software);
```

```csharp
// Условный бряк — остановить только когда RAX == 0
var addr = api.Symbols.GetModules()[0].BaseAddress + 0x1234UL;
api.OnDebugEventFilter += evt => {
    if (evt.Address != addr) return false;
    if (Reg("RAX") != 0) {
        api.Continue();
        return true; // подавить UI break
    }
    return false; // RAX == 0, показать в UI
};
api.Breakpoints.SetBreakpoint(api.TargetPid, 0, addr, PluginBreakpointType.Software);
```

### Патчинг

```csharp
// NOP инструкцию (5 байт)
WriteMem(RIP, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 });
```

```csharp
// Патч jne → jmp (всегда прыгать)
WriteMem(0x7FF612340, new byte[] { 0xEB }); // short jmp
```

```csharp
// Записать строку в память
var addr = api.Memory.AllocateMemory(api.TargetPid, 256);
WriteMem(addr, Encoding.ASCII.GetBytes("Hello\0"));
print($"String at 0x{addr:X}");
```

### Файловый I/O

```csharp
// Сдампить регион памяти в файл
var data = ReadMem(0x7FF612340, 0x10000);
File.WriteAllBytes(@"C:\Temp\dump.bin", data);
print("Dumped 64KB");
```

```csharp
// Загрузить патч из файла
var patch = File.ReadAllBytes(@"C:\Temp\patch.bin");
WriteMem(0x7FF612340, patch);
```

---

## REPL особенности

- **Переменные живут между запусками** — объяви `var x = 42;` в одном Run, используй `x` в следующем
- **Выделение + F5** — выполнить только выделенный фрагмент
- **Reset State** — сбросить все переменные
- **Console.WriteLine** перенаправлен в output
- **Async поддерживается** — `await Task.Delay(1000);`
- **Ошибки компиляции** показываются в output с номером строки
