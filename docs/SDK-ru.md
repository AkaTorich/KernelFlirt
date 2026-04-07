# KernelFlirt SDK -- Руководство по разработке плагинов

**Версия документации:** 2.0  
**Целевая платформа:** .NET 9.0, Windows x64  
**Пространство имен:** `KernelFlirt.SDK`

---

## Оглавление

- [1. Введение](#1-введение)
  - [1.1. Что такое KernelFlirt](#11-что-такое-kernelflirt)
  - [1.2. Архитектура отладчика](#12-архитектура-отладчика)
  - [1.3. Система плагинов](#13-система-плагинов)
- [2. Начало работы](#2-начало-работы)
  - [2.1. Системные требования](#21-системные-требования)
  - [2.2. Создание проекта плагина](#22-создание-проекта-плагина)
  - [2.3. Файл проекта (.csproj)](#23-файл-проекта-csproj)
  - [2.4. Интерфейс IKernelFlirtPlugin](#24-интерфейс-ikernelflirtplugin)
  - [2.5. Минимальный плагин](#25-минимальный-плагин)
  - [2.6. Сборка и развертывание](#26-сборка-и-развертывание)
  - [2.7. Отладка плагина](#27-отладка-плагина)
- [3. IDebuggerApi -- главный интерфейс](#3-idebuggerapi----главный-интерфейс)
  - [3.1. Суб-API](#31-суб-api)
  - [3.2. Свойства состояния](#32-свойства-состояния)
  - [3.3. Команды управления исполнением](#33-команды-управления-исполнением)
  - [3.4. События](#34-события)
- [4. IMemoryApi -- работа с памятью](#4-imemoryapi----работа-с-памятью)
  - [4.1. ReadMemory](#41-readmemory)
  - [4.2. WriteMemory](#42-writememory)
  - [4.3. ReadRegisters](#43-readregisters)
  - [4.4. WriteRip](#44-writerip)
  - [4.5. WriteRipAndRsp](#45-writeripandrsp)
  - [4.6. ProtectMemory](#46-protectmemory)
  - [4.7. AllocateMemory](#47-allocatememory)
  - [4.8. FreeMemory](#48-freememory)
- [5. IBreakpointApi -- точки останова](#5-ibreakpointapi----точки-останова)
  - [5.1. SetBreakpoint](#51-setbreakpoint)
  - [5.2. RemoveBreakpoint](#52-removebreakpoint)
  - [5.3. GetAll](#53-getall)
  - [5.4. ToggleBreakpoint](#54-togglebreakpoint)
  - [5.5. Типы точек останова](#55-типы-точек-останова)
- [6. ISymbolApi -- символы и модули](#6-isymbolapi----символы-и-модули)
  - [6.1. ResolveAddress](#61-resolveaddress)
  - [6.2. ResolveNameToAddress](#62-resolvenametoaddress)
  - [6.3. GetModules](#63-getmodules)
  - [6.4. GetKernelModules](#64-getkernelmodules)
  - [6.5. RegisterFunction](#65-registerfunction)
  - [6.6. GetRegisteredFunctions](#66-getregisteredfunctions)
- [7. IProcessApi -- процессы, потоки и антиотладка](#7-iprocessapi----процессы-потоки-и-антиотладка)
  - [7.1. Перечисление процессов и потоков](#71-перечисление-процессов-и-потоков)
  - [7.2. Управление потоками](#72-управление-потоками)
  - [7.3. GetPebAddress](#73-getpebaddress)
  - [7.4. API антиотладки](#74-api-антиотладки)
- [8. ILogApi -- журналирование](#8-ilogapi----журналирование)
- [9. IUiApi -- пользовательский интерфейс](#9-iuiapi----пользовательский-интерфейс)
  - [9.1. NavigateDisassembly](#91-navigatedisassembly)
  - [9.2. DisasmGoBack](#92-disasmgoback)
  - [9.3. AddMenuItem](#93-addmenuitem)
  - [9.4. AddToolPanel](#94-addtoolpanel)
  - [9.5. Аннотации адресов](#95-аннотации-адресов)
  - [9.6. Декомпиляция](#96-декомпиляция)
  - [9.7. Модули и секции](#97-модули-и-секции)
  - [9.8. Межплагинное взаимодействие](#98-межплагинное-взаимодействие)
  - [9.9. События заметок](#99-события-заметок)
- [10. Модели данных](#10-модели-данных)
  - [10.1. PluginRegister](#101-pluginregister)
  - [10.2. PluginBreakpoint](#102-pluginbreakpoint)
  - [10.3. PluginModuleInfo](#103-pluginmoduleinfo)
  - [10.4. PluginKernelModuleInfo](#104-pluginkernelmoduleinfo)
  - [10.5. PluginProcessInfo](#105-pluginprocessinfo)
  - [10.6. PluginThreadInfo](#106-pluginthreadinfo)
  - [10.7. PluginSectionInfo](#107-pluginsectioninfo)
  - [10.8. PluginFunctionEntry](#108-pluginfunctionentry)
  - [10.9. PluginDebugEvent](#109-plugindebugevent)
  - [10.10. PluginBreakpointType (перечисление)](#1010-pluginbreakpointtype-перечисление)
  - [10.11. PluginDebugEventType (перечисление)](#1011-plugindebugeventtype-перечисление)
  - [10.12. PluginScriptHost](#1012-pluginscripthost)
- [11. События отладки](#11-события-отладки)
  - [11.1. OnDebugEvent](#111-ondebugevent)
  - [11.2. OnDebugEventFilter](#112-ondebugeventfilter)
  - [11.3. OnBeforeRun](#113-onbeforerun)
  - [11.4. OnBreakStateEntered / OnBreakStateExited](#114-onbreakstateentered--onbreakstateexited)
  - [11.5. ContinueMode -- управление возобновлением](#115-continuemode----управление-возобновлением)
  - [11.6. Трассировка guard page](#116-трассировка-guard-page)
  - [11.7. Быстрая трассировка на стороне драйвера](#117-быстрая-трассировка-на-стороне-драйвера)
- [12. Разработка пользовательского интерфейса](#12-разработка-пользовательского-интерфейса)
  - [12.1. WPF-контролы в плагинах](#121-wpf-контролы-в-плагинах)
  - [12.2. AddToolPanel -- добавление вкладок](#122-addtoolpanel----добавление-вкладок)
  - [12.3. AddMenuItem -- добавление пунктов меню](#123-addmenuitem----добавление-пунктов-меню)
  - [12.4. DataGrid -- таблицы данных](#124-datagrid----таблицы-данных)
  - [12.5. Контекстные меню](#125-контекстные-меню)
  - [12.6. Темизация и кисти](#126-темизация-и-кисти)
  - [12.7. Диалоговые окна](#127-диалоговые-окна)
- [13. Потоки и асинхронность](#13-потоки-и-асинхронность)
  - [13.1. UI-поток и Dispatcher](#131-ui-поток-и-dispatcher)
  - [13.2. Фоновые задачи](#132-фоновые-задачи)
  - [13.3. Паттерн async/await](#133-паттерн-asyncawait)
  - [13.4. Потокобезопасность API](#134-потокобезопасность-api)
- [14. Межплагинное взаимодействие](#14-межплагинное-взаимодействие)
  - [14.1. SetPluginData / GetPluginData](#141-setplugindata--getplugindata)
  - [14.2. Примеры обмена данными](#142-примеры-обмена-данными)
- [15. Персистентность плагинов](#15-персистентность-плагинов)
  - [15.1. Сохранение состояния в JSON](#151-сохранение-состояния-в-json)
  - [15.2. Автоматическое переключение по таргету](#152-автоматическое-переключение-по-таргету)
  - [15.3. Ребазирование адресов](#153-ребазирование-адресов)
  - [15.4. Полная сессия (SessionPlugin)](#154-полная-сессия-sessionplugin)
- [16. Полные примеры плагинов](#16-полные-примеры-плагинов)
  - [16.1. Простой плагин: только меню](#161-простой-плагин-только-меню)
  - [16.2. Средний плагин: панель с DataGrid](#162-средний-плагин-панель-с-datagrid)
  - [16.3. Сложный плагин: фоновое сканирование памяти](#163-сложный-плагин-фоновое-сканирование-памяти)
  - [16.4. Плагин-распаковщик: OnDebugEventFilter](#164-плагин-распаковщик-ondebugeventfilter)
- [17. Best practices и частые ошибки](#17-best-practices-и-частые-ошибки)
  - [17.1. Обязательные правила](#171-обязательные-правила)
  - [17.2. Частые ошибки](#172-частые-ошибки)
  - [17.3. Рекомендации по производительности](#173-рекомендации-по-производительности)
  - [17.4. Чек-лист перед релизом](#174-чек-лист-перед-релизом)
- [18. Справочник перечислений и констант](#18-справочник-перечислений-и-констант)
  - [18.1. Константы защиты памяти](#181-константы-защиты-памяти)
  - [18.2. Характеристики секций PE](#182-характеристики-секций-pe)
  - [18.3. Коды исключений Windows](#183-коды-исключений-windows)
  - [18.4. Имена регистров](#184-имена-регистров)

---

## 1. Введение

### 1.1. Что такое KernelFlirt

KernelFlirt -- ядерный отладчик для Windows x64, работающий через собственный драйвер ядра. В отличие от стандартных отладчиков (WinDbg, x64dbg), KernelFlirt перехватывает обработчик отладочных исключений (`KdpStub`) непосредственно в `ntoskrnl.exe`, что позволяет отлаживать процессы без использования стандартного подключения ядерного отладчика и без обнаружения большинством антиотладочных техник.

Ключевые возможности:
- Отладка процессов на уровне ядра (Ring 0 -> Ring 3)
- Программные и аппаратные точки останова
- Чтение/запись памяти через IOCTL драйвера
- Встроенный декомпилятор (RetDec)
- Расширяемость через систему плагинов
- Удаленная отладка через TCP (KfRelay)

### 1.2. Архитектура отладчика

```
+------------------+      TCP/IOCTL      +------------------+
|   KernelFlirt    | <=================> |    KfRelay.exe   |
|   (UI, .NET 9)   |                     |  (целевая VM)    |
+------------------+                     +--------+---------+
        |                                         |
   Плагины (.dll)                           IOCTL драйвера
        |                                         |
+------------------+                     +--------+---------+
| KernelFlirt.SDK  |                     |  KfDriver.sys    |
|  (интерфейсы)    |                     | (hook KdpStub)   |
+------------------+                     +------------------+
```

Драйвер `KfDriver.sys` перехватывает функцию `KdpStub` в ядре Windows. Когда процесс попадает на точку останова или исключение, драйвер перехватывает управление, собирает контекст (регистры, адрес) и передает его через IOCTL в UI-приложение KernelFlirt. Для удаленной отладки (виртуальная машина VMware) используется промежуточный ретранслятор `KfRelay.exe`, работающий на целевой машине и пробрасывающий IOCTL через TCP.

Отладочные события приходят в фоновом потоке. Событие `OnDebugEventFilter` вызывается в этом же фоновом потоке, **до** обработки UI. Событие `OnDebugEvent` вызывается в UI-потоке **после** обработки.

### 1.3. Система плагинов

Плагины KernelFlirt -- это обычные сборки .NET 9 (`net9.0-windows`), реализующие интерфейс `IKernelFlirtPlugin`. При запуске KernelFlirt сканирует папку `plugins/` рядом с `KernelFlirt.exe`, загружает все найденные DLL и ищет классы, реализующие `IKernelFlirtPlugin`.

Жизненный цикл плагина:
1. KernelFlirt находит DLL в `plugins/`
2. Загружает сборку через `AssemblyLoadContext`
3. Находит класс с `IKernelFlirtPlugin` через рефлексию
4. Создает экземпляр (конструктор без параметров)
5. Вызывает `Initialize(IDebuggerApi api)`
6. Плагин работает, обрабатывает события
7. При завершении KernelFlirt вызывается `Shutdown()`

---

## 2. Начало работы

### 2.1. Системные требования

- **ОС разработки:** Windows 10/11 x64
- **.NET SDK:** 9.0 или выше
- **IDE:** Visual Studio 2022, JetBrains Rider или VS Code с расширением C#
- **WPF:** для плагинов с пользовательским интерфейсом
- **Целевая платформа отладки:** только x64 (32-битная отладка и WoW64 не поддерживаются)

### 2.2. Создание проекта плагина

Создайте новый проект типа "Class Library" для .NET 9.0 с поддержкой Windows:

```bash
dotnet new classlib -n MyPlugin -f net9.0-windows
```

### 2.3. Файл проекта (.csproj)

Минимальный `.csproj` для плагина KernelFlirt:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RootNamespace>MyPlugin</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\sdk\KernelFlirt.SDK.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

</Project>
```

**Критически важные параметры:**

| Параметр | Значение | Описание |
|----------|----------|----------|
| `TargetFramework` | `net9.0-windows` | Обязательно `windows`, иначе WPF-контролы недоступны |
| `EnableDynamicLoading` | `true` | Необходимо для корректной загрузки через `AssemblyLoadContext` |
| `UseWPF` | `true` | Включает поддержку WPF-контролов (кнопки, панели, DataGrid) |
| `Private` | `false` | **Не копировать** SDK DLL в выходную папку -- она уже загружена хостом |
| `ExcludeAssets` | `runtime` | Исключить runtime-зависимости SDK из выходной папки |

**Если вы НЕ используете WPF** (плагин без UI), можно убрать `<UseWPF>true</UseWPF>`, но обычно лучше оставить -- это не увеличивает размер DLL.

### 2.4. Интерфейс IKernelFlirtPlugin

```csharp
namespace KernelFlirt.SDK;

public interface IKernelFlirtPlugin
{
    string Name { get; }
    string Description { get; }
    string Version { get; }

    void Initialize(IDebuggerApi api);
    void Shutdown();
}
```

| Член | Тип | Описание |
|------|-----|----------|
| `Name` | `string` | Отображаемое имя плагина в списке плагинов KernelFlirt |
| `Description` | `string` | Краткое описание функциональности плагина |
| `Version` | `string` | Строка версии (например, `"1.0"`, `"2.1.3"`) |
| `Initialize(IDebuggerApi api)` | `void` | Вызывается один раз при загрузке. Сохраните ссылку на `api`. Регистрируйте обработчики событий, пункты меню и панели здесь |
| `Shutdown()` | `void` | Вызывается при завершении приложения. Освободите ресурсы, сохраните состояние |

### 2.5. Минимальный плагин

```csharp
using KernelFlirt.SDK;

namespace MyPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "My Plugin";
    public string Description => "Мой первый плагин для KernelFlirt";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        api.Log.Info("Мой плагин загружен!");
    }

    public void Shutdown()
    {
        // Освобождение ресурсов, если необходимо
    }
}
```

### 2.6. Сборка и развертывание

1. Соберите проект:
   ```bash
   dotnet build -c Release
   ```

2. Скопируйте результат сборки в папку `plugins/` рядом с `KernelFlirt.exe`:
   ```
   bin\UI\KernelFlirt.exe
   bin\UI\plugins\
       MyPlugin.dll        <-- ваш плагин
       MyPlugin.deps.json  <-- зависимости (если есть)
   ```

3. Запустите `KernelFlirt.exe` -- плагин загрузится автоматически.

**Структура папки plugins:**
```
plugins/
  BookmarksPlugin.dll
  XrefsPlugin.dll
  GraphViewPlugin.dll
  SessionPlugin.dll
  ScriptingPlugin.dll
  MyPlugin.dll          <-- ваш плагин
```

### 2.7. Отладка плагина

Для отладки плагина в Visual Studio:

1. Откройте свойства проекта плагина
2. В разделе "Debug" установите:
   - Launch: Executable
   - Executable: путь к `KernelFlirt.exe`
   - Working directory: папка с `KernelFlirt.exe`
3. Поставьте точку останова в `Initialize()` и нажмите F5

Альтернативно, используйте `_api.Log.Info()` для вывода отладочных сообщений в лог-панель KernelFlirt.

---

## 3. IDebuggerApi -- главный интерфейс

`IDebuggerApi` -- основной интерфейс, предоставляемый плагинам при инициализации. Через него доступны все суб-API, свойства состояния отладчика, команды управления исполнением и события.

```csharp
namespace KernelFlirt.SDK;

public interface IDebuggerApi
{
    // Суб-API
    IMemoryApi Memory { get; }
    IBreakpointApi Breakpoints { get; }
    ISymbolApi Symbols { get; }
    IProcessApi Process { get; }
    ILogApi Log { get; }
    IUiApi UI { get; }

    // Состояние
    bool IsConnected { get; }
    bool IsBreakState { get; }
    uint TargetPid { get; }
    uint SelectedThreadId { get; }
    bool Is32Bit { get; }

    // События
    event Action<PluginDebugEvent>? OnDebugEvent;
    event Action? OnConnected;
    event Action? OnDisconnected;
    event Action? OnBreakStateEntered;
    event Action? OnBreakStateExited;
    event Action? OnBeforeRun;
    event Func<PluginDebugEvent, bool>? OnDebugEventFilter;

    // Команды
    void Continue();
    void SingleStep();
    void StepOver();
    void StepOut();
    void RunToCursor(ulong address);
    void SkipInstruction();
    void Pause();
}
```

### 3.1. Суб-API

| Свойство | Тип | Описание |
|----------|-----|----------|
| `Memory` | `IMemoryApi` | Чтение/запись памяти процесса и регистров |
| `Breakpoints` | `IBreakpointApi` | Управление точками останова (программные, аппаратные, memory) |
| `Symbols` | `ISymbolApi` | Разрешение адресов в символьные имена и обратно, работа с модулями |
| `Process` | `IProcessApi` | Перечисление процессов/потоков, API антиотладки |
| `Log` | `ILogApi` | Вывод сообщений в лог-панель |
| `UI` | `IUiApi` | Элементы интерфейса: меню, вкладки, навигация, аннотации |

### 3.2. Свойства состояния

| Свойство | Тип | Описание |
|----------|-----|----------|
| `IsConnected` | `bool` | `true`, когда отладчик подключен к целевой машине (VM или локальный драйвер) |
| `IsBreakState` | `bool` | `true`, когда целевой процесс остановлен (сработала точка останова, single-step и т.д.). **Чтение памяти и регистров возможно ТОЛЬКО в break state** |
| `TargetPid` | `uint` | PID отлаживаемого процесса. `0`, если процесс не выбран |
| `SelectedThreadId` | `uint` | ID текущего выбранного потока |
| `Is32Bit` | `bool` | `true`, если целевой процесс 32-битный (WoW64). **В текущей версии не используется -- поддерживается только x64** |

**Важно:** Всегда проверяйте `IsConnected` и `IsBreakState` перед обращением к памяти, регистрам и точкам останова. Вызовы API в неподходящем состоянии вернут ошибку или `null`.

```csharp
if (!_api.IsConnected || !_api.IsBreakState)
{
    _api.Log.Warning("Отладчик не подключен или процесс не остановлен");
    return;
}
// Безопасно работать с памятью, регистрами и т.д.
```

### 3.3. Команды управления исполнением

| Метод | Горячая клавиша | Описание |
|-------|-----------------|----------|
| `Continue()` | F9 | Возобновить выполнение процесса. Эквивалент кнопки Run |
| `SingleStep()` | F7 | Выполнить одну инструкцию с входом в вызовы (Step Into). Для `CALL` -- входит внутрь функции |
| `StepOver()` | F8 | Выполнить одну инструкцию без входа в вызовы (Step Over). Для `CALL` -- устанавливает временную точку останова на следующей инструкции и запускает выполнение |
| `StepOut()` | Ctrl+F9 | Выход из текущей функции (Step Out). Читает адрес возврата из `[RSP]` и выполняет до него |
| `RunToCursor(ulong address)` | F4 | Выполнение до указанного адреса. Устанавливает временную точку останова по адресу `address` и возобновляет выполнение |
| `SkipInstruction()` | Ctrl+F8 | Пропустить текущую инструкцию -- перемещает RIP за нее **без выполнения** |
| `Pause()` | F12 | Приостановить выполняющийся процесс. Останавливает все потоки |

**Пример -- выполнение до адреса:**
```csharp
ulong targetAddr = _api.Symbols.ResolveNameToAddress("kernel32!CreateFileW");
if (targetAddr != 0)
{
    _api.RunToCursor(targetAddr);
    _api.Log.Info($"Запущен до {targetAddr:X16}");
}
```

**Пример -- пропуск инструкции (обход проверки):**
```csharp
// Пропустить текущую инструкцию (например, JZ на антиотладочную проверку)
_api.SkipInstruction();
_api.Log.Info("Инструкция пропущена");
```

### 3.4. События

Все события определены в интерфейсе `IDebuggerApi`. Подписывайтесь на них в `Initialize()`:

```csharp
public void Initialize(IDebuggerApi api)
{
    _api = api;

    api.OnConnected += () =>
        api.Log.Info("Подключено к целевой машине");

    api.OnDisconnected += () =>
        api.Log.Info("Отключено от целевой машины");

    api.OnBreakStateEntered += () =>
        api.Log.Info($"Процесс остановлен, PID={api.TargetPid}");

    api.OnBreakStateExited += () =>
        api.Log.Info("Процесс возобновлен");

    api.OnBeforeRun += () =>
        api.Log.Info("Вот-вот запустим процесс...");

    api.OnDebugEvent += OnDebugEvent;
    api.OnDebugEventFilter += OnDebugEventFilter;
}
```

| Событие | Сигнатура | Поток вызова | Описание |
|---------|-----------|--------------|----------|
| `OnConnected` | `Action` | UI | Отладчик подключился к целевой машине |
| `OnDisconnected` | `Action` | UI | Отладчик отключился |
| `OnBreakStateEntered` | `Action` | UI | Процесс остановлен (точка останова, single-step, исключение) |
| `OnBreakStateExited` | `Action` | UI | Процесс возобновлен |
| `OnBeforeRun` | `Action` | UI | Вызывается непосредственно перед запуском (Run/F9). Идеальное место для установки точек останова |
| `OnDebugEvent` | `Action<PluginDebugEvent>` | UI | Информационное событие, после обработки UI |
| `OnDebugEventFilter` | `Func<PluginDebugEvent, bool>` | **Фоновый** | **Критическое событие.** Вызывается ДО обработки UI. Возвращает `true` для подавления UI-обработки. Подробнее в [разделе 11.2](#112-ondebugeventfilter) |

---

## 4. IMemoryApi -- работа с памятью

Интерфейс `IMemoryApi` предоставляет полный доступ к памяти целевого процесса и его регистрам. Все операции работают через драйвер ядра, минуя стандартные API Windows.

```csharp
public interface IMemoryApi
{
    byte[]? ReadMemory(uint pid, ulong address, uint size);
    bool WriteMemory(uint pid, ulong address, byte[] data);
    IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid);
    bool WriteRip(uint pid, uint tid, ulong newRip);
    bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp);
    (bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection);
    ulong AllocateMemory(uint pid, ulong size);
    bool FreeMemory(uint pid, ulong address);
}
```

### 4.1. ReadMemory

```csharp
byte[]? ReadMemory(uint pid, ulong address, uint size)
```

Читает блок памяти из адресного пространства процесса.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса. Используйте `_api.TargetPid` для текущего отлаживаемого процесса |
| `address` | `ulong` | Виртуальный адрес начала чтения |
| `size` | `uint` | Количество байт для чтения |

**Возвращает:** `byte[]` с прочитанными данными, или `null` при ошибке (невалидный адрес, процесс не остановлен, недостаточно прав).

**Примеры:**

```csharp
// Чтение 8 байт (QWORD) по адресу
byte[]? data = _api.Memory.ReadMemory(_api.TargetPid, address, 8);
if (data != null)
{
    ulong value = BitConverter.ToUInt64(data);
    _api.Log.Info($"Значение по {address:X16}: 0x{value:X16}");
}

// Чтение 4 байт (DWORD)
byte[]? dword = _api.Memory.ReadMemory(_api.TargetPid, address, 4);
if (dword != null)
{
    uint value32 = BitConverter.ToUInt32(dword);
}

// Чтение строки (ANSI, null-terminated)
byte[]? strData = _api.Memory.ReadMemory(_api.TargetPid, strAddress, 256);
if (strData != null)
{
    int nullIdx = Array.IndexOf(strData, (byte)0);
    string text = System.Text.Encoding.ASCII.GetString(strData, 0,
        nullIdx >= 0 ? nullIdx : strData.Length);
}

// Чтение Unicode-строки (UTF-16)
byte[]? wstrData = _api.Memory.ReadMemory(_api.TargetPid, wstrAddress, 512);
if (wstrData != null)
{
    int nullIdx = -1;
    for (int i = 0; i < wstrData.Length - 1; i += 2)
    {
        if (wstrData[i] == 0 && wstrData[i + 1] == 0) { nullIdx = i; break; }
    }
    string wtext = System.Text.Encoding.Unicode.GetString(wstrData, 0,
        nullIdx >= 0 ? nullIdx : wstrData.Length);
}

// Чтение указателя (8 байт в x64)
byte[]? ptrData = _api.Memory.ReadMemory(_api.TargetPid, ptrAddress, 8);
ulong pointedTo = ptrData != null ? BitConverter.ToUInt64(ptrData) : 0;
```

### 4.2. WriteMemory

```csharp
bool WriteMemory(uint pid, ulong address, byte[] data)
```

Записывает данные в память процесса.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `address` | `ulong` | Виртуальный адрес начала записи |
| `data` | `byte[]` | Данные для записи |

**Возвращает:** `true` при успехе, `false` при ошибке.

**Примеры:**

```csharp
// Записать NOP (0x90) вместо инструкции
bool ok = _api.Memory.WriteMemory(_api.TargetPid, address, new byte[] { 0x90 });

// Записать два NOP-а вместо короткого прыжка (JZ rel8 = 2 байта)
_api.Memory.WriteMemory(_api.TargetPid, jzAddress, new byte[] { 0x90, 0x90 });

// Записать QWORD
ulong newValue = 0xDEADBEEF;
_api.Memory.WriteMemory(_api.TargetPid, address, BitConverter.GetBytes(newValue));

// Записать JMP REL32 (5 байт: E9 xx xx xx xx)
int offset = (int)((long)targetAddr - (long)(patchAddr + 5));
byte[] jmp = new byte[5];
jmp[0] = 0xE9;
BitConverter.GetBytes(offset).CopyTo(jmp, 1);
_api.Memory.WriteMemory(_api.TargetPid, patchAddr, jmp);
```

### 4.3. ReadRegisters

```csharp
IReadOnlyList<PluginRegister> ReadRegisters(uint pid, uint tid)
```

Читает все регистры указанного потока.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `tid` | `uint` | Идентификатор потока |

**Возвращает:** Список `PluginRegister` со всеми регистрами. Каждый регистр имеет поля `Name` (строка), `Value` (ulong) и `IsFlag` (bool).

**Доступные регистры:** `RAX`, `RBX`, `RCX`, `RDX`, `RSI`, `RDI`, `RBP`, `RSP`, `R8`-`R15`, `RIP`, `RFLAGS`, `DR0`-`DR7`, `CS`, `DS`, `ES`, `FS`, `GS`, `SS`.

**Флаги** (поле `IsFlag = true`): `CF`, `ZF`, `SF`, `OF`, `PF`, `AF`, `DF`, `TF`, `IF`.

**Примеры:**

```csharp
var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);

// Получить RIP
ulong rip = regs.First(r => r.Name == "RIP").Value;
_api.Log.Info($"RIP = 0x{rip:X16}");

// Получить RAX (часто содержит возвращаемое значение)
ulong rax = regs.First(r => r.Name == "RAX").Value;

// Получить RSP (вершина стека)
ulong rsp = regs.First(r => r.Name == "RSP").Value;

// Получить RCX (первый параметр в x64 calling convention)
ulong rcx = regs.First(r => r.Name == "RCX").Value;

// Вывести все регистры общего назначения
foreach (var reg in regs.Where(r => !r.IsFlag))
    _api.Log.Info($"  {reg.Name} = 0x{reg.Value:X16}");

// Проверить конкретный флаг
bool zeroFlag = regs.First(r => r.Name == "ZF").Value != 0;
_api.Log.Info($"ZF = {zeroFlag}");
```

### 4.4. WriteRip

```csharp
bool WriteRip(uint pid, uint tid, ulong newRip)
```

Устанавливает новое значение регистра RIP для указанного потока. Поток должен быть остановлен (break state).

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `tid` | `uint` | Идентификатор потока |
| `newRip` | `ulong` | Новое значение RIP |

**Возвращает:** `true` при успехе.

**Пример -- перенаправление выполнения:**
```csharp
// Перенаправить выполнение на другой адрес
ulong newAddr = _api.Symbols.ResolveNameToAddress("mymodule!BypassFunction");
if (newAddr != 0)
{
    _api.Memory.WriteRip(_api.TargetPid, _api.SelectedThreadId, newAddr);
    _api.Log.Info($"RIP перенаправлен на 0x{newAddr:X16}");
}
```

### 4.5. WriteRipAndRsp

```csharp
bool WriteRipAndRsp(uint tid, ulong newRip, ulong newRsp)
```

Атомарно устанавливает RIP и RSP. Используется при перенаправлении выполнения, когда нужно также восстановить стек.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока |
| `newRip` | `ulong` | Новое значение RIP |
| `newRsp` | `ulong` | Новое значение RSP |

**Возвращает:** `true` при успехе.

**Обратите внимание:** в отличие от `WriteRip`, этот метод принимает только `tid` (без `pid`).

**Пример -- трассировка IAT:**
```csharp
// При перехвате IAT вызова: перенаправить на настоящую функцию,
// восстановив стек (убрав лишний CALL frame)
evt.NewRip = realFunctionAddress;
evt.NewRsp = originalRsp;
```

### 4.6. ProtectMemory

```csharp
(bool ok, uint oldProtection) ProtectMemory(uint pid, ulong address, uint size, uint newProtection)
```

Изменяет атрибуты защиты страниц памяти (аналог `VirtualProtectEx`).

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `address` | `ulong` | Начальный адрес региона |
| `size` | `uint` | Размер региона в байтах |
| `newProtection` | `uint` | Новые атрибуты защиты (константы Windows) |

**Возвращает:** Кортеж `(bool ok, uint oldProtection)` -- успешность операции и предыдущие атрибуты защиты.

**Константы защиты:**

| Константа | Значение | Описание |
|-----------|----------|----------|
| `PAGE_NOACCESS` | `0x01` | Нет доступа |
| `PAGE_READONLY` | `0x02` | Только чтение |
| `PAGE_READWRITE` | `0x04` | Чтение и запись |
| `PAGE_EXECUTE` | `0x10` | Только выполнение |
| `PAGE_EXECUTE_READ` | `0x20` | Выполнение и чтение |
| `PAGE_EXECUTE_READWRITE` | `0x40` | Выполнение, чтение и запись |
| `PAGE_GUARD` | `0x100` | Guard page (используется для memory breakpoints) |

**Пример -- сделать секцию кода записываемой:**
```csharp
var (ok, oldProt) = _api.Memory.ProtectMemory(_api.TargetPid, codeAddress, 0x1000, 0x40);
if (ok)
{
    // Записать патч
    _api.Memory.WriteMemory(_api.TargetPid, codeAddress, patchBytes);
    // Восстановить защиту
    _api.Memory.ProtectMemory(_api.TargetPid, codeAddress, 0x1000, oldProt);
}
```

### 4.7. AllocateMemory

```csharp
ulong AllocateMemory(uint pid, ulong size)
```

Выделяет блок памяти в адресном пространстве целевого процесса (аналог `VirtualAllocEx` с `MEM_COMMIT | MEM_RESERVE` и `PAGE_EXECUTE_READWRITE`).

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `size` | `ulong` | Размер выделяемого блока в байтах |

**Возвращает:** Базовый адрес выделенного блока, или `0` при ошибке.

**Пример -- выделить память и записать шеллкод:**
```csharp
ulong buf = _api.Memory.AllocateMemory(_api.TargetPid, 0x1000);
if (buf != 0)
{
    byte[] shellcode = { 0xCC, 0xC3 }; // INT3; RET
    _api.Memory.WriteMemory(_api.TargetPid, buf, shellcode);
    _api.Log.Info($"Шеллкод записан по адресу 0x{buf:X16}");
}
```

### 4.8. FreeMemory

```csharp
bool FreeMemory(uint pid, ulong address)
```

Освобождает ранее выделенный блок памяти.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `address` | `ulong` | Базовый адрес блока (полученный из `AllocateMemory`) |

**Возвращает:** `true` при успехе.

---

## 5. IBreakpointApi -- точки останова

Интерфейс для управления точками останова всех типов.

```csharp
public interface IBreakpointApi
{
    uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1);
    bool RemoveBreakpoint(uint handle);
    IReadOnlyList<PluginBreakpoint> GetAll();
    void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software);
}
```

### 5.1. SetBreakpoint

```csharp
uint? SetBreakpoint(uint pid, uint tid, ulong address, PluginBreakpointType type, uint length = 1)
```

Устанавливает точку останова.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |
| `tid` | `uint` | Идентификатор потока (для аппаратных BP; для программных можно указать `0`) |
| `address` | `ulong` | Адрес точки останова |
| `type` | `PluginBreakpointType` | Тип точки останова (см. [раздел 5.5](#55-типы-точек-останова)) |
| `length` | `uint` | Размер наблюдаемой области для аппаратных watchpoint (1, 2, 4 или 8 байт). По умолчанию `1`. Для типов `Software` и `Hardware` -- всегда `1` |

**Возвращает:** `uint` -- уникальный дескриптор (handle) точки останова при успехе, или `null` при ошибке.

**Примеры:**

```csharp
// Программная точка останова (INT3)
uint? bp = _api.Breakpoints.SetBreakpoint(
    _api.TargetPid, 0, 0x140001000, PluginBreakpointType.Software);
if (bp.HasValue)
    _api.Log.Info($"BP установлена, handle={bp.Value}");

// Аппаратная точка останова на выполнение (DR0-DR3)
uint? hwBp = _api.Breakpoints.SetBreakpoint(
    _api.TargetPid, _api.SelectedThreadId, entryPoint, PluginBreakpointType.Hardware);

// Аппаратный watchpoint на запись (4 байта)
uint? wBp = _api.Breakpoints.SetBreakpoint(
    _api.TargetPid, _api.SelectedThreadId, globalVarAddr,
    PluginBreakpointType.HwWrite, length: 4);

// Аппаратный watchpoint на чтение/запись (8 байт)
uint? rwBp = _api.Breakpoints.SetBreakpoint(
    _api.TargetPid, _api.SelectedThreadId, iatEntry,
    PluginBreakpointType.HwReadWrite, length: 8);

// Memory breakpoint (на основе защиты страниц)
uint? memBp = _api.Breakpoints.SetBreakpoint(
    _api.TargetPid, 0, pageAddress, PluginBreakpointType.Memory);
```

**Ограничения:**
- Максимум 4 аппаратных точки останова одновременно (DR0-DR3)
- `length` для аппаратных watchpoint может быть только 1, 2, 4 или 8

### 5.2. RemoveBreakpoint

```csharp
bool RemoveBreakpoint(uint handle)
```

Удаляет точку останова по дескриптору.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `handle` | `uint` | Дескриптор, полученный из `SetBreakpoint` |

**Возвращает:** `true` при успехе.

```csharp
if (bp.HasValue)
    _api.Breakpoints.RemoveBreakpoint(bp.Value);
```

### 5.3. GetAll

```csharp
IReadOnlyList<PluginBreakpoint> GetAll()
```

Возвращает список всех установленных точек останова.

**Возвращает:** `IReadOnlyList<PluginBreakpoint>` -- список активных точек останова.

```csharp
var allBps = _api.Breakpoints.GetAll();
foreach (var bp in allBps)
{
    _api.Log.Info($"BP handle={bp.Handle} addr=0x{bp.Address:X16} " +
                  $"type={bp.Type} enabled={bp.Enabled} hits={bp.HitCount}");
}
```

### 5.4. ToggleBreakpoint

```csharp
void ToggleBreakpoint(ulong address, PluginBreakpointType type = PluginBreakpointType.Software)
```

Переключает точку останова через UI. Если по данному адресу уже есть точка останова -- удаляет ее. Если нет -- добавляет. Обновляет список точек останова в UI, маркеры в дизассемблере и состояние в драйвере.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес точки останова |
| `type` | `PluginBreakpointType` | Тип точки останова. По умолчанию `Software` |

**Отличие от SetBreakpoint:** `ToggleBreakpoint` -- это высокоуровневая операция, которая полностью синхронизирует UI (обновляет список BP, красную метку в дизассемблере, отправляет команду в драйвер). `SetBreakpoint` -- низкоуровневая операция, которая только создает BP в драйвере.

**Пример -- восстановление точек останова из файла сессии:**
```csharp
foreach (var savedBp in sessionData.Breakpoints)
{
    // Проверяем, нет ли уже BP по этому адресу
    if (!_api.Breakpoints.GetAll().Any(b => b.Address == savedBp.Address))
    {
        _api.Breakpoints.ToggleBreakpoint(savedBp.Address, (PluginBreakpointType)savedBp.Type);
    }
}
```

### 5.5. Типы точек останова

Перечисление `PluginBreakpointType`:

| Значение | Имя | Описание |
|----------|-----|----------|
| `0` | `Software` | Программная точка останова (INT3, `0xCC`). Заменяет первый байт инструкции на `0xCC`. При срабатывании оригинальный байт восстанавливается |
| `1` | `Hardware` | Аппаратная точка останова на выполнение (DR0-DR3). Работает без модификации кода. Выживает при перезаписи кода. Максимум 4 одновременно |
| `2` | `HwWrite` | Аппаратный watchpoint на запись. Срабатывает при записи по указанному адресу. Поддерживает `length` 1/2/4/8 байт |
| `3` | `HwReadWrite` | Аппаратный watchpoint на чтение/запись. Срабатывает при любом доступе к данным. Поддерживает `length` 1/2/4/8 байт |
| `4` | `Memory` | Memory breakpoint на основе защиты страниц (guard pages). Покрывает целую страницу (4 КБ). Медленнее аппаратных, но без ограничения на количество |

---

## 6. ISymbolApi -- символы и модули

Интерфейс для разрешения символов, работы с модулями и регистрации пользовательских функций.

```csharp
public interface ISymbolApi
{
    string? ResolveAddress(ulong address);
    ulong ResolveNameToAddress(string name);
    IReadOnlyList<PluginModuleInfo> GetModules();
    IReadOnlyList<PluginKernelModuleInfo> GetKernelModules();
    void RegisterFunction(ulong address, string? name, uint size = 0);
    IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions();
}
```

### 6.1. ResolveAddress

```csharp
string? ResolveAddress(ulong address)
```

Разрешает виртуальный адрес в символьное имя.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Виртуальный адрес |

**Возвращает:** Строку формата `"модуль!функция+смещение"` (например, `"kernel32!CreateFileW"`, `"ntdll!NtQueryInformationProcess+0x14"`), или `null`, если символ не найден.

Для адресов, зарегистрированных через `RegisterFunction`, возвращает имя пользовательской функции.

```csharp
string? name = _api.Symbols.ResolveAddress(rip);
if (name != null)
    _api.Log.Info($"RIP указывает на: {name}");
else
    _api.Log.Info($"Символ не найден для 0x{rip:X16}");
```

### 6.2. ResolveNameToAddress

```csharp
ulong ResolveNameToAddress(string name)
```

Разрешает символьное имя в виртуальный адрес.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `name` | `string` | Символьное имя в формате `"модуль!функция"` |

**Возвращает:** Виртуальный адрес или `0`, если символ не найден.

**Формат имени:**
- `"kernel32!CreateFileW"` -- экспортируемая функция
- `"ntdll.dll!NtQueryInformationProcess"` -- расширение `.dll` опционально
- `"ntoskrnl!KeBugCheckEx"` -- ядерная функция (если загружены символы ядра)

```csharp
ulong addr = _api.Symbols.ResolveNameToAddress("kernel32!IsDebuggerPresent");
if (addr != 0)
{
    _api.Breakpoints.ToggleBreakpoint(addr);
    _api.Log.Info($"BP на IsDebuggerPresent по адресу 0x{addr:X16}");
}
```

### 6.3. GetModules

```csharp
IReadOnlyList<PluginModuleInfo> GetModules()
```

Возвращает список всех загруженных модулей (DLL/EXE) целевого процесса пользовательского режима.

**Возвращает:** `IReadOnlyList<PluginModuleInfo>` -- список модулей с полями `BaseAddress`, `Size`, `Name`.

```csharp
var modules = _api.Symbols.GetModules();
foreach (var mod in modules)
    _api.Log.Info($"  {mod.Name} @ 0x{mod.BaseAddress:X16} size=0x{mod.Size:X}");

// Найти модуль по адресу
var targetModule = modules.FirstOrDefault(m =>
    address >= m.BaseAddress && address < m.BaseAddress + m.Size);
```

### 6.4. GetKernelModules

```csharp
IReadOnlyList<PluginKernelModuleInfo> GetKernelModules()
```

Возвращает список всех загруженных модулей ядра (драйверов).

**Возвращает:** `IReadOnlyList<PluginKernelModuleInfo>` -- список ядерных модулей с полями `BaseAddress`, `Size`, `LoadOrder`, `Name`.

```csharp
var kmods = _api.Symbols.GetKernelModules();
var ntoskrnl = kmods.FirstOrDefault(m =>
    m.Name.Contains("ntoskrnl", StringComparison.OrdinalIgnoreCase));
if (ntoskrnl != null)
    _api.Log.Info($"ntoskrnl @ 0x{ntoskrnl.BaseAddress:X16}");
```

### 6.5. RegisterFunction

```csharp
void RegisterFunction(ulong address, string? name, uint size = 0)
```

Регистрирует пользовательскую функцию по указанному адресу. После регистрации `ResolveAddress` будет возвращать указанное имя для этого адреса и адресов внутри диапазона `[address, address + size)`.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Начальный адрес функции |
| `name` | `string?` | Имя функции. Если `null` -- удаляет регистрацию |
| `size` | `uint` | **КРИТИЧЕСКИЙ ПАРАМЕТР.** Размер функции в байтах. Если указан `size > 0`, то `ResolveAddress` вернет имя этой функции для любого адреса в диапазоне `[address, address + size)` с указанием смещения (например, `"MyFunc+0x1A"`). Если `size = 0` -- имя возвращается только для точного адреса |

**ВНИМАНИЕ:** Параметр `size` критически важен для корректного отображения в дизассемблере. Без указания размера функция не будет правильно отображаться в контексте (не будут показаны смещения `+0xNN` для инструкций внутри функции). Всегда указывайте размер, если он известен.

```csharp
// Зарегистрировать функцию с размером
_api.Symbols.RegisterFunction(0x140001000, "DecryptBuffer", size: 0x120);

// Теперь ResolveAddress(0x140001050) вернет "DecryptBuffer+0x50"

// Удалить регистрацию
_api.Symbols.RegisterFunction(0x140001000, null);
```

### 6.6. GetRegisteredFunctions

```csharp
IReadOnlyList<PluginFunctionEntry> GetRegisteredFunctions()
```

Возвращает список всех функций, зарегистрированных через `RegisterFunction`.

**Возвращает:** `IReadOnlyList<PluginFunctionEntry>` -- список с полями `Address`, `Name`, `Size`.

```csharp
var funcs = _api.Symbols.GetRegisteredFunctions();
foreach (var f in funcs)
    _api.Log.Info($"  {f.Name} @ 0x{f.Address:X16} size=0x{f.Size:X}");
```

---

## 7. IProcessApi -- процессы, потоки и антиотладка

Интерфейс для перечисления процессов и потоков, управления потоками и обхода антиотладочных техник.

```csharp
public interface IProcessApi
{
    IReadOnlyList<PluginProcessInfo> EnumProcesses();
    IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid);
    bool SuspendThread(uint tid);
    bool ResumeThread(uint tid);
    (ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid);
    bool ClearDebugPort(uint pid);
    bool ClearThreadHide(uint pid);
    bool InstallNtQsiHook();
    bool RemoveNtQsiHook();
    string ProbeNtQsiHook();
    bool SetSpoofSharedUserData(bool enable);
}
```

### 7.1. Перечисление процессов и потоков

#### EnumProcesses

```csharp
IReadOnlyList<PluginProcessInfo> EnumProcesses()
```

Перечисляет все процессы на целевой машине. Работает через драйвер ядра (`ZwQuerySystemInformation`).

**Возвращает:** Список `PluginProcessInfo` с полями `ProcessId`, `SessionId`, `Name`.

```csharp
var procs = _api.Process.EnumProcesses();
foreach (var p in procs)
    _api.Log.Info($"  [{p.ProcessId}] {p.Name} (session {p.SessionId})");
```

#### EnumThreads

```csharp
IReadOnlyList<PluginThreadInfo> EnumThreads(uint pid)
```

Перечисляет все потоки процесса.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |

**Возвращает:** Список `PluginThreadInfo` с полями `ThreadId`, `StartAddress`, `State`, `Priority`.

```csharp
var threads = _api.Process.EnumThreads(_api.TargetPid);
foreach (var t in threads)
    _api.Log.Info($"  TID={t.ThreadId} start=0x{t.StartAddress:X16} " +
                  $"state={t.State} prio={t.Priority}");
```

### 7.2. Управление потоками

#### SuspendThread

```csharp
bool SuspendThread(uint tid)
```

Приостанавливает поток (увеличивает счетчик suspend).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока |

**Возвращает:** `true` при успехе.

#### ResumeThread

```csharp
bool ResumeThread(uint tid)
```

Возобновляет приостановленный поток (уменьшает счетчик suspend).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `tid` | `uint` | Идентификатор потока |

**Возвращает:** `true` при успехе.

**Пример -- заморозить все потоки кроме основного:**
```csharp
var threads = _api.Process.EnumThreads(_api.TargetPid);
foreach (var t in threads)
{
    if (t.ThreadId != _api.SelectedThreadId)
        _api.Process.SuspendThread(t.ThreadId);
}
// ... выполнить анализ ...
// Разморозить
foreach (var t in threads)
{
    if (t.ThreadId != _api.SelectedThreadId)
        _api.Process.ResumeThread(t.ThreadId);
}
```

### 7.3. GetPebAddress

```csharp
(ulong PebAddress, ulong Peb32Address) GetPebAddress(uint pid)
```

Получает адрес PEB (Process Environment Block) процесса.

**Параметры:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |

**Возвращает:** Кортеж `(ulong PebAddress, ulong Peb32Address)`:
- `PebAddress` -- адрес 64-битного PEB
- `Peb32Address` -- адрес 32-битного PEB для WoW64 процессов (или `0`)

```csharp
var (peb64, peb32) = _api.Process.GetPebAddress(_api.TargetPid);
_api.Log.Info($"PEB64 = 0x{peb64:X16}");

// Чтение BeingDebugged из PEB
// PEB.BeingDebugged = offset 0x02 (1 байт)
byte[]? beingDebugged = _api.Memory.ReadMemory(_api.TargetPid, peb64 + 2, 1);
if (beingDebugged != null)
    _api.Log.Info($"PEB.BeingDebugged = {beingDebugged[0]}");
```

### 7.4. API антиотладки

KernelFlirt предоставляет набор функций для обхода антиотладочных механизмов Windows. Все эти функции работают на уровне ядра, что делает обход невидимым для пользовательского кода.

#### ClearDebugPort

```csharp
bool ClearDebugPort(uint pid)
```

Обнуляет поле `EPROCESS.DebugPort` для указанного процесса. Это скрывает факт отладки от следующих антиотладочных проверок:

- `NtQueryInformationProcess(ProcessDebugPort)` -- вернет `0`
- `NtQueryInformationProcess(ProcessDebugObjectHandle)` -- вернет ошибку (нет объекта)
- `NtQueryInformationProcess(ProcessDebugFlags)` -- вернет `1` (не отлаживается)
- `NtClose` с невалидным дескриптором -- не вызовет исключение

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |

**Возвращает:** `true` при успехе.

```csharp
_api.Process.ClearDebugPort(_api.TargetPid);
_api.Log.Info("DebugPort обнулен");
```

#### ClearThreadHide

```csharp
bool ClearThreadHide(uint pid)
```

Сбрасывает бит `HideFromDebugger` в `CrossThreadFlags` для всех потоков процесса. Обходит антиотладочную технику `NtSetInformationThread(ThreadHideFromDebugger)`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `pid` | `uint` | Идентификатор процесса |

**Возвращает:** `true` при успехе.

```csharp
_api.Process.ClearThreadHide(_api.TargetPid);
_api.Log.Info("HideFromDebugger сброшен для всех потоков");
```

#### InstallNtQsiHook / RemoveNtQsiHook

```csharp
bool InstallNtQsiHook()
bool RemoveNtQsiHook()
```

Устанавливает/удаляет inline-хук на `NtQuerySystemInformation` для подмены результата класса `SystemKernelDebuggerInformation` (0x23). Это скрывает наличие ядерного отладчика.

**ВНИМАНИЕ:** Хук NtQSI вызывает BSOD от PatchGuard через 5-10 минут. Используйте кратковременно и обязательно удаляйте хук после использования.

```csharp
// Установить хук
if (_api.Process.InstallNtQsiHook())
    _api.Log.Info("NtQSI hook установлен (PatchGuard BSOD через 5-10 мин!)");

// ... выполнить нужные действия ...

// Удалить хук ДО срабатывания PatchGuard
_api.Process.RemoveNtQsiHook();
```

#### ProbeNtQsiHook

```csharp
string ProbeNtQsiHook()
```

Диагностическая функция: читает байты и дизассемблирует начало `NtQuerySystemInformation` для проверки состояния хука.

**Возвращает:** Строку с диагностической информацией.

#### SetSpoofSharedUserData

```csharp
bool SetSpoofSharedUserData(bool enable)
```

Включает/выключает подмену `SharedUserData` (`KUSER_SHARED_DATA`) для скрытия информации об отладчике ядра.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `enable` | `bool` | `true` -- включить подмену, `false` -- выключить |

**Возвращает:** `true` при успехе.

```csharp
_api.Process.SetSpoofSharedUserData(true);
_api.Log.Info("SharedUserData подменен");
```

**Полный пример -- обход антиотладки:**
```csharp
public void HideDebugger()
{
    if (!_api.IsBreakState) return;

    uint pid = _api.TargetPid;

    // 1. Обнулить DebugPort
    _api.Process.ClearDebugPort(pid);

    // 2. Сбросить HideFromDebugger для всех потоков
    _api.Process.ClearThreadHide(pid);

    // 3. Подменить SharedUserData
    _api.Process.SetSpoofSharedUserData(true);

    // 4. Обнулить PEB.BeingDebugged
    var (peb, _) = _api.Process.GetPebAddress(pid);
    if (peb != 0)
        _api.Memory.WriteMemory(pid, peb + 2, new byte[] { 0 });

    // 5. Обнулить PEB.NtGlobalFlag
    // Offset 0xBC (x64)
    _api.Memory.WriteMemory(pid, peb + 0xBC, BitConverter.GetBytes(0u));

    _api.Log.Info("Все антиотладочные проверки обойдены");
}
```

---

## 8. ILogApi -- журналирование

Интерфейс для вывода сообщений в лог-панель KernelFlirt.

```csharp
public interface ILogApi
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
```

| Метод | Описание | Цвет в UI |
|-------|----------|-----------|
| `Info(string message)` | Информационное сообщение. Префикс `[Plugin]` | Стандартный (белый/светлый) |
| `Warning(string message)` | Предупреждение. Префикс `[Plugin] WARNING:` | Желтый |
| `Error(string message)` | Ошибка. Префикс `[Plugin] ERROR:` | Красный |

```csharp
_api.Log.Info("Плагин инициализирован");
_api.Log.Warning("Модуль не найден, работаем без символов");
_api.Log.Error($"Чтение памяти по адресу 0x{addr:X16} не удалось");
```

**Рекомендация:** Используйте `[ИмяПлагина]` в начале сообщений для удобной идентификации:
```csharp
_api.Log.Info("[MyPlugin] Анализ завершен: найдено 42 xref-а");
```

---

## 9. IUiApi -- пользовательский интерфейс

Интерфейс для взаимодействия с UI KernelFlirt. Все методы потокобезопасны -- автоматически маршалятся в UI-поток.

```csharp
public interface IUiApi
{
    void NavigateDisassembly(ulong address);
    void AddMenuItem(string header, Action callback);
    void AddToolPanel(string title, object wpfContent);
    void AddUnpackedModule(ulong peBase, string name);
    void RefreshModulesAndSections();
    void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections);
    void DecompileFunction(ulong address);
    string GetDecompiledCode();
    void DisasmGoBack();
    void SetAddressAnnotation(ulong address, string? annotation);
    string? GetAddressAnnotation(ulong address);
    IReadOnlyDictionary<ulong, string> GetAllAnnotations();
    void RefreshDisassembly();
    void SetPluginData(string key, object? value);
    object? GetPluginData(string key);

    event Action<ulong, string>? OnNoteAdded;
    event Action<ulong, string>? OnNoteEdited;
    event Action<ulong>? OnNoteRemoved;
}
```

### 9.1. NavigateDisassembly

```csharp
void NavigateDisassembly(ulong address)
```

Прокручивает представление дизассемблера к указанному адресу.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Целевой адрес |

```csharp
// Перейти к адресу функции
ulong addr = _api.Symbols.ResolveNameToAddress("kernel32!CreateFileW");
if (addr != 0)
    _api.UI.NavigateDisassembly(addr);
```

### 9.2. DisasmGoBack

```csharp
void DisasmGoBack()
```

Возврат к предыдущей позиции дизассемблера (отмена `NavigateDisassembly`). Работает как стек навигации.

### 9.3. AddMenuItem

```csharp
void AddMenuItem(string header, Action callback)
```

Добавляет пункт в меню "Plugins" главного окна KernelFlirt.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `header` | `string` | Текст пункта меню. Символ `_` перед буквой делает ее горячей клавишей (мнемоника). Например, `"Find _Xrefs"` -- нажатие `X` активирует пункт при открытом меню |
| `callback` | `Action` | Функция обратного вызова, выполняемая при нажатии на пункт меню |

```csharp
// Простой пункт меню
_api.UI.AddMenuItem("Показать _информацию", () =>
{
    _api.Log.Info($"PID={_api.TargetPid}, Break={_api.IsBreakState}");
});

// Пункт меню с подчеркнутой мнемоникой
_api.UI.AddMenuItem("Add _Bookmark at RIP", OnAddAtRip);
```

### 9.4. AddToolPanel

```csharp
void AddToolPanel(string title, object wpfContent)
```

Добавляет пользовательскую WPF-вкладку в главное окно KernelFlirt. Вкладка появляется в нижней панели рядом со стандартными вкладками (Log, Registers, Stack и т.д.).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `title` | `string` | Заголовок вкладки |
| `wpfContent` | `object` | WPF UIElement (например, `Grid`, `StackPanel`, `UserControl`). Должен наследовать `System.Windows.UIElement` |

```csharp
var panel = new StackPanel();
panel.Children.Add(new TextBlock { Text = "Привет из плагина!" });

var btn = new Button { Content = "Нажми меня" };
btn.Click += (s, e) => _api.Log.Info("Кнопка нажата!");
panel.Children.Add(btn);

_api.UI.AddToolPanel("Моя вкладка", panel);
```

Подробнее о создании UI -- в [разделе 12](#12-разработка-пользовательского-интерфейса).

### 9.5. Аннотации адресов

Аннотации отображаются как комментарии `"; текст"` в дизассемблере рядом с инструкциями.

#### SetAddressAnnotation

```csharp
void SetAddressAnnotation(ulong address, string? annotation)
```

Устанавливает текстовую аннотацию для адреса. Если `annotation` равна `null` или пустой строке, аннотация удаляется.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес инструкции |
| `annotation` | `string?` | Текст аннотации, или `null`/`""` для удаления |

#### GetAddressAnnotation

```csharp
string? GetAddressAnnotation(ulong address)
```

Получает аннотацию для адреса, или `null`, если аннотации нет.

#### GetAllAnnotations

```csharp
IReadOnlyDictionary<ulong, string> GetAllAnnotations()
```

Возвращает все аннотации как словарь `адрес -> текст`.

#### RefreshDisassembly

```csharp
void RefreshDisassembly()
```

Обновляет представление дизассемблера для отображения измененных аннотаций.

**Пример -- автоматическое аннотирование API-вызовов:**
```csharp
// Добавить комментарий к вызову функции
_api.UI.SetAddressAnnotation(callAddress, "CreateFileW(\"config.ini\", ...)");
_api.UI.RefreshDisassembly();

// Удалить аннотацию
_api.UI.SetAddressAnnotation(callAddress, null);
_api.UI.RefreshDisassembly();

// Получить все аннотации
var all = _api.UI.GetAllAnnotations();
foreach (var (addr, text) in all)
    _api.Log.Info($"  0x{addr:X16}: {text}");
```

### 9.6. Декомпиляция

#### DecompileFunction

```csharp
void DecompileFunction(ulong address)
```

Запрашивает декомпиляцию функции по указанному адресу. Декомпиляция выполняется асинхронно через RetDec. Результат доступен через `GetDecompiledCode()` после завершения.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `address` | `ulong` | Адрес любой инструкции внутри функции |

#### GetDecompiledCode

```csharp
string GetDecompiledCode()
```

Возвращает текущий декомпилированный код (C-псевдокод). Возвращает пустую строку, если декомпиляция не выполнялась.

```csharp
_api.UI.DecompileFunction(rip);
// Дождаться декомпиляции (асинхронная операция)
await Task.Delay(2000);
string code = _api.UI.GetDecompiledCode();
if (!string.IsNullOrEmpty(code))
    _api.Log.Info($"Декомпилированный код:\n{code}");
```

### 9.7. Модули и секции

#### AddUnpackedModule

```csharp
void AddUnpackedModule(ulong peBase, string name)
```

Регистрирует динамически распакованный PE-файл как виртуальный модуль. Обновляет все представления: секции, импорты, строки, функции.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `peBase` | `ulong` | Базовый адрес PE-образа в памяти |
| `name` | `string` | Имя модуля для отображения |

#### RefreshModulesAndSections

```csharp
void RefreshModulesAndSections()
```

Принудительно обновляет списки модулей и секций в UI.

#### AddModuleSections

```csharp
void AddModuleSections(string moduleName, IReadOnlyList<PluginSectionInfo> sections)
```

Предоставляет информацию о секциях модуля напрямую (минуя парсинг PE-заголовка). Используется, когда упаковщик обнуляет PE-заголовок (анти-дамп).

| Параметр | Тип | Описание |
|----------|-----|----------|
| `moduleName` | `string` | Имя модуля |
| `sections` | `IReadOnlyList<PluginSectionInfo>` | Список секций с полями `Name`, `VirtualAddress`, `VirtualSize`, `Characteristics` |

```csharp
// Пример для упакованного бинарника с затертым заголовком
var sections = new List<PluginSectionInfo>
{
    new() { Name = ".text", VirtualAddress = peBase + 0x1000,
            VirtualSize = 0x5000, Characteristics = 0x60000020 },
    new() { Name = ".rdata", VirtualAddress = peBase + 0x6000,
            VirtualSize = 0x2000, Characteristics = 0x40000040 },
    new() { Name = ".data", VirtualAddress = peBase + 0x8000,
            VirtualSize = 0x1000, Characteristics = 0xC0000040 }
};
_api.UI.AddModuleSections("packed.exe", sections);
```

### 9.8. Межплагинное взаимодействие

#### SetPluginData

```csharp
void SetPluginData(string key, object? value)
```

Сохраняет произвольные данные в общем хранилище (словарь в памяти). Данные доступны любому плагину.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `key` | `string` | Уникальный ключ |
| `value` | `object?` | Значение (любой объект). `null` для удаления |

#### GetPluginData

```csharp
object? GetPluginData(string key)
```

Извлекает данные, сохраненные через `SetPluginData`.

| Параметр | Тип | Описание |
|----------|-----|----------|
| `key` | `string` | Ключ |

**Возвращает:** Сохраненный объект, или `null`, если ключ не найден.

Подробнее -- в [разделе 14](#14-межплагинное-взаимодействие).

### 9.9. События заметок

Эти события вызываются, когда пользователь добавляет, редактирует или удаляет заметки (аннотации) через контекстное меню дизассемблера, MCP или AI Assistant.

| Событие | Сигнатура | Описание |
|---------|-----------|----------|
| `OnNoteAdded` | `Action<ulong, string>` | Пользователь добавил заметку. Параметры: адрес, текст |
| `OnNoteEdited` | `Action<ulong, string>` | Пользователь отредактировал заметку. Параметры: адрес, новый текст |
| `OnNoteRemoved` | `Action<ulong>` | Пользователь удалил заметку. Параметр: адрес |

```csharp
api.UI.OnNoteAdded += (addr, note) =>
    _api.Log.Info($"Заметка добавлена: 0x{addr:X16} = {note}");

api.UI.OnNoteEdited += (addr, note) =>
    _api.Log.Info($"Заметка изменена: 0x{addr:X16} = {note}");

api.UI.OnNoteRemoved += addr =>
    _api.Log.Info($"Заметка удалена: 0x{addr:X16}");
```

---

## 10. Модели данных

### 10.1. PluginRegister

Представляет регистр процессора.

```csharp
public class PluginRegister
{
    public string Name { get; set; } = "";     // Имя регистра ("RAX", "RIP", "ZF" и т.д.)
    public ulong Value { get; set; }           // Значение регистра
    public bool IsFlag { get; set; }           // true для индивидуальных флагов (CF, ZF, SF...)
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `Name` | `string` | Имя регистра. Регистры общего назначения: `RAX`, `RBX`, `RCX`, `RDX`, `RSI`, `RDI`, `RBP`, `RSP`, `R8`-`R15`. Указатель инструкций: `RIP`. Флаговый регистр: `RFLAGS`. Отладочные: `DR0`-`DR7`. Сегментные: `CS`, `DS`, `ES`, `FS`, `GS`, `SS`. Индивидуальные флаги (с `IsFlag=true`): `CF`, `ZF`, `SF`, `OF`, `PF`, `AF`, `DF`, `TF`, `IF` |
| `Value` | `ulong` | 64-битное значение регистра. Для флагов: `0` или `1` |
| `IsFlag` | `bool` | `true` для индивидуальных флагов (`CF`, `ZF` и т.д.), `false` для обычных регистров |

### 10.2. PluginBreakpoint

Описывает установленную точку останова.

```csharp
public class PluginBreakpoint
{
    public uint Handle { get; set; }              // Уникальный дескриптор
    public ulong Address { get; set; }            // Адрес точки останова
    public PluginBreakpointType Type { get; set; } // Тип (Software, Hardware, ...)
    public bool Enabled { get; set; }             // Активна ли точка останова
    public string? Condition { get; set; }        // Условие (зарезервировано)
    public uint HitCount { get; set; }            // Количество срабатываний
    public byte OriginalByte { get; set; }        // Оригинальный байт (для Software BP)
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `Handle` | `uint` | Уникальный дескриптор для удаления через `RemoveBreakpoint` |
| `Address` | `ulong` | Виртуальный адрес точки останова |
| `Type` | `PluginBreakpointType` | Тип: `Software`, `Hardware`, `HwWrite`, `HwReadWrite`, `Memory` |
| `Enabled` | `bool` | `true`, если точка останова активна |
| `Condition` | `string?` | Условие срабатывания (зарезервировано для будущего использования) |
| `HitCount` | `uint` | Количество срабатываний с момента установки |
| `OriginalByte` | `byte` | Оригинальный байт, замененный на `0xCC` (только для `Software` BP) |

### 10.3. PluginModuleInfo

Информация о загруженном модуле пользовательского режима.

```csharp
public class PluginModuleInfo
{
    public ulong BaseAddress { get; set; }  // Базовый адрес в виртуальном пространстве
    public uint Size { get; set; }          // Размер модуля
    public string Name { get; set; } = "";  // Имя файла (например, "kernel32.dll")
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `BaseAddress` | `ulong` | Базовый адрес загрузки модуля в адресном пространстве процесса |
| `Size` | `uint` | Размер образа модуля в байтах |
| `Name` | `string` | Имя файла модуля (например, `"kernel32.dll"`, `"ntdll.dll"`, `"target.exe"`) |

### 10.4. PluginKernelModuleInfo

Информация о модуле ядра (драйвере).

```csharp
public class PluginKernelModuleInfo
{
    public ulong BaseAddress { get; set; }    // Адрес в ядерном пространстве
    public uint Size { get; set; }            // Размер
    public ushort LoadOrder { get; set; }     // Порядок загрузки
    public string Name { get; set; } = "";    // Имя (например, "ntoskrnl.exe")
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `BaseAddress` | `ulong` | Виртуальный адрес в пространстве ядра |
| `Size` | `uint` | Размер модуля |
| `LoadOrder` | `ushort` | Порядковый номер загрузки (0 = ntoskrnl) |
| `Name` | `string` | Имя файла драйвера/модуля ядра |

### 10.5. PluginProcessInfo

Информация о процессе.

```csharp
public class PluginProcessInfo
{
    public uint ProcessId { get; set; }    // PID
    public uint SessionId { get; set; }    // ID сессии
    public string Name { get; set; } = ""; // Имя процесса ("notepad.exe")
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `ProcessId` | `uint` | Идентификатор процесса (PID) |
| `SessionId` | `uint` | Идентификатор сессии Windows |
| `Name` | `string` | Имя исполняемого файла процесса |

### 10.6. PluginThreadInfo

Информация о потоке.

```csharp
public class PluginThreadInfo
{
    public uint ThreadId { get; set; }       // TID
    public ulong StartAddress { get; set; }  // Адрес начальной функции
    public uint State { get; set; }          // Состояние потока (флаги)
    public uint Priority { get; set; }       // Приоритет
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `ThreadId` | `uint` | Идентификатор потока (TID) |
| `StartAddress` | `ulong` | Адрес стартовой функции потока (начальная точка входа) |
| `State` | `uint` | Флаги состояния потока |
| `Priority` | `uint` | Приоритет потока |

### 10.7. PluginSectionInfo

Информация о секции PE-образа.

```csharp
public class PluginSectionInfo
{
    public string Name { get; set; } = "";        // ".text", ".rdata", ".data"
    public ulong VirtualAddress { get; set; }     // Абсолютный виртуальный адрес
    public uint VirtualSize { get; set; }         // Размер секции
    public uint Characteristics { get; set; }     // Характеристики PE-секции
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `Name` | `string` | Имя секции (`.text`, `.rdata`, `.data`, `.rsrc` и т.д.) |
| `VirtualAddress` | `ulong` | **Абсолютный** виртуальный адрес секции (не RVA!) |
| `VirtualSize` | `uint` | Размер секции в байтах |
| `Characteristics` | `uint` | Характеристики PE-секции. Стандартные значения: `0x60000020` (код, чтение+выполнение), `0x40000040` (данные, чтение), `0xC0000040` (данные, чтение+запись) |

### 10.8. PluginFunctionEntry

Запись о пользовательской функции, зарегистрированной через `RegisterFunction`.

```csharp
public class PluginFunctionEntry
{
    public ulong Address { get; set; }     // Адрес начала функции
    public string Name { get; set; } = ""; // Имя функции
    public uint Size { get; set; }         // Размер функции в байтах
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `Address` | `ulong` | Начальный адрес функции |
| `Name` | `string` | Имя функции |
| `Size` | `uint` | Размер тела функции в байтах |

### 10.9. PluginDebugEvent

Описывает отладочное событие. Передается в `OnDebugEvent` и `OnDebugEventFilter`. Содержит как поля для чтения (информация о событии), так и записываемые поля (управление возобновлением).

```csharp
public class PluginDebugEvent
{
    // Информация о событии (чтение)
    public PluginDebugEventType Type { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public ulong Address { get; set; }
    public bool IsKernelMode { get; set; }
    public uint ExceptionCode { get; set; }
    public ulong FaultAddress { get; set; }
    public uint AccessType { get; set; }

    // Управление возобновлением (запись)
    public uint ContinueMode { get; set; }
    public ulong NewRip { get; set; }
    public ulong NewRsp { get; set; }
    public ulong TraceRangeBase { get; set; }
    public ulong TraceRangeEnd { get; set; }
    public uint TraceMaxSteps { get; set; }
}
```

**Поля информации о событии:**

| Поле | Тип | Описание |
|------|-----|----------|
| `Type` | `PluginDebugEventType` | Тип события: `Breakpoint`, `SingleStep`, `HwBreakpoint`, `HwWatchpoint`, `MemoryBp`, `AccessViolation` |
| `ProcessId` | `uint` | PID процесса, вызвавшего событие |
| `ThreadId` | `uint` | TID потока, вызвавшего событие |
| `Address` | `ulong` | RIP в момент события |
| `IsKernelMode` | `bool` | `true`, если событие произошло в режиме ядра |
| `ExceptionCode` | `uint` | Код исключения Windows (например, `0x80000003` для INT3, `0xC0000005` для AV) |
| `FaultAddress` | `ulong` | Для `AccessViolation`: адрес, к которому произошел доступ |
| `AccessType` | `uint` | Для `AccessViolation`: тип доступа -- `0`=чтение, `1`=запись, `8`=выполнение |

**Поля управления возобновлением (записываемые):**

| Поле | Тип | Описание |
|------|-----|----------|
| `ContinueMode` | `uint` | Режим продолжения. Подробнее в [разделе 11.5](#115-continuemode----управление-возобновлением) |
| `NewRip` | `ulong` | Перенаправление RIP при возобновлении. `0` = не перенаправлять |
| `NewRsp` | `ulong` | Перенаправление RSP при возобновлении. `0` = не изменять |
| `TraceRangeBase` | `ulong` | Для ContinueMode=4 (Trace): начало диапазона трассировки (включительно) |
| `TraceRangeEnd` | `ulong` | Для ContinueMode=4 (Trace): конец диапазона трассировки (исключительно) |
| `TraceMaxSteps` | `uint` | Для ContinueMode=4 (Trace): максимум шагов (0 = 500 000 по умолчанию) |

### 10.10. PluginBreakpointType (перечисление)

```csharp
public enum PluginBreakpointType
{
    Software = 0,     // INT3 (0xCC)
    Hardware = 1,     // DR0-DR3, выполнение
    HwWrite = 2,      // DR0-DR3, запись
    HwReadWrite = 3,  // DR0-DR3, чтение/запись
    Memory = 4        // Защита страниц (guard page)
}
```

### 10.11. PluginDebugEventType (перечисление)

```csharp
public enum PluginDebugEventType
{
    Breakpoint = 1,       // Программная точка останова (INT3)
    SingleStep = 2,       // Шаг выполнен (Trap Flag)
    HwBreakpoint = 3,     // Аппаратная точка останова (выполнение)
    HwWatchpoint = 4,     // Аппаратный watchpoint (данные)
    MemoryBp = 5,         // Memory breakpoint
    AccessViolation = 6   // Нарушение доступа (page fault)
}
```

### 10.12. PluginScriptHost

Хост глобальных переменных для плагина Scripting. Определен в SDK для совместимости с `AssemblyLoadContext`.

```csharp
public class PluginScriptHost
{
    public IDebuggerApi api { get; set; } = null!;    // Ссылка на API отладчика
    public Action<string> print { get; set; }          // Функция вывода (Console.WriteLine)
}
```

---

## 11. События отладки

Система событий -- ключевой механизм для создания мощных плагинов. KernelFlirt предоставляет два уровня обработки событий: информационный (`OnDebugEvent`) и фильтрующий (`OnDebugEventFilter`).

### 11.1. OnDebugEvent

```csharp
event Action<PluginDebugEvent>? OnDebugEvent;
```

Информационное событие, вызываемое в **UI-потоке** после того, как UI обработал отладочное событие. Плагин получает данные о событии, но не может повлиять на его обработку.

**Поток вызова:** UI-поток (Dispatcher)

**Когда использовать:**
- Логирование событий
- Обновление UI-панелей при остановках
- Сбор статистики (подсчет срабатываний BP)

```csharp
api.OnDebugEvent += (evt) =>
{
    _api.Log.Info($"[Event] {evt.Type} @ 0x{evt.Address:X16} " +
                  $"PID={evt.ProcessId} TID={evt.ThreadId}");
};
```

### 11.2. OnDebugEventFilter

```csharp
event Func<PluginDebugEvent, bool>? OnDebugEventFilter;
```

**Самый мощный механизм SDK.** Фильтр событий, вызываемый в **фоновом потоке** ПЕРЕД обработкой UI. Позволяет перехватывать события и самостоятельно управлять выполнением процесса.

**Поток вызова:** Фоновый поток (НЕ UI)

**Возвращаемое значение:**
- `true` -- событие подавлено, UI его не увидит. Плагин должен вызвать `Continue()`, `SingleStep()` или установить `ContinueMode`
- `false` -- событие передается UI для обычной обработки

**Когда использовать:**
- Автоматическая распаковка (OEP detection)
- IAT-трассировка
- Guard page мониторинг
- Условные точки останова
- Автоматизация (логирование API-вызовов без остановки)

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    // Пример: пропускать BP на определенном адресе
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _autoContinueAddr)
    {
        // Записать лог, но не останавливать
        _api.Log.Info($"[Auto] BP at 0x{evt.Address:X16}, продолжаем");
        evt.ContinueMode = 1; // StepPast (шагнуть через INT3, потом продолжить)
        return true; // подавить UI
    }
    return false; // пустить в UI
}
```

**Критические правила:**
1. Не вызывайте WPF-элементы напрямую из фильтра -- используйте `Dispatcher.BeginInvoke`
2. Если вернули `true` -- процесс ДОЛЖЕН быть возобновлен (через `ContinueMode` или `Continue()`)
3. Не выполняйте долгие операции в фильтре -- это блокирует обработку событий
4. Фильтр вызывается для КАЖДОГО события -- будьте эффективны

### 11.3. OnBeforeRun

```csharp
event Action? OnBeforeRun;
```

Вызывается непосредственно перед запуском процесса (Run/F9/Continue). Идеальное место для установки "ленивых" точек останова.

**Поток вызова:** UI-поток

```csharp
api.OnBeforeRun += () =>
{
    // Установить BP только перед запуском, не при инициализации
    if (_needSetBp && _bpAddress != 0)
    {
        _api.Breakpoints.SetBreakpoint(_api.TargetPid, 0,
            _bpAddress, PluginBreakpointType.Software);
        _needSetBp = false;
    }
};
```

### 11.4. OnBreakStateEntered / OnBreakStateExited

```csharp
event Action? OnBreakStateEntered;
event Action? OnBreakStateExited;
```

Вызываются при остановке/возобновлении процесса.

**Поток вызова:** UI-поток

**Типичное использование:**
- Обновление данных в панелях при остановке
- Блокировка/разблокировка кнопок

```csharp
api.OnBreakStateEntered += () =>
{
    // Обновить данные в нашей панели
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        RefreshData();
    });
};
```

### 11.5. ContinueMode -- управление возобновлением

Поле `ContinueMode` в `PluginDebugEvent` управляет тем, как процесс возобновит выполнение после обработки события в фильтре.

| Значение | Имя | Описание |
|----------|-----|----------|
| `0` | Run | Возобновить выполнение нормально (по умолчанию) |
| `1` | StepPast | Шагнуть через программную точку останова, затем автоматически продолжить. Аналог F9 через BP -- восстанавливает оригинальный байт, делает шаг, ставит INT3 обратно, продолжает |
| `2` | StepInto | Шагнуть через программную точку останова, затем остановиться (Single Step). Аналог F7 через BP |
| `3` | Handled | Подавить исключение (AV не дойдет до SEH процесса) + установить Trap Flag для single-step. Используется для guard page трассировки |
| `4` | Trace | Быстрая трассировка на стороне драйвера. Драйвер шагает внутренне, пока RIP находится в `[TraceRangeBase, TraceRangeEnd)`. Событие SingleStep приходит только когда RIP выходит за диапазон или исчерпан `TraceMaxSteps` |

### 11.6. Трассировка guard page

Guard page -- мощная техника для отслеживания доступа к памяти без аппаратных ограничений. Основана на изменении защиты страниц и перехвате Access Violation.

**Алгоритм:**
1. Установить `PAGE_NOACCESS` на отслеживаемую область
2. При Access Violation на эту область:
   - Снять защиту (установить `PAGE_READWRITE`)
   - Записать информацию о доступе
   - Установить `ContinueMode = 3` (Handled + TF)
   - Вернуть `true` из фильтра
3. При SingleStep (TF):
   - Восстановить `PAGE_NOACCESS`
   - Вернуть `true` из фильтра

```csharp
private ulong _guardBase;
private uint _guardSize;
private bool _rearmOnStep;

private void StartGuardPage(ulong baseAddr, uint size)
{
    _guardBase = baseAddr;
    _guardSize = size;
    _api.Memory.ProtectMemory(_api.TargetPid, _guardBase, _guardSize, 0x01); // PAGE_NOACCESS
}

private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.AccessViolation)
    {
        if (evt.FaultAddress >= _guardBase &&
            evt.FaultAddress < _guardBase + _guardSize)
        {
            // Снять защиту, позволить доступ
            _api.Memory.ProtectMemory(_api.TargetPid, _guardBase, _guardSize, 0x04);

            string accessStr = evt.AccessType switch
            {
                0 => "READ",
                1 => "WRITE",
                8 => "EXECUTE",
                _ => $"UNKNOWN({evt.AccessType})"
            };
            _api.Log.Info($"[Guard] {accessStr} @ 0x{evt.FaultAddress:X16} " +
                          $"from 0x{evt.Address:X16}");

            evt.ContinueMode = 3; // Handled: подавить AV + установить TF
            _rearmOnStep = true;
            return true;
        }
    }

    if (evt.Type == PluginDebugEventType.SingleStep && _rearmOnStep)
    {
        // Восстановить guard page
        _api.Memory.ProtectMemory(_api.TargetPid, _guardBase, _guardSize, 0x01);
        _rearmOnStep = false;
        return true; // подавить, продолжить
    }

    return false;
}
```

### 11.7. Быстрая трассировка на стороне драйвера

`ContinueMode = 4` (Trace) -- специализированный режим для IAT-трассировки через обертки упаковщиков. Драйвер выполняет трассировку внутренне, не передавая каждый шаг в UI, что значительно ускоряет процесс.

```csharp
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _wrapperEntry)
    {
        // Трассировать через обертку упаковщика до выхода за ее границы
        evt.ContinueMode = 4; // Trace
        evt.TraceRangeBase = _wrapperBase;
        evt.TraceRangeEnd = _wrapperBase + _wrapperSize;
        evt.TraceMaxSteps = 10000; // максимум шагов
        return true;
    }

    if (evt.Type == PluginDebugEventType.SingleStep)
    {
        // RIP вышел за границы обертки -- это адрес настоящей API
        ulong realApi = evt.Address;
        string? name = _api.Symbols.ResolveAddress(realApi);
        _api.Log.Info($"[IAT] wrapper -> {name ?? $"0x{realApi:X16}"}");
        return true;
    }

    return false;
}
```

**Параметры Trace-режима:**
- `TraceRangeBase` -- начало диапазона (включительно). Пока RIP в диапазоне, драйвер шагает
- `TraceRangeEnd` -- конец диапазона (исключительно). Когда RIP выходит за диапазон, генерируется SingleStep
- `TraceMaxSteps` -- предохранитель. `0` = 500 000 шагов по умолчанию

---

## 12. Разработка пользовательского интерфейса

### 12.1. WPF-контролы в плагинах

KernelFlirt построен на WPF (Windows Presentation Foundation). Плагины создают UI-элементы с использованием стандартных WPF-контролов из пространства имен `System.Windows.Controls`.

Для использования WPF в плагине необходимо:
1. Указать `<UseWPF>true</UseWPF>` в `.csproj`
2. Добавить `using System.Windows;` и `using System.Windows.Controls;`

**Доступные контролы:**
- `StackPanel` -- вертикальная/горизонтальная компоновка
- `Grid` -- табличная компоновка
- `Button` -- кнопка
- `TextBlock` -- текстовая метка
- `TextBox` -- поле ввода
- `DataGrid` -- таблица данных
- `ListView` -- списковое представление
- `ComboBox` -- выпадающий список
- `CheckBox` -- флажок
- `ContextMenu` -- контекстное меню
- `MenuItem` -- пункт меню
- `ScrollViewer` -- скроллируемая область
- `TabControl` -- вкладки внутри панели
- `UserControl` -- пользовательский составной контрол

### 12.2. AddToolPanel -- добавление вкладок

Метод `AddToolPanel` добавляет вкладку в основную панель инструментов KernelFlirt. Вкладка отображается вместе со стандартными вкладками (Log, Registers, Stack, Modules и т.д.).

**Рекомендуемая структура панели:**
```csharp
private void BuildUi()
{
    // Корневой контейнер -- Grid с тремя строками
    var root = new Grid();
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Тулбар
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Контент
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Статусбар

    // Тулбар
    var toolbar = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(4)
    };
    var addBtn = new Button
    {
        Content = "+ Добавить",
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 4, 0)
    };
    addBtn.Click += (_, _) => OnAdd();
    toolbar.Children.Add(addBtn);

    Grid.SetRow(toolbar, 0);
    root.Children.Add(toolbar);

    // Основной контент (например, DataGrid)
    _grid = new DataGrid { /* ... настройки ... */ };
    Grid.SetRow(_grid, 1);
    root.Children.Add(_grid);

    // Статус
    var status = new TextBlock
    {
        Margin = new Thickness(4),
        Foreground = Brushes.Gray,
        FontSize = 11
    };
    Grid.SetRow(status, 2);
    root.Children.Add(status);

    // Зарегистрировать вкладку
    _api.UI.AddToolPanel("Мой плагин", root);
}
```

### 12.3. AddMenuItem -- добавление пунктов меню

Пункты меню добавляются в раздел "Plugins" главного меню.

```csharp
// Простой пункт
_api.UI.AddMenuItem("Показать _информацию", ShowInfo);

// Несколько пунктов
_api.UI.AddMenuItem("Save _Session...", OnSave);
_api.UI.AddMenuItem("Load S_ession...", OnLoad);

// Мнемоника: символ '_' перед буквой делает ее горячей клавишей
// "Save _Session" -> подчеркнутая 'S'
// "Load S_ession" -> подчеркнутая 'e' (чтобы не конфликтовать с первой S)
```

### 12.4. DataGrid -- таблицы данных

`DataGrid` -- основной контрол для отображения табличных данных в плагинах. Рекомендуемые настройки для единого стиля с KernelFlirt:

```csharp
_grid = new DataGrid
{
    AutoGenerateColumns = false,         // Ручное определение колонок
    IsReadOnly = true,                   // Только чтение
    SelectionMode = DataGridSelectionMode.Single,  // Одиночный выбор
    HeadersVisibility = DataGridHeadersVisibility.Column, // Заголовки только колонок
    GridLinesVisibility = DataGridGridLinesVisibility.None, // Без сетки
    Background = Brushes.Transparent,    // Прозрачный фон (для темной темы)
    BorderThickness = new Thickness(0),  // Без рамки
    RowBackground = Brushes.Transparent,
    AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
    FontFamily = new FontFamily("Consolas"),  // Моноширинный шрифт
    FontSize = 12
};

// Определение колонок
_grid.Columns.Add(new DataGridTextColumn
{
    Header = "Адрес",
    Binding = new System.Windows.Data.Binding("AddressHex"),
    Width = 150
});

_grid.Columns.Add(new DataGridTextColumn
{
    Header = "Модуль",
    Binding = new System.Windows.Data.Binding("Display"),
    Width = 160
});

_grid.Columns.Add(new DataGridTextColumn
{
    Header = "Заметка",
    Binding = new System.Windows.Data.Binding("Note"),
    Width = new DataGridLength(1, DataGridLengthUnitType.Star) // Растягиваемая
});
```

**Обновление данных:**
```csharp
private void RefreshGrid()
{
    _grid.ItemsSource = null;        // Сбросить привязку
    _grid.ItemsSource = _dataList;   // Установить новую
}
```

**Обработка двойного клика:**
```csharp
_grid.MouseDoubleClick += (sender, e) =>
{
    if (_grid.SelectedItem is MyDataItem item)
        _api.UI.NavigateDisassembly(item.Address);
};
```

### 12.5. Контекстные меню

```csharp
var ctx = new ContextMenu();

var goToItem = new MenuItem { Header = "Перейти к адресу" };
goToItem.Click += (_, _) =>
{
    if (_grid.SelectedItem is Bookmark bm)
        _api.UI.NavigateDisassembly(bm.Address);
};
ctx.Items.Add(goToItem);

var editItem = new MenuItem { Header = "Редактировать..." };
editItem.Click += (_, _) => OnEditSelected();
ctx.Items.Add(editItem);

ctx.Items.Add(new Separator()); // Разделитель

var removeItem = new MenuItem { Header = "Удалить" };
removeItem.Click += (_, _) => OnRemoveSelected();
ctx.Items.Add(removeItem);

_grid.ContextMenu = ctx;
```

### 12.6. Темизация и кисти

KernelFlirt использует темную тему. Для гармоничного интерфейса используйте следующие цвета:

```csharp
// Прозрачный фон (для наследования темы от родителя)
Background = Brushes.Transparent;

// Полупрозрачный альтернирующий фон строк
AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

// Серый текст для статуса
Foreground = Brushes.Gray;

// Моноширинный шрифт для адресов и кода
FontFamily = new FontFamily("Consolas");
```

**Рекомендации:**
- Избегайте жестко заданных светлых фонов (белый, светло-серый)
- Используйте `Brushes.Transparent` для фона контролов
- Моноширинный шрифт `Consolas` для адресов и шестнадцатеричных данных
- Стандартный шрифт для текстовых описаний

### 12.7. Диалоговые окна

Для ввода данных от пользователя создавайте модальные диалоговые окна:

```csharp
private static string? PromptString(string title, string prompt, string defaultValue)
{
    var dlg = new Window
    {
        Title = title,
        Width = 400,
        Height = 150,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Owner = Application.Current.MainWindow
    };

    var sp = new StackPanel { Margin = new Thickness(12) };
    sp.Children.Add(new TextBlock
    {
        Text = prompt,
        Margin = new Thickness(0, 0, 0, 6)
    });

    var tb = new TextBox { Text = defaultValue };
    sp.Children.Add(tb);

    var btnPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 10, 0, 0)
    };

    var okBtn = new Button
    {
        Content = "OK",
        Width = 70,
        IsDefault = true,
        Margin = new Thickness(0, 0, 6, 0)
    };
    okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };

    var cancelBtn = new Button { Content = "Cancel", Width = 70, IsCancel = true };

    btnPanel.Children.Add(okBtn);
    btnPanel.Children.Add(cancelBtn);
    sp.Children.Add(btnPanel);

    dlg.Content = sp;
    tb.Focus();
    tb.SelectAll();

    return dlg.ShowDialog() == true ? tb.Text : null;
}
```

Для диалогов сохранения/открытия файла используйте стандартные WPF-диалоги:

```csharp
// Сохранить файл
var dlg = new Microsoft.Win32.SaveFileDialog
{
    Filter = "KF Session (*.kfsession)|*.kfsession",
    Title = "Сохранить сессию",
    DefaultExt = ".kfsession",
    FileName = "session.kfsession"
};
if (dlg.ShowDialog() == true)
{
    File.WriteAllText(dlg.FileName, json);
}

// Открыть файл
var openDlg = new Microsoft.Win32.OpenFileDialog
{
    Filter = "KF Session (*.kfsession)|*.kfsession",
    Title = "Загрузить сессию"
};
if (openDlg.ShowDialog() == true)
{
    string json = File.ReadAllText(openDlg.FileName);
}
```

---

## 13. Потоки и асинхронность

### 13.1. UI-поток и Dispatcher

KernelFlirt -- WPF-приложение. Все операции с UI-элементами должны выполняться в UI-потоке. Если вы обрабатываете события, приходящие из фоновых потоков, используйте `Dispatcher`:

```csharp
// Способ 1: BeginInvoke (неблокирующий, отложенный вызов)
Application.Current.Dispatcher.BeginInvoke(() =>
{
    _grid.ItemsSource = null;
    _grid.ItemsSource = _results;
});

// Способ 2: Invoke (блокирующий, ждет завершения)
Application.Current.Dispatcher.Invoke(() =>
{
    return _someTextBlock.Text;
});

// Способ 3: Проверка необходимости маршалинга
var dispatcher = Application.Current.Dispatcher;
if (dispatcher != null && !dispatcher.CheckAccess())
    dispatcher.Invoke(UpdateUi);
else
    UpdateUi();
```

**Когда нужен Dispatcher:**

| Ситуация | Поток | Dispatcher нужен? |
|----------|-------|-------------------|
| `Initialize()` | UI | Нет |
| `OnBreakStateEntered` | UI | Нет (но лучше использовать `BeginInvoke` для безопасности) |
| `OnDebugEvent` | UI | Нет |
| `OnDebugEventFilter` | **Фоновый** | **Да, для любых UI-операций** |
| Свой `Task.Run()` | **Фоновый** | **Да** |

**Важно:** Методы `IUiApi` (NavigateDisassembly, AddToolPanel и т.д.) автоматически маршалятся в UI-поток. Для них Dispatcher не нужен. Но обновление ваших собственных WPF-контролов требует явного маршалинга.

### 13.2. Фоновые задачи

Для длительных операций (сканирование памяти, анализ) используйте `Task.Run()`:

```csharp
private CancellationTokenSource? _cts;

private void StartScan()
{
    _cts = new CancellationTokenSource();
    var token = _cts.Token;

    Task.Run(() =>
    {
        _api.Log.Info("[Scan] Начало сканирования...");

        for (ulong addr = _scanBase; addr < _scanEnd; addr += 0x1000)
        {
            if (token.IsCancellationRequested) break;

            byte[]? page = _api.Memory.ReadMemory(_api.TargetPid, addr, 0x1000);
            if (page != null)
            {
                // Анализ данных...
                AnalyzePage(addr, page);
            }
        }

        // Обновить UI с результатами
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RefreshGrid();
            _api.Log.Info($"[Scan] Завершено, найдено {_results.Count} результатов");
        });
    }, token);
}

private void StopScan()
{
    _cts?.Cancel();
}
```

### 13.3. Паттерн async/await

Используйте `async/await` для неблокирующих операций:

```csharp
// Экспорт функции для MCP/AI через SetPluginData
Func<string, Task<string>> executeAsync = async (code) =>
{
    try
    {
        return await _engine.ExecuteAsync(code);
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
};
_api.UI.SetPluginData("ScriptExecute", executeAsync);
```

### 13.4. Потокобезопасность API

| API | Потокобезопасен? | Примечание |
|-----|-----------------|------------|
| `IMemoryApi` | Да | Вызовы сериализуются через IOCTL |
| `IBreakpointApi` | Да | Вызовы через IOCTL |
| `ISymbolApi` | Да | Внутренняя синхронизация |
| `IProcessApi` | Да | Вызовы через IOCTL |
| `ILogApi` | Да | Dispatch в UI-поток автоматически |
| `IUiApi` | Да | Dispatch в UI-поток автоматически |
| `IsBreakState`, `IsConnected` и т.д. | Да | Volatile read |
| `Continue()`, `SingleStep()` и т.д. | Зависит | Безопасно из `OnDebugEventFilter`, иначе вызывать из UI-потока |

---

## 14. Межплагинное взаимодействие

### 14.1. SetPluginData / GetPluginData

KernelFlirt предоставляет простой механизм для обмена данными между плагинами через общее хранилище "ключ-значение".

```csharp
// Плагин A: сохранить данные
_api.UI.SetPluginData("MyPlugin.Results", resultsList);

// Плагин B: прочитать данные
var results = _api.UI.GetPluginData("MyPlugin.Results") as List<ScanResult>;
if (results != null)
{
    // Использовать данные другого плагина
}
```

**Рекомендации по ключам:**
- Используйте префикс с именем плагина: `"GraphBlockColors"`, `"ScriptExecute"`
- Документируйте типы значений для других разработчиков
- Устанавливайте значение в `null` для очистки

### 14.2. Примеры обмена данными

**Пример 1: GraphView и SessionPlugin**

Плагин GraphView сохраняет цвета блоков через `SetPluginData`, а SessionPlugin сохраняет/восстанавливает их из файла сессии:

```csharp
// GraphView: сохранить цвета блоков
var colors = new Dictionary<ulong, Color>();
colors[0x140001000] = Colors.Red;
colors[0x140001050] = Colors.Green;
_api.UI.SetPluginData("GraphBlockColors", colors);

// SessionPlugin: получить и сохранить цвета
if (_api.UI.GetPluginData("GraphBlockColors") is Dictionary<ulong, Color> colors)
{
    foreach (var (addr, color) in colors)
    {
        // Сохранить в файл сессии
        data.BlockColors.Add(new BlockColorEntry
        {
            Address = addr,
            Color = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        });
    }
}
```

**Пример 2: Scripting и MCP**

Плагин Scripting экспортирует функцию выполнения скриптов через `SetPluginData`, а MCP-сервер вызывает ее:

```csharp
// ScriptingPlugin: экспорт функции
Func<string, Task<string>> executeScript = async (code) =>
{
    try { return await _engine.ExecuteAsync(code); }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
};
_api.UI.SetPluginData("ScriptExecute", executeScript);

// McpServerPlugin: использование
var exec = _api.UI.GetPluginData("ScriptExecute") as Func<string, Task<string>>;
if (exec != null)
{
    string result = await exec("api.Memory.ReadRegisters(api.TargetPid, api.SelectedThreadId)");
}
```

---

## 15. Персистентность плагинов

### 15.1. Сохранение состояния в JSON

Для сохранения состояния плагина между сессиями используйте JSON-файлы в папке `plugins/`:

```csharp
using System.IO;
using System.Text.Json;

private string _savePath = "";

public void Initialize(IDebuggerApi api)
{
    _api = api;
    var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
    _savePath = Path.Combine(pluginsDir, "myplugin_data.json");
    LoadFromDisk();
}

public void Shutdown()
{
    SaveToDisk();
}

private void SaveToDisk()
{
    try
    {
        var json = JsonSerializer.Serialize(_data,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_savePath, json);
    }
    catch (Exception ex)
    {
        _api.Log.Warning($"[MyPlugin] Ошибка сохранения: {ex.Message}");
    }
}

private void LoadFromDisk()
{
    try
    {
        if (!File.Exists(_savePath)) return;
        var json = File.ReadAllText(_savePath);
        _data = JsonSerializer.Deserialize<MyData>(json) ?? new MyData();
    }
    catch (Exception ex)
    {
        _api.Log.Warning($"[MyPlugin] Ошибка загрузки: {ex.Message}");
    }
}
```

### 15.2. Автоматическое переключение по таргету

Плагин BookmarksPlugin демонстрирует привязку данных к конкретному отлаживаемому процессу:

```csharp
private string _currentTarget = "";

private void UpdateTarget()
{
    // Определить имя таргета из первого модуля
    string target = "";
    var modules = _api.Symbols.GetModules();
    if (modules.Count > 0)
        target = Path.GetFileNameWithoutExtension(modules[0].Name);

    // Fallback: определить по ядерному модулю на текущем RIP
    if (string.IsNullOrEmpty(target))
    {
        var kmods = _api.Symbols.GetKernelModules();
        var regs = _api.Memory.ReadRegisters(_api.TargetPid, _api.SelectedThreadId);
        var rip = regs.FirstOrDefault(r => r.Name == "RIP" || r.Name == "EIP");
        if (rip != null)
        {
            var km = kmods.FirstOrDefault(m =>
                rip.Value >= m.BaseAddress && rip.Value < m.BaseAddress + m.Size);
            if (km != null)
                target = Path.GetFileNameWithoutExtension(km.Name);
        }
    }

    if (string.IsNullOrEmpty(target) || target == _currentTarget) return;

    // Сохранить текущее состояние, переключиться на новый таргет
    if (!string.IsNullOrEmpty(_currentTarget))
        SaveToDisk();

    _currentTarget = target;
    _savePath = Path.Combine(_pluginsDir, $"{target}.myplugin.json");
    LoadFromDisk();
}
```

Подписка на событие для автоматического переключения:

```csharp
api.OnBreakStateEntered += () =>
    Application.Current.Dispatcher.BeginInvoke(() => UpdateTarget());
```

### 15.3. Ребазирование адресов

При ASLR адреса модулей меняются при каждом запуске. Для корректного восстановления сохраненных адресов необходимо ребазирование:

```csharp
// При сохранении: запомнить базовые адреса модулей
var modules = _api.Symbols.GetModules();
foreach (var mod in modules)
    savedData.Modules.Add(new ModuleEntry
    {
        Name = mod.Name,
        BaseAddress = mod.BaseAddress,
        Size = mod.Size
    });

// При загрузке: вычислить дельту для каждого модуля
var currentModules = _api.Symbols.GetModules();
var rebaseMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
foreach (var saved in savedData.Modules)
{
    var cur = currentModules.FirstOrDefault(m =>
        m.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));
    if (cur != null)
        rebaseMap[saved.Name] = (long)cur.BaseAddress - (long)saved.BaseAddress;
}

// Применение ребазирования
static ulong Rebase(ulong addr, List<ModuleEntry> savedModules,
                     Dictionary<string, long> rebaseMap)
{
    foreach (var mod in savedModules)
    {
        if (addr >= mod.BaseAddress && addr < mod.BaseAddress + mod.Size)
        {
            if (rebaseMap.TryGetValue(mod.Name, out long delta))
                return (ulong)((long)addr + delta);
            break;
        }
    }
    return addr; // Модуль не найден -- вернуть оригинальный адрес
}
```

### 15.4. Полная сессия (SessionPlugin)

Плагин SessionPlugin демонстрирует сохранение/восстановление полного состояния отладочной сессии:

**Что сохраняется:**
- Точки останова (адреса, типы)
- Аннотации/комментарии (адреса, текст)
- Пользовательские функции (адреса, имена, размеры)
- Цвета блоков графа (межплагинные данные)
- Базовые адреса модулей (для ребазирования)

**Формат файла:** `.kfsession` (JSON)

**Восстановление точек останова через ToggleBreakpoint:**
```csharp
// Используем ToggleBreakpoint для полной синхронизации с UI
var existingBps = _api.Breakpoints.GetAll();
foreach (var bp in data.Breakpoints)
{
    ulong addr = Rebase(bp.Address, data.Modules, rebaseMap);
    if (existingBps.Any(b => b.Address == addr)) continue; // Пропустить дубли
    _api.Breakpoints.ToggleBreakpoint(addr, (PluginBreakpointType)bp.Type);
}

// Восстановление функций с параметром size
foreach (var f in data.Functions)
{
    ulong addr = Rebase(f.Address, data.Modules, rebaseMap);
    _api.Symbols.RegisterFunction(addr, f.Name, f.Size); // size КРИТИЧЕН!
}
```

---

## 16. Полные примеры плагинов

### 16.1. Простой плагин: только меню

Минимальный плагин, добавляющий два пункта меню для быстрой навигации и обхода антиотладки.

```csharp
using KernelFlirt.SDK;

namespace QuickNavPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Quick Nav";
    public string Description => "Быстрая навигация к точке входа и обход антиотладки";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;

        // Пункт меню: перейти к точке входа
        api.UI.AddMenuItem("Go to _Entry Point", GoToEntryPoint);

        // Пункт меню: скрыть отладчик
        api.UI.AddMenuItem("_Hide Debugger", HideDebugger);

        // Пункт меню: показать информацию
        api.UI.AddMenuItem("Show _Info", ShowInfo);

        api.Log.Info("[QuickNav] Плагин загружен");
    }

    public void Shutdown() { }

    private void GoToEntryPoint()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Процесс должен быть остановлен");
            return;
        }

        var modules = _api.Symbols.GetModules();
        if (modules.Count == 0)
        {
            _api.Log.Warning("Модули не загружены");
            return;
        }

        // Первый модуль = главный EXE
        var mainModule = modules[0];

        // Прочитать AddressOfEntryPoint из PE-заголовка
        // PE signature offset = [baseAddress + 0x3C] (4 байта)
        byte[]? peOffData = _api.Memory.ReadMemory(
            _api.TargetPid, mainModule.BaseAddress + 0x3C, 4);
        if (peOffData == null) return;
        uint peOffset = BitConverter.ToUInt32(peOffData);

        // AddressOfEntryPoint = PE + 0x28 (4 байта)
        byte[]? epData = _api.Memory.ReadMemory(
            _api.TargetPid, mainModule.BaseAddress + peOffset + 0x28, 4);
        if (epData == null) return;
        uint epRva = BitConverter.ToUInt32(epData);
        ulong epVa = mainModule.BaseAddress + epRva;

        _api.UI.NavigateDisassembly(epVa);
        _api.Log.Info($"[QuickNav] Entry Point: 0x{epVa:X16}");
    }

    private void HideDebugger()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Процесс должен быть остановлен");
            return;
        }

        uint pid = _api.TargetPid;

        _api.Process.ClearDebugPort(pid);
        _api.Process.ClearThreadHide(pid);
        _api.Process.SetSpoofSharedUserData(true);

        // Обнулить PEB.BeingDebugged
        var (peb, _) = _api.Process.GetPebAddress(pid);
        if (peb != 0)
        {
            _api.Memory.WriteMemory(pid, peb + 2, new byte[] { 0 }); // BeingDebugged
            _api.Memory.WriteMemory(pid, peb + 0xBC,
                BitConverter.GetBytes(0u)); // NtGlobalFlag
        }

        _api.Log.Info("[QuickNav] Отладчик скрыт");
    }

    private void ShowInfo()
    {
        _api.Log.Info("=== Информация об отладке ===");
        _api.Log.Info($"  Подключено: {_api.IsConnected}");
        _api.Log.Info($"  Break state: {_api.IsBreakState}");
        _api.Log.Info($"  PID: {_api.TargetPid}");
        _api.Log.Info($"  TID: {_api.SelectedThreadId}");
        _api.Log.Info($"  Модулей: {_api.Symbols.GetModules().Count}");
        _api.Log.Info($"  K-модулей: {_api.Symbols.GetKernelModules().Count}");
        _api.Log.Info($"  Точек останова: {_api.Breakpoints.GetAll().Count}");
    }
}
```

### 16.2. Средний плагин: панель с DataGrid

Плагин для отображения строк в памяти процесса с панелью DataGrid.

```csharp
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace StringFinderPlugin;

public class FoundString
{
    public ulong Address { get; set; }
    public string Text { get; set; } = "";
    public string Type { get; set; } = ""; // "ASCII" или "Unicode"

    public string AddressHex => $"{Address:X16}";
}

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "String Finder";
    public string Description => "Поиск строк в памяти процесса";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private DataGrid _grid = null!;
    private TextBox _searchBox = null!;
    private TextBlock _statusText = null!;
    private readonly List<FoundString> _results = [];

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        BuildUi();
        api.UI.AddMenuItem("Find _Strings...", () =>
            Application.Current.Dispatcher.BeginInvoke(() => _searchBox.Focus()));
    }

    public void Shutdown() { }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Тулбар: поле поиска и кнопка
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4)
        };

        _searchBox = new TextBox
        {
            Width = 200,
            Margin = new Thickness(0, 0, 4, 0)
        };
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoSearch(); };
        toolbar.Children.Add(new TextBlock
        {
            Text = "Поиск: ",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        toolbar.Children.Add(_searchBox);

        var searchBtn = new Button
        {
            Content = "Найти",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0)
        };
        searchBtn.Click += (_, _) => DoSearch();
        toolbar.Children.Add(searchBtn);

        var clearBtn = new Button
        {
            Content = "Очистить",
            Padding = new Thickness(8, 2, 8, 2)
        };
        clearBtn.Click += (_, _) => { _results.Clear(); RefreshGrid(); };
        toolbar.Children.Add(clearBtn);

        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(
                Color.FromArgb(20, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Адрес",
            Binding = new System.Windows.Data.Binding("AddressHex"),
            Width = 150
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Тип",
            Binding = new System.Windows.Data.Binding("Type"),
            Width = 70
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Строка",
            Binding = new System.Windows.Data.Binding("Text"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is FoundString fs)
                _api.UI.NavigateDisassembly(fs.Address);
        };

        // Контекстное меню
        var ctx = new ContextMenu();
        var goItem = new MenuItem { Header = "Перейти в дизассемблер" };
        goItem.Click += (_, _) =>
        {
            if (_grid.SelectedItem is FoundString fs)
                _api.UI.NavigateDisassembly(fs.Address);
        };
        ctx.Items.Add(goItem);

        var copyItem = new MenuItem { Header = "Копировать адрес" };
        copyItem.Click += (_, _) =>
        {
            if (_grid.SelectedItem is FoundString fs)
                Clipboard.SetText(fs.AddressHex);
        };
        ctx.Items.Add(copyItem);
        _grid.ContextMenu = ctx;

        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        // Статус
        _statusText = new TextBlock
        {
            Margin = new Thickness(4),
            Foreground = Brushes.Gray,
            FontSize = 11,
            Text = "Готов"
        };
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        _api.UI.AddToolPanel("Strings", root);
    }

    private void DoSearch()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Процесс должен быть остановлен");
            return;
        }

        string query = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _results.Clear();
        _statusText.Text = "Поиск...";

        // Поиск в фоновом потоке
        uint pid = _api.TargetPid;
        var modules = _api.Symbols.GetModules();

        Task.Run(() =>
        {
            byte[] asciiPattern = Encoding.ASCII.GetBytes(query);
            byte[] unicodePattern = Encoding.Unicode.GetBytes(query);
            var found = new List<FoundString>();

            foreach (var mod in modules)
            {
                // Сканируем модуль блоками по 64 КБ
                for (ulong offset = 0; offset < mod.Size; offset += 0x10000)
                {
                    uint blockSize = (uint)Math.Min(0x10000, mod.Size - offset);
                    byte[]? data = _api.Memory.ReadMemory(
                        pid, mod.BaseAddress + offset, blockSize);
                    if (data == null) continue;

                    // Поиск ASCII
                    SearchPattern(data, asciiPattern, mod.BaseAddress + offset,
                        "ASCII", query, found);

                    // Поиск Unicode
                    SearchPattern(data, unicodePattern, mod.BaseAddress + offset,
                        "Unicode", query, found);
                }
            }

            // Обновить UI
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _results.AddRange(found);
                RefreshGrid();
                _statusText.Text = $"Найдено: {_results.Count}";
                _api.Log.Info(
                    $"[StringFinder] Найдено {_results.Count} строк для '{query}'");
            });
        });
    }

    private static void SearchPattern(byte[] data, byte[] pattern,
        ulong baseAddr, string type, string text, List<FoundString> results)
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
                results.Add(new FoundString
                {
                    Address = baseAddr + (ulong)i,
                    Text = text,
                    Type = type
                });
            }
        }
    }

    private void RefreshGrid()
    {
        _grid.ItemsSource = null;
        _grid.ItemsSource = _results;
    }
}
```

### 16.3. Сложный плагин: фоновое сканирование памяти

Плагин для автоматического поиска паттернов в памяти с прогрессом и отменой.

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KernelFlirt.SDK;

namespace PatternScanPlugin;

public class PatternMatch
{
    public ulong Address { get; set; }
    public string Module { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Context { get; set; } = ""; // Байты вокруг совпадения

    public string AddressHex => $"{Address:X16}";
}

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Pattern Scanner";
    public string Description => "Фоновое сканирование памяти по паттернам (IDA-style)";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private DataGrid _grid = null!;
    private TextBox _patternBox = null!;
    private ProgressBar _progress = null!;
    private TextBlock _statusText = null!;
    private Button _scanBtn = null!;
    private Button _stopBtn = null!;
    private CancellationTokenSource? _cts;
    private readonly List<PatternMatch> _matches = [];

    public void Initialize(IDebuggerApi api)
    {
        _api = api;
        BuildUi();
        api.Log.Info("[PatternScan] Плагин загружен. Паттерн: \"48 8B ?? 48 85 C0\" (? = wildcard)");
    }

    public void Shutdown()
    {
        _cts?.Cancel();
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Строка ввода паттерна
        var inputPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4)
        };
        inputPanel.Children.Add(new TextBlock
        {
            Text = "Паттерн:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        _patternBox = new TextBox
        {
            Width = 300,
            Text = "48 8B ?? 48 85 C0",
            Margin = new Thickness(0, 0, 4, 0),
            FontFamily = new FontFamily("Consolas")
        };
        inputPanel.Children.Add(_patternBox);

        _scanBtn = new Button
        {
            Content = "Сканировать",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0)
        };
        _scanBtn.Click += (_, _) => StartScan();
        inputPanel.Children.Add(_scanBtn);

        _stopBtn = new Button
        {
            Content = "Стоп",
            Padding = new Thickness(8, 2, 8, 2),
            IsEnabled = false
        };
        _stopBtn.Click += (_, _) => StopScan();
        inputPanel.Children.Add(_stopBtn);

        Grid.SetRow(inputPanel, 0);
        root.Children.Add(inputPanel);

        // Прогресс
        _progress = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(4, 0, 4, 4),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_progress, 1);
        root.Children.Add(_progress);

        // DataGrid
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = new SolidColorBrush(
                Color.FromArgb(20, 255, 255, 255)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Адрес",
            Binding = new System.Windows.Data.Binding("AddressHex"),
            Width = 150
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Модуль",
            Binding = new System.Windows.Data.Binding("Module"),
            Width = 120
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Контекст",
            Binding = new System.Windows.Data.Binding("Context"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is PatternMatch m)
                _api.UI.NavigateDisassembly(m.Address);
        };

        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        // Статус
        _statusText = new TextBlock
        {
            Text = "Готов",
            Margin = new Thickness(4),
            Foreground = Brushes.Gray,
            FontSize = 11
        };
        Grid.SetRow(_statusText, 3);
        root.Children.Add(_statusText);

        _api.UI.AddToolPanel("Pattern Scan", root);
    }

    private void StartScan()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("Процесс должен быть остановлен");
            return;
        }

        string patternStr = _patternBox.Text.Trim();
        if (string.IsNullOrEmpty(patternStr)) return;

        // Парсинг паттерна (поддержка wildcards: ??, ?)
        var (pattern, mask) = ParsePattern(patternStr);
        if (pattern.Length == 0) return;

        _matches.Clear();
        RefreshGrid();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _scanBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _progress.Visibility = Visibility.Visible;

        uint pid = _api.TargetPid;
        var modules = _api.Symbols.GetModules();
        ulong totalSize = (ulong)modules.Sum(m => (long)m.Size);

        Task.Run(() =>
        {
            ulong scanned = 0;
            var found = new List<PatternMatch>();

            foreach (var mod in modules)
            {
                if (token.IsCancellationRequested) break;

                for (ulong offset = 0; offset < mod.Size; offset += 0x10000)
                {
                    if (token.IsCancellationRequested) break;

                    uint blockSize = (uint)Math.Min(0x10000, mod.Size - offset);
                    byte[]? data = _api.Memory.ReadMemory(
                        pid, mod.BaseAddress + offset, blockSize);
                    scanned += blockSize;

                    if (data != null)
                    {
                        for (int i = 0; i <= data.Length - pattern.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < pattern.Length; j++)
                            {
                                if (mask[j] && data[i + j] != pattern[j])
                                {
                                    match = false;
                                    break;
                                }
                            }
                            if (match)
                            {
                                // Извлечь контекст (16 байт)
                                int ctxStart = Math.Max(0, i - 4);
                                int ctxLen = Math.Min(data.Length - ctxStart, 24);
                                string ctx = BitConverter.ToString(
                                    data, ctxStart, ctxLen).Replace("-", " ");

                                found.Add(new PatternMatch
                                {
                                    Address = mod.BaseAddress + offset + (ulong)i,
                                    Module = mod.Name,
                                    Pattern = patternStr,
                                    Context = ctx
                                });
                            }
                        }
                    }

                    // Обновить прогресс
                    double pct = totalSize > 0
                        ? (double)scanned / totalSize * 100
                        : 0;
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        _progress.Value = pct;
                        _statusText.Text =
                            $"Сканирование... {pct:F0}% ({found.Count} найдено)";
                    });
                }
            }

            // Готово
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _matches.AddRange(found);
                RefreshGrid();
                _scanBtn.IsEnabled = true;
                _stopBtn.IsEnabled = false;
                _progress.Visibility = Visibility.Collapsed;
                _statusText.Text = token.IsCancellationRequested
                    ? $"Прервано. Найдено: {found.Count}"
                    : $"Завершено. Найдено: {found.Count}";
                _api.Log.Info(
                    $"[PatternScan] Найдено {found.Count} совпадений для '{patternStr}'");
            });
        }, token);
    }

    private void StopScan()
    {
        _cts?.Cancel();
    }

    private static (byte[] pattern, bool[] mask) ParsePattern(string str)
    {
        var parts = str.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pattern = new byte[parts.Length];
        var mask = new bool[parts.Length]; // true = exact match, false = wildcard

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "?" || parts[i] == "??")
            {
                pattern[i] = 0;
                mask[i] = false; // wildcard
            }
            else if (byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber,
                         null, out byte b))
            {
                pattern[i] = b;
                mask[i] = true; // exact
            }
        }
        return (pattern, mask);
    }

    private void RefreshGrid()
    {
        _grid.ItemsSource = null;
        _grid.ItemsSource = _matches;
    }
}
```

### 16.4. Плагин-распаковщик: OnDebugEventFilter

Автоматический распаковщик с определением OEP (Original Entry Point) на основе `OnDebugEventFilter`.

```csharp
using System.Windows;
using System.Windows.Controls;
using KernelFlirt.SDK;

namespace AutoUnpackerPlugin;

public class Plugin : IKernelFlirtPlugin
{
    public string Name => "Auto Unpacker";
    public string Description => "Автоматическое определение OEP для упакованных бинарников";
    public string Version => "1.0";

    private IDebuggerApi _api = null!;
    private bool _active;
    private ulong _textBase;
    private ulong _textEnd;
    private ulong _packerBase;
    private ulong _packerEnd;
    private int _stepCount;
    private TextBlock _statusLabel = null!;

    public void Initialize(IDebuggerApi api)
    {
        _api = api;

        // Подписаться на фильтр событий
        api.OnDebugEventFilter += OnFilter;

        // Построить простой UI
        var panel = new StackPanel { Margin = new Thickness(8) };

        _statusLabel = new TextBlock
        {
            Text = "Статус: Неактивен",
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(_statusLabel);

        var startBtn = new Button
        {
            Content = "Запустить автораспаковку",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 4),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        startBtn.Click += (_, _) => StartUnpacking();
        panel.Children.Add(startBtn);

        var stopBtn = new Button
        {
            Content = "Остановить",
            Padding = new Thickness(8, 4, 8, 4),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        stopBtn.Click += (_, _) => StopUnpacking();
        panel.Children.Add(stopBtn);

        api.UI.AddToolPanel("Unpacker", panel);
    }

    public void Shutdown() { }

    private void StartUnpacking()
    {
        if (!_api.IsBreakState)
        {
            _api.Log.Warning("[Unpacker] Процесс должен быть остановлен");
            return;
        }

        var modules = _api.Symbols.GetModules();
        if (modules.Count == 0) return;

        var mainModule = modules[0];

        // Определить секцию .text (обычно первая секция кода)
        // Для упрощения: считаем, что .text начинается с base + 0x1000
        _textBase = mainModule.BaseAddress + 0x1000;
        _textEnd = _textBase + 0x50000; // Примерный размер

        // Регион упаковщика (обычно последняя секция)
        _packerBase = mainModule.BaseAddress + mainModule.Size - 0x10000;
        _packerEnd = mainModule.BaseAddress + mainModule.Size;

        _active = true;
        _stepCount = 0;

        // Установить memory breakpoint на секцию .text
        // Когда упаковщик распакует код и передаст управление,
        // RIP окажется в .text -- это OEP
        _api.Memory.ProtectMemory(_api.TargetPid, _textBase,
            (uint)(_textEnd - _textBase), 0x01); // PAGE_NOACCESS

        Application.Current.Dispatcher.BeginInvoke(() =>
            _statusLabel.Text = "Статус: Активен, ожидание OEP...");

        _api.Log.Info("[Unpacker] Запущен. Guard page на .text установлен. " +
                     "Нажмите F9 для продолжения.");
    }

    private void StopUnpacking()
    {
        _active = false;
        Application.Current.Dispatcher.BeginInvoke(() =>
            _statusLabel.Text = "Статус: Остановлен");
        _api.Log.Info("[Unpacker] Остановлен");
    }

    private bool _rearmGuard;

    private bool OnFilter(PluginDebugEvent evt)
    {
        if (!_active) return false;

        if (evt.Type == PluginDebugEventType.AccessViolation)
        {
            ulong fault = evt.FaultAddress;

            // Доступ к .text -- возможно, упаковщик распаковывает код
            if (fault >= _textBase && fault < _textEnd)
            {
                // Если это EXECUTE -- это OEP!
                if (evt.AccessType == 8)
                {
                    _active = false;
                    ulong oep = evt.Address;

                    // Восстановить защиту
                    _api.Memory.ProtectMemory(_api.TargetPid, _textBase,
                        (uint)(_textEnd - _textBase), 0x20); // PAGE_EXECUTE_READ

                    _api.Log.Info($"[Unpacker] OEP НАЙДЕН: 0x{oep:X16}");
                    _api.Symbols.RegisterFunction(oep, "OEP", size: 0x100);

                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        _statusLabel.Text = $"OEP: 0x{oep:X16}";
                        _api.UI.NavigateDisassembly(oep);
                        _api.UI.SetAddressAnnotation(oep, "=== OEP (Original Entry Point) ===");
                        _api.UI.RefreshDisassembly();
                    });

                    return false; // Остановить в UI -- пользователь увидит OEP
                }

                // Доступ READ/WRITE -- упаковщик записывает распакованные данные
                _api.Memory.ProtectMemory(_api.TargetPid, _textBase,
                    (uint)(_textEnd - _textBase), 0x04); // PAGE_READWRITE
                evt.ContinueMode = 3; // Handled + TF
                _rearmGuard = true;
                _stepCount++;
                return true;
            }
        }

        if (evt.Type == PluginDebugEventType.SingleStep && _rearmGuard)
        {
            // Восстановить guard page после шага
            _api.Memory.ProtectMemory(_api.TargetPid, _textBase,
                (uint)(_textEnd - _textBase), 0x01); // PAGE_NOACCESS
            _rearmGuard = false;

            if (_stepCount % 1000 == 0)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                    _statusLabel.Text = $"Статус: {_stepCount} доступов к .text...");
            }

            return true;
        }

        return false;
    }
}
```

---

## 17. Best practices и частые ошибки

### 17.1. Обязательные правила

1. **Всегда проверяйте IsBreakState** перед чтением памяти и регистров:
   ```csharp
   if (!_api.IsBreakState) return;
   ```

2. **Всегда проверяйте возвращаемое значение ReadMemory на null:**
   ```csharp
   byte[]? data = _api.Memory.ReadMemory(pid, addr, size);
   if (data == null) { _api.Log.Error("Чтение не удалось"); return; }
   ```

3. **Используйте Dispatcher для обновления UI из фоновых потоков:**
   ```csharp
   Application.Current.Dispatcher.BeginInvoke(() => RefreshGrid());
   ```

4. **В OnDebugEventFilter -- всегда возвращайте false по умолчанию:**
   ```csharp
   private bool OnFilter(PluginDebugEvent evt)
   {
       // Обработка...
       return false; // Пусть UI обрабатывает
   }
   ```

5. **При RegisterFunction всегда указывайте size:**
   ```csharp
   _api.Symbols.RegisterFunction(addr, "MyFunc", size: 0x120); // ПРАВИЛЬНО
   _api.Symbols.RegisterFunction(addr, "MyFunc");               // НЕПРАВИЛЬНО (size=0)
   ```

6. **Используйте ToggleBreakpoint для восстановления BP из файлов:**
   ```csharp
   _api.Breakpoints.ToggleBreakpoint(addr); // Обновляет UI + driver
   ```

7. **Сохраняйте данные в Shutdown():**
   ```csharp
   public void Shutdown() { SaveToDisk(); }
   ```

### 17.2. Частые ошибки

#### Ошибка 1: Обращение к WPF из фонового потока

```csharp
// НЕПРАВИЛЬНО -- крэш!
api.OnDebugEventFilter += (evt) =>
{
    _statusLabel.Text = "Событие!"; // WPF из фонового потока
    return false;
};

// ПРАВИЛЬНО
api.OnDebugEventFilter += (evt) =>
{
    Application.Current.Dispatcher.BeginInvoke(() =>
        _statusLabel.Text = "Событие!");
    return false;
};
```

#### Ошибка 2: Забыли вернуть true/установить ContinueMode в фильтре

```csharp
// НЕПРАВИЛЬНО -- процесс зависнет!
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint)
    {
        _api.Log.Info("BP hit!");
        return true; // Подавили UI, но не указали ContinueMode и не вызвали Continue()!
    }
    return false;
}

// ПРАВИЛЬНО
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint)
    {
        _api.Log.Info("BP hit!");
        evt.ContinueMode = 1; // StepPast -- шагнуть через INT3 и продолжить
        return true;
    }
    return false;
}
```

#### Ошибка 3: ReadMemory без проверки break state

```csharp
// НЕПРАВИЛЬНО -- вернет null, если процесс работает
var data = _api.Memory.ReadMemory(pid, addr, 8);
ulong val = BitConverter.ToUInt64(data); // NullReferenceException!

// ПРАВИЛЬНО
if (!_api.IsBreakState) return;
var data = _api.Memory.ReadMemory(pid, addr, 8);
if (data == null) return;
ulong val = BitConverter.ToUInt64(data);
```

#### Ошибка 4: Private=true в ProjectReference

```xml
<!-- НЕПРАВИЛЬНО -- SDK DLL скопируется в output, конфликт версий -->
<ProjectReference Include="..\..\src\sdk\KernelFlirt.SDK.csproj" />

<!-- ПРАВИЛЬНО -->
<ProjectReference Include="..\..\src\sdk\KernelFlirt.SDK.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

#### Ошибка 5: Долгая операция в OnDebugEventFilter

```csharp
// НЕПРАВИЛЬНО -- блокирует обработку событий
private bool OnFilter(PluginDebugEvent evt)
{
    Thread.Sleep(1000); // Блокировка!
    var data = _api.Memory.ReadMemory(pid, 0, 0x1000000); // 16 МБ чтение
    // ... долгий анализ ...
    return false;
}

// ПРАВИЛЬНО -- минимальная работа в фильтре
private bool OnFilter(PluginDebugEvent evt)
{
    if (evt.Type == PluginDebugEventType.Breakpoint && evt.Address == _targetAddr)
    {
        // Быстро сохранить данные
        _lastHitAddress = evt.Address;
        _lastHitTid = evt.ThreadId;
        // Тяжелый анализ -- в UI потоке, после остановки
        return false;
    }
    return false;
}
```

#### Ошибка 6: Не очищают аннотации при удалении закладок

```csharp
// НЕПРАВИЛЬНО -- аннотация останется в дизассемблере
_bookmarks.Remove(bm);

// ПРАВИЛЬНО
_api.UI.SetAddressAnnotation(bm.Address, null);
_bookmarks.Remove(bm);
_api.UI.RefreshDisassembly();
```

#### Ошибка 7: InstallNtQsiHook без таймера удаления

```csharp
// НЕПРАВИЛЬНО -- PatchGuard BSOD через 5-10 минут!
_api.Process.InstallNtQsiHook();
// ... забыли удалить ...

// ПРАВИЛЬНО
_api.Process.InstallNtQsiHook();
// Удалить до PatchGuard
Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ =>
{
    _api.Process.RemoveNtQsiHook();
    _api.Log.Info("NtQSI hook удален до PatchGuard");
});
```

### 17.3. Рекомендации по производительности

1. **Читайте память блоками** (4-64 КБ), а не побайтово
2. **Кешируйте результаты** `ReadRegisters` и `GetModules` внутри одной остановки
3. **Используйте `BeginInvoke` вместо `Invoke`** для неблокирующего обновления UI
4. **Ограничивайте частоту обновления прогресса** в фоновых задачах (не каждый байт)
5. **Отменяйте фоновые задачи** через `CancellationToken` в `Shutdown()`

### 17.4. Чек-лист перед релизом

- [ ] `.csproj` содержит `EnableDynamicLoading`, `UseWPF`, `Private=false`
- [ ] `Initialize()` сохраняет ссылку на `_api`
- [ ] `Shutdown()` сохраняет состояние и отменяет фоновые задачи
- [ ] Все обращения к памяти проверяют `IsBreakState`
- [ ] Все вызовы `ReadMemory` проверяют на `null`
- [ ] UI обновляется через Dispatcher из фоновых потоков
- [ ] `OnDebugEventFilter` возвращает `false` по умолчанию
- [ ] `RegisterFunction` вызывается с параметром `size`
- [ ] Нет жестких светлых цветов (белый фон и т.д.)
- [ ] Моноширинный шрифт для адресов и hex-данных
- [ ] Аннотации очищаются при удалении данных
- [ ] NtQsiHook удаляется вовремя

---

## 18. Справочник перечислений и констант

### 18.1. Константы защиты памяти

Используются в `ProtectMemory` (параметр `newProtection`):

| Константа | Значение | Описание |
|-----------|----------|----------|
| `PAGE_NOACCESS` | `0x01` | Нет доступа. Любое обращение вызывает AV |
| `PAGE_READONLY` | `0x02` | Только чтение |
| `PAGE_READWRITE` | `0x04` | Чтение и запись |
| `PAGE_WRITECOPY` | `0x08` | Copy-on-write |
| `PAGE_EXECUTE` | `0x10` | Только выполнение |
| `PAGE_EXECUTE_READ` | `0x20` | Выполнение и чтение |
| `PAGE_EXECUTE_READWRITE` | `0x40` | Выполнение, чтение и запись |
| `PAGE_EXECUTE_WRITECOPY` | `0x80` | Выполнение с copy-on-write |
| `PAGE_GUARD` | `0x100` | Guard page (модификатор, комбинируется через OR) |
| `PAGE_NOCACHE` | `0x200` | Без кеширования |

### 18.2. Характеристики секций PE

Используются в `PluginSectionInfo.Characteristics`:

| Константа | Значение | Описание |
|-----------|----------|----------|
| `IMAGE_SCN_CNT_CODE` | `0x00000020` | Секция содержит код |
| `IMAGE_SCN_CNT_INITIALIZED_DATA` | `0x00000040` | Инициализированные данные |
| `IMAGE_SCN_CNT_UNINITIALIZED_DATA` | `0x00000080` | Неинициализированные данные (BSS) |
| `IMAGE_SCN_MEM_EXECUTE` | `0x20000000` | Секция исполняемая |
| `IMAGE_SCN_MEM_READ` | `0x40000000` | Секция читаемая |
| `IMAGE_SCN_MEM_WRITE` | `0x80000000` | Секция записываемая |

**Типичные комбинации:**

| Секция | Характеристики | Описание |
|--------|---------------|----------|
| `.text` | `0x60000020` | Код, чтение+выполнение |
| `.rdata` | `0x40000040` | Данные, только чтение |
| `.data` | `0xC0000040` | Данные, чтение+запись |
| `.rsrc` | `0x40000040` | Ресурсы, только чтение |

### 18.3. Коды исключений Windows

Встречаются в `PluginDebugEvent.ExceptionCode`:

| Код | Значение | Описание |
|-----|----------|----------|
| `STATUS_BREAKPOINT` | `0x80000003` | Программная точка останова (INT3) |
| `STATUS_SINGLE_STEP` | `0x80000004` | Одиночный шаг (Trap Flag) |
| `STATUS_ACCESS_VIOLATION` | `0xC0000005` | Нарушение доступа (page fault) |
| `STATUS_GUARD_PAGE_VIOLATION` | `0x80000001` | Guard page |
| `STATUS_ILLEGAL_INSTRUCTION` | `0xC000001D` | Недопустимая инструкция |
| `STATUS_INTEGER_DIVIDE_BY_ZERO` | `0xC0000094` | Деление на ноль |
| `STATUS_STACK_OVERFLOW` | `0xC00000FD` | Переполнение стека |
| `STATUS_PRIVILEGED_INSTRUCTION` | `0xC0000096` | Привилегированная инструкция |

### 18.4. Имена регистров

Регистры, возвращаемые `ReadRegisters`:

**Регистры общего назначения (IsFlag = false):**
`RAX`, `RBX`, `RCX`, `RDX`, `RSI`, `RDI`, `RBP`, `RSP`, `R8`, `R9`, `R10`, `R11`, `R12`, `R13`, `R14`, `R15`

**Указатель инструкций:**
`RIP`

**Флаговый регистр:**
`RFLAGS`

**Отладочные регистры:**
`DR0`, `DR1`, `DR2`, `DR3`, `DR6`, `DR7`

**Сегментные регистры:**
`CS`, `DS`, `ES`, `FS`, `GS`, `SS`

**Индивидуальные флаги (IsFlag = true):**

| Флаг | Описание |
|------|----------|
| `CF` | Carry Flag -- флаг переноса |
| `PF` | Parity Flag -- флаг четности |
| `AF` | Auxiliary Flag -- вспомогательный флаг |
| `ZF` | Zero Flag -- флаг нуля |
| `SF` | Sign Flag -- флаг знака |
| `TF` | Trap Flag -- флаг трассировки |
| `IF` | Interrupt Flag -- флаг прерываний |
| `DF` | Direction Flag -- флаг направления |
| `OF` | Overflow Flag -- флаг переполнения |

**x64 Calling Convention (Microsoft):**
- Параметры (целые/указатели): `RCX`, `RDX`, `R8`, `R9`, далее на стеке
- Возвращаемое значение: `RAX`
- Volatile (можно менять): `RAX`, `RCX`, `RDX`, `R8`-`R11`
- Non-volatile (нужно сохранять): `RBX`, `RBP`, `RDI`, `RSI`, `R12`-`R15`
- Указатель стека: `RSP` (должен быть выровнен на 16 байт перед CALL)

---

*Документация KernelFlirt SDK v2.0. Последнее обновление: 2026-04-08.*
