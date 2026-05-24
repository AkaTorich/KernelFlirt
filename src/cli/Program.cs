// KfConsole — консольный фронтенд для KernelFlirt-драйвера.
//
// Подключается локально (\\.\KernelFlirt) или к удалённому релею по TCP,
// предоставляет REPL в стиле WinDbg/x64dbg с базовыми командами отладки.
//
// Использование:
//   KfConsole.exe                    — REPL без авто-подключения
//   KfConsole.exe local              — авто-подключение к локальному драйверу
//   KfConsole.exe <host>             — авто-подключение к релею (порт 31337)
//   KfConsole.exe <host>:<port>      — авто-подключение к релею на указанный порт
using System.Globalization;

namespace KernelFlirt.Cli;

internal static class Program
{
    private static readonly KfClient Client = new();
    private static readonly Session Sess = new();
    private static readonly SymbolService Syms = new();
    private static readonly ExprEvaluator Evaluator;
    private static volatile bool _running = true;
    private static volatile bool _eventListenerRunning;
    private static Thread? _eventListener;

    static Program()
    {
        Evaluator = new ExprEvaluator(
            client: Client, syms: Syms,
            getRegs: () => { lock (Sess.Lock) return Sess.LastRegs; },
            getPid: () => { lock (Sess.Lock) return Sess.TargetPid; },
            is32Bit: () => { lock (Sess.Lock) return Sess.Is32Bit; });
    }

    // Главное состояние сессии — общее между основным потоком и слушателем
    // событий. Менять только под Sess._lock.
#pragma warning disable CS0649  // Is32Bit зарезервирован для WoW64 — присваивается из ENUM_PROCESSES когда будет реализован
    internal sealed class Session
    {
        public readonly object Lock = new();
        public uint TargetPid;
        public uint CurrentTid;
        public bool IsBreak;
        public bool Is32Bit;   // Резерв под WoW64-target — пока всегда false (x64-only).
        public bool IsPausedViaSuspend;  // TRUE после `open` (entry-point): поток в SUSPENDED,
                                         // не в KfReportAndBlock; шаг = SingleStep + Resume + Wait.
        public ulong LastRip;
        public ulong LastRsp;
        public KF_REGISTERS? LastRegs;   // снимок для парсера выражений
        public readonly List<BpRec> Breakpoints = new();
    }

    /// <summary>Запись о точке останова. IsTemp=true — авто-снимается после события.
    /// Condition — выражение, которое должно быть != 0 чтобы событие было показано.</summary>
    internal sealed class BpRec
    {
        public uint    Handle;
        public ulong   Addr;
        public bool    IsTemp;
        public string? Condition;
        public string  Kind;     // "sw" | "hw-e" | "hw-w" | "hw-rw" | "mem"
        public BpRec(uint h, ulong a, bool temp = false, string? cond = null, string kind = "sw")
        { Handle = h; Addr = a; IsTemp = temp; Condition = cond; Kind = kind; }
    }
#pragma warning restore CS0649

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { Console.InputEncoding = new System.Text.UTF8Encoding(false); } catch { }
        Ansi.EnableVtOnLegacyConsole();
        Ansi.Enabled = true;   // всегда цвет, отключить можно командой `color off`
        Syms.Initialize();  // dbghelp init с учётом _NT_SYMBOL_PATH
        Syms.AttachClient(Client);  // для ReadRsdsFromTarget
        Syms.LogMessage = msg => Console.WriteLine(msg);  // диагностика загрузки PDB
        Console.WriteLine(Ansi.Wrap(Ansi.Magenta, "KernelFlirt console ") + Ansi.Wrap(Ansi.Yellow, "v2.1"));
        Console.WriteLine(Ansi.Wrap(Ansi.Dim, "type 'help' for commands, 'q' to quit"));

        if (args.Length > 0)
            TryAutoConnect(args[0]);

        RunRepl();
        StopEventListener();
        Client.Dispose();
        return 0;
    }

    private static void TryAutoConnect(string spec)
    {
        if (spec.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            if (Client.ConnectLocal()) Print($"connected (local driver)");
            else Print("connect failed (admin? driver loaded?)");
            return;
        }
        var parts = spec.Split(':');
        string host = parts[0];
        int port = parts.Length > 1 ? int.Parse(parts[1]) : 31337;
        if (Client.ConnectRemote(host, port)) Print($"connected ({host}:{port})");
        else Print($"connect failed ({host}:{port})");
    }

    // ── REPL ──────────────────────────────────────────────────────────────

    private static void RunRepl()
    {
        // В pipe-режиме (stdin redirected) ReadLine.Reboot ведёт себя плохо —
        // используем голый Console.ReadLine. История и автодополнение нужны
        // только в интерактивной сессии.
        if (Console.IsInputRedirected)
        {
            while (_running)
            {
                Console.Write(Prompt());
                var line = Console.ReadLine();
                if (line == null) break;
                line = line.TrimStart('﻿').Trim();
                if (line.Length == 0) continue;
                try { Dispatch(line); }
                catch (Exception ex) { Print($"error: {ex.Message}"); }
            }
            return;
        }

        // История команд в %APPDATA%\KernelFlirt\history.txt чтобы между сессиями.
        try
        {
            var histPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KernelFlirt", "history.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(histPath)!);
            if (File.Exists(histPath))
                ReadLineReboot.ReadLine.AddHistory(File.ReadAllLines(histPath));
            // Автодополнение по словарю всех зарегистрированных команд.
            ReadLineReboot.ReadLine.AutoCompletionHandler = new SimpleCompleter();
            ReadLineReboot.ReadLine.HistoryEnabled = true;

            while (_running)
            {
                string? line;
                try { line = ReadLineReboot.ReadLine.Read(Prompt()); }
                catch { break; }   // Ctrl+C / EOF
                if (line == null) break;
                line = line.TrimStart('﻿').Trim();
                if (line.Length == 0) continue;
                try { Dispatch(line); }
                catch (Exception ex) { Print($"error: {ex.Message}"); }
            }

            // Сохраним историю.
            try { File.WriteAllLines(histPath, ReadLineReboot.ReadLine.GetHistory()); } catch { }
        }
        catch
        {
            // Fallback на голый Console.ReadLine если ReadLine.Reboot отвалится
            // (например, stdin перенаправлен — пакет может ругаться).
            while (_running)
            {
                Console.Write(Prompt());
                var line = Console.ReadLine();
                if (line == null) break;
                line = line.TrimStart('﻿').Trim();
                if (line.Length == 0) continue;
                try { Dispatch(line); }
                catch (Exception ex) { Print($"error: {ex.Message}"); }
            }
        }
    }

    /// <summary>Tab-completion: предлагает команду из фиксированного списка по префиксу.</summary>
    private sealed class SimpleCompleter : ReadLineReboot.IAutoCompleteHandler
    {
        public char[] Separators { get; set; } = new[] { ' ', '\t' };
        private static readonly string[] Commands =
        {
            "help", "quit", "connect", "disconnect",
            "open", "attach", "detach", "reset",
            "procs", "mods", "kmods", "threads", "tid",
            "r", "d", "dq", "dd", "dw", "dp", "da", "du", "s", "u", "e",
            ".alloc", ".free", ".protect",
            "bp", "ba", "bm", "bl", "bc",
            "g", "t", "p", "o", "ss", "k", "wait", "interrupt", "suspend", "resume",
            "!peb", "stats", "color", "ad",
        };
        public string[] GetSuggestions(string text, int index)
        {
            // Если это первое слово — предлагаем команды; иначе — ничего интересного.
            if (text.IndexOfAny(Separators) >= 0) return Array.Empty<string>();
            return Commands.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    private static string Prompt()
    {
        lock (Sess.Lock)
        {
            if (!Client.IsConnected) return Ansi.Wrap(Ansi.Dim, "kf> ");
            if (Sess.TargetPid == 0)  return Ansi.Wrap(Ansi.Dim, "kf*> ");
            string state = Sess.IsBreak ? Ansi.Wrap(Ansi.Red, "brk") : Ansi.Wrap(Ansi.Green, "run");
            string arch  = Ansi.Wrap(Ansi.Yellow, Sess.Is32Bit ? "x86" : "x64");
            string pid   = Ansi.Wrap(Ansi.Cyan, Sess.TargetPid.ToString());
            string tid   = Ansi.Wrap(Ansi.Cyan, Sess.CurrentTid.ToString());
            return $"{Ansi.Wrap(Ansi.Dim, "kf(")}{pid}:{tid}/{arch}/{state}{Ansi.Wrap(Ansi.Dim, ")> ")}";
        }
    }

    // ── Цветные хелперы для сообщений ────────────────────────────────────
    private static void Ok(string msg)   => Console.WriteLine(Ansi.Wrap(Ansi.Green, "✓ ") + msg);
    private static void Err(string msg)  => Console.WriteLine(Ansi.Wrap(Ansi.Red,   "✗ ") + msg);
    private static void Info(string msg) => Console.WriteLine(Ansi.Wrap(Ansi.Cyan,  "• ") + msg);
    private static string Addr(ulong a)  { bool b; lock(Sess.Lock) b=Sess.Is32Bit; return Ansi.Wrap(Ansi.Gray, FormatAddr(a, b)); }
    private static string Pid(uint p)    => Ansi.Wrap(Ansi.Cyan, p.ToString());
    private static string Num(int n)     => Ansi.Wrap(Ansi.Orange, n.ToString());
    private static string Num(uint n)    => Ansi.Wrap(Ansi.Orange, n.ToString());
    private static string Hex(ulong v)   => Ansi.Wrap(Ansi.Orange, "0x" + v.ToString("X"));
    private static string Yp(string p)   => Ansi.Wrap(Ansi.Yellow, p);   // путь / имя
    private static string Kw(string s)   => Ansi.Wrap(Ansi.Magenta, s);   // ключевые слова операций

    // Адрес: 8-hex для x86, 16-hex для x64, всегда с tick-разделителем как в OllyDbg.
    private static string FormatAddr(ulong v, bool is32Bit)
    {
        if (is32Bit) return $"{(uint)v:X8}";
        string h = v.ToString("X16");
        return h[..8] + "`" + h[8..];
    }
    private static string Fa(ulong v) { bool b; lock (Sess.Lock) b = Sess.Is32Bit; return FormatAddr(v, b); }

    // ── Команды ───────────────────────────────────────────────────────────

    private static void Dispatch(string line)
    {
        var (cmd, rest) = Split(line);
        switch (cmd.ToLowerInvariant())
        {
            case "help": case "?": Help(rest); break;
            case "q": case "quit": case "exit": _running = false; break;

            case "connect": CmdConnect(rest); break;
            case "disconnect": CmdDisconnect(); break;

            case "procs": case "ps": CmdProcs(); break;
            case "mods": CmdMods(); break;
            case "kmods": CmdKmods(); break;
            case "threads": case "tt": CmdThreads(); break;
            case "k": case "stack": CmdStack(rest); break;
            case "suspend": CmdSuspend(rest); break;
            case "resume": CmdResume(rest); break;
            case "!peb": case "peb": CmdPeb(); break;
            case "stats": CmdStats(); break;
            case "open": case "launch": CmdOpen(rest); break;
            case "attach": CmdAttach(rest); break;
            case "detach": CmdDetach(); break;
            case "reset": CmdReset(); break;

            case "r": case "reg": CmdRegisters(rest); break;
            case "d": case "db": CmdDump(rest, byteMode: true); break;
            case "dq": CmdDump(rest, byteMode: false); break;
            case "dd": CmdDumpUnits(rest, unit: 4); break;
            case "dw": CmdDumpUnits(rest, unit: 2); break;
            case "dp": CmdDumpPointers(rest); break;
            case "da": CmdDumpString(rest, wide: false); break;
            case "du": CmdDumpString(rest, wide: true); break;
            case "s": case "search": CmdSearch(rest); break;
            case "u": case "dis": CmdDisasm(rest); break;
            case "e": case "eb": CmdEdit(rest); break;
            case ".alloc": CmdAlloc(rest); break;
            case ".free": CmdFree(rest); break;
            case ".protect": CmdProtect(rest); break;

            case "bp": CmdBp(rest); break;
            case "ba": CmdBpHw(rest); break;
            case "bm": CmdBpMem(rest); break;
            case "bl": CmdBpList(); break;
            case "bc": CmdBpClear(rest); break;

            case "g": case "run": case "go": CmdGo(rest); break;
            case "t": case "sti": CmdStepInto(); break;
            case "p": case "sto": CmdStepOver(); break;
            case "o": case "out": CmdStepOut(); break;
            case "ss": CmdSingleStep(); break;
            case "wait": CmdWait(); break;
            case "interrupt": CmdInterrupt(); break;

            case "tid": CmdSetTid(rest); break;
            case "color": CmdColor(rest); break;
            case "ad": CmdAntiDebug(rest); break;

            default: Print($"unknown command: {cmd} (type 'help')"); break;
        }
    }

    private static void Help(string topic)
    {
        Print(
@"Commands:
  connect [local | host[:port]]   подключение к драйверу или релею
  disconnect                      разрыв подключения

  open <path-to-exe>              запустить процесс под отладкой
  procs                           список процессов
  mods                            список модулей текущего target
  kmods                           список модулей ядра (ntoskrnl + драйверы)
  threads                         список потоков текущего target
  attach <pid>                    взять процесс под отладку
  detach                          отвязаться от текущего процесса
  reset                           драйвер: снять все BP, выгрузить хук

  r                               показать все регистры
  r <name>                        показать регистр
  r <name>=<expr>                 установить регистр
  d <addr> [count=64]             hex dump (байты)
  dq <addr> [count=8]             qword dump (8 байт по 16-сс)
  dd <addr> [count=16]            dword dump
  dw <addr> [count=32]            word dump
  dp <addr> [count=8]             дамп указателей (+символы)
  da <addr> [count=64]            ASCII-строка
  du <addr> [count=64]            UTF-16-строка
  s <addr> <len> <pattern>        поиск (hex c ?? | ""строка"" | L""unicode"")
  u <addr> [count=16]             дизассемблер
  e <addr> <hex bytes...>         запись в память (e 401570 90 90 c3)
  .alloc <size> [prot=rwx]        выделить память в target
  .free <addr>                    освободить память
  .protect <addr> <size> <prot>   сменить защиту (rwx|rw|rx|r|ro|na|hex)

  bp <addr> [if <cond>]           поставить SW BP (INT3)
  ba <e|r|w><len> <addr>          аппаратный BP/watchpoint (DR0-3)
  bm <addr> [size]                memory BP (PAGE_GUARD)
  bl                              список BP
  bc <addr | handle | all>        снять BP

  g [addr]                        продолжить (g <addr> = run to cursor)
  t                               step into (одна инструкция)
  p                               step over (через call/loop)
  o                               step out (до return-адреса)
  ss                              принудительный single step (TF)
  k [frames]                      стек вызовов (по frame-pointer)
  wait                            ждать debug event
  interrupt                       suspend текущий поток
  suspend [tid]                   приостановить поток
  resume [tid]                    возобновить поток

  !peb                            разбор PEB target'а
  stats                           счётчики/адреса inline-хука
  tid <tid>                       выбрать поток (regs/step применяются к нему)
  color [on|off]                  ANSI-подсветка дизасма / hex-dump

  ad clr_debug_port               занулить EPROCESS.DebugPort у target
  ad clr_thread_hide              убрать HideFromDebugger у всех потоков
  ad ntqsi on|off                 hook NtQuerySystemInformation (class 0x23)
  ad spoof on|off                 спуф KUSER_SHARED_DATA.KdDebuggerEnabled

  q                               выход");
    }

    // ── Подключение ───────────────────────────────────────────────────────

    private static void CmdConnect(string arg)
    {
        if (Client.IsConnected) { Info("уже подключено"); return; }
        if (arg.Length == 0 || arg.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            if (Client.ConnectLocal())
            {
                if (Client.Ping(out var v, out var m))
                    Ok($"{Kw("connected")} (local), driver v{Hex(v)} magic={Hex(m)}");
                else
                    Info("connected, но Ping не отвечает");
            }
            else
                Err("CreateFileW(\\\\.\\KernelFlirt) FAIL — проверь права и `KfLoader load`");
        }
        else
        {
            var parts = arg.Split(':');
            string host = parts[0];
            int port = parts.Length > 1 ? int.Parse(parts[1]) : 31337;
            if (Client.ConnectRemote(host, port))
            {
                if (Client.Ping(out var v, out _))
                    Ok($"{Kw("connected")} ({Yp(host)}:{Num(port)}), driver v{Hex(v)}");
                else
                    Info("connected (TCP), но Ping не отвечает");
            }
            else Err($"connect FAIL ({host}:{port})");
        }
    }

    private static void CmdDisconnect()
    {
        StopTrapFrameKeeper();
        StopEventListener();
        Client.Disconnect();
        lock (Sess.Lock)
        {
            Sess.TargetPid = 0; Sess.CurrentTid = 0; Sess.IsBreak = false;
            Sess.IsPausedViaSuspend = false; Sess.Breakpoints.Clear();
        }
        Ok(Kw("disconnected"));
    }

    // ── Информационные ───────────────────────────────────────────────────

    private static bool Require(bool cond, string msg)
    {
        if (!cond) Print(msg);
        return cond;
    }

    private static void CmdProcs()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        var procs = Client.EnumProcesses();
        Info($"{Num(procs.Count)} processes:");
        foreach (var p in procs.OrderBy(x => x.Pid))
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Cyan, p.Pid.ToString().PadLeft(6))}  "
                            + $"{Ansi.Wrap(Ansi.Dim, "s" + p.SessionId)}  "
                            + Ansi.Wrap(Ansi.Yellow, p.Name));
    }

    private static void CmdMods()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid;
        lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан — `attach <pid>`")) return;
        var mods = Client.EnumModules(pid);
        Info($"{Num(mods.Count)} modules in PID {Pid(pid)}:");
        foreach (var m in mods.OrderBy(x => x.Base))
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Gray, Fa(m.Base))}  "
                            + $"size={Ansi.Wrap(Ansi.Orange, m.Size.ToString("X8"))}  "
                            + Ansi.Wrap(Ansi.Yellow, m.Name));
    }

    private static void CmdThreads()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid;
        lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан — `attach <pid>`")) return;
        var threads = Client.EnumThreads(pid);
        Info($"{Num(threads.Count)} threads in PID {Pid(pid)}:");
        foreach (var t in threads)
        {
            string? startSym = Syms.Resolve(t.StartAddress);
            string tail = startSym != null ? "  " + Ansi.Wrap(Ansi.Yellow, startSym) : "";
            Console.WriteLine($"  TID {Ansi.Wrap(Ansi.Cyan, t.ThreadId.ToString().PadLeft(6))}  "
                            + $"start={Ansi.Wrap(Ansi.Gray, Fa(t.StartAddress))}  "
                            + $"state={Ansi.Wrap(Ansi.Dim, t.State.ToString())}  "
                            + $"prio={Ansi.Wrap(Ansi.Dim, t.Priority.ToString())}{tail}");
        }
    }

    private static void CmdKmods()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        var mods = Client.EnumKernelModules();
        if (mods.Count == 0) { Info("нет модулей ядра (или ENUM FAIL)"); return; }
        Info($"{Num(mods.Count)} kernel modules:");
        foreach (var m in mods.OrderBy(x => x.Base))
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Gray, FormatAddr(m.Base, false))}  "
                            + $"size={Ansi.Wrap(Ansi.Orange, m.Size.ToString("X8"))}  "
                            + Ansi.Wrap(Ansi.Yellow, m.Name));
    }

    private static void CmdSuspend(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint tid;
        if (arg.Length == 0) { lock (Sess.Lock) tid = Sess.CurrentTid; }
        else if (!uint.TryParse(arg, out tid)) { Print("usage: suspend [tid]"); return; }
        if (!Require(tid != 0, "TID не задан")) return;
        if (Client.SuspendThread(tid)) Ok($"{Kw("suspended")} TID {Pid(tid)}");
        else Err("SuspendThread FAIL");
    }

    private static void CmdResume(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint tid;
        if (arg.Length == 0) { lock (Sess.Lock) tid = Sess.CurrentTid; }
        else if (!uint.TryParse(arg, out tid)) { Print("usage: resume [tid]"); return; }
        if (!Require(tid != 0, "TID не задан")) return;
        if (Client.ResumeThread(tid)) Ok($"{Kw("resumed")} TID {Pid(tid)}");
        else Err("ResumeThread FAIL");
    }

    /// <summary>!peb — разбор PEB target'а (BeingDebugged, ImageBase, Ldr, NtGlobalFlag).</summary>
    private static void CmdPeb()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; bool is32; lock (Sess.Lock) { pid = Sess.TargetPid; is32 = Sess.Is32Bit; }
        if (!Require(pid != 0, "target не задан")) return;

        var peb = Client.GetPebAddress(pid);
        if (peb == null) { Err("GetPebAddress FAIL"); return; }
        // Для WoW64-target показываем 32-битный PEB, иначе 64-битный.
        ulong pebAddr = is32 && peb.Value.Peb32 != 0 ? peb.Value.Peb32 : peb.Value.Peb64;
        if (pebAddr == 0) { Err("PEB-адрес = 0"); return; }

        // Смещения полей PEB различаются для 32- и 64-битной структуры.
        int ptr = is32 ? 4 : 8;
        int offBeingDbg = 2;
        int offImageBase = is32 ? 0x08 : 0x10;
        int offLdr       = is32 ? 0x0C : 0x18;
        int offParams    = is32 ? 0x10 : 0x20;
        int offNtGlobal  = is32 ? 0x68 : 0xBC;

        uint readSize = (uint)(offNtGlobal + 4);
        var data = Client.ReadMemory(pid, pebAddr, readSize);
        if (data == null) { Err("ReadMemory(PEB) FAIL"); return; }

        ulong RP(int off) => (ulong)(ptr == 4 ? BitConverter.ToUInt32(data, off) : BitConverter.ToUInt64(data, off));

        // Метка поля: фиксированная ширина ДО окраски (escape-коды не учитываются в длину).
        string L(string s) => Ansi.Wrap(Ansi.Cyan, s.PadRight(18));
        Info($"PEB @ {Addr(pebAddr)} ({(is32 ? "32-bit" : "64-bit")}):");
        byte beingDbg = data[offBeingDbg];
        string dbgColor = beingDbg != 0 ? Ansi.Wrap(Ansi.Red, beingDbg.ToString()) : Ansi.Wrap(Ansi.Green, "0");
        Console.WriteLine($"    {L("BeingDebugged")}= {dbgColor}");
        Console.WriteLine($"    {L("ImageBaseAddress")}= {Addr(RP(offImageBase))}");
        Console.WriteLine($"    {L("Ldr")}= {Addr(RP(offLdr))}");
        Console.WriteLine($"    {L("ProcessParameters")}= {Addr(RP(offParams))}");
        uint ntGlobal = BitConverter.ToUInt32(data, offNtGlobal);
        Console.WriteLine($"    {L("NtGlobalFlag")}= {Ansi.Wrap(Ansi.Orange, "0x" + ntGlobal.ToString("X"))}");
    }

    /// <summary>stats — счётчики и адреса inline-хука (IOCTL_KF_GET_HOOK_STATS).</summary>
    private static void CmdStats()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        var s = Client.GetHookStats();
        if (s == null) { Err("GetHookStats FAIL"); return; }
        var v = s.Value;
        string N(uint x) => Ansi.Wrap(Ansi.Orange, x.ToString());
        string A(ulong x) => Ansi.Wrap(Ansi.Gray, FormatAddr(x, false));
        string L(string x) => Ansi.Wrap(Ansi.Cyan, x);   // метка поля
        Info("hook stats:");
        Console.WriteLine($"    {L("HookCalls")}={N(v.HookCallCount)}  {L("BpHit")}={N(v.BpHitCount)}  "
                        + $"{L("BpNotFound")}={N(v.BpNotFoundCount)}  {L("Steps")}={N(v.StepCount)}");
        Console.WriteLine($"    {L("TargetCalls")}={N(v.TargetCallCount)}  {L("ThreadBlocked")}={N(v.ThreadBlocked)}  "
                        + $"{L("ContinueMode")}={N(v.ContinueMode)}");
        Console.WriteLine($"    {L("KdDebuggerEnabled")}={N(v.KdDebuggerEnabled)}  {L("KdDebuggerNotPresent")}={N(v.KdDebuggerNotPresent)}");
        Console.WriteLine($"    {L("KiDebugRoutine")}: addr={A(v.KiDebugRoutineAddr)} orig={A(v.KiDebugRoutineOrig)} now={A(v.KiDebugRoutineNow)}");
        Console.WriteLine($"    {L("HookedFunc(KdpStub)")}={A(v.HookedFuncAddr)}  {L("KdTrap")}={A(v.KdTrapAddr)}");
        Console.WriteLine($"    {L("LastTargetAddr")}={A(v.LastTargetAddr)}  {L("LastTargetCode")}={Ansi.Wrap(Ansi.Orange, "0x" + v.LastTargetCode.ToString("X8"))}");
        if (v.TraceActive != 0 || v.TraceStepCount != 0)
            Console.WriteLine($"    {L("trace")}: active={N(v.TraceActive)} steps={N(v.TraceStepCount)} "
                            + $"av={N(v.TraceAvCount)} int3={N(v.TraceInt3Count)} unk={N(v.TraceUnkCount)}");
    }

    /// <summary>
    /// k — стек вызовов через раскрутку по frame-pointer (RBP/EBP).
    /// Точно для кода с классическим прологом (push rbp; mov rbp,rsp); при FPO/leaf
    /// результат приблизительный — это ограничение метода, а не драйвера.
    /// </summary>
    private static void CmdStack(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; bool is32; lock (Sess.Lock) { pid = Sess.TargetPid; tid = Sess.CurrentTid; is32 = Sess.Is32Bit; }
        if (!Require(pid != 0 && tid != 0, "target/TID не задан")) return;

        int maxFrames = 64;
        if (arg.Length > 0 && int.TryParse(arg, out int mf) && mf > 0) maxFrames = Math.Min(mf, 256);

        var regs = Client.ReadRegisters(pid, tid);
        if (regs == null) { Err("ReadRegisters FAIL"); return; }
        var r = regs.Value;
        int ptr = is32 ? 4 : 8;
        ulong ip = is32 ? (r.Rip & 0xFFFFFFFF) : r.Rip;
        ulong fp = is32 ? (r.Rbp & 0xFFFFFFFF) : r.Rbp;

        Info($"call stack (frame-pointer walk, TID {Pid(tid)}):");
        void Frame(int n, ulong a)
        {
            string? sym = Syms.Resolve(a);
            string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Dim, n.ToString("D2"))}  {Addr(a)}{symPart}");
        }

        Frame(0, ip);
        for (int n = 1; n < maxFrames; n++)
        {
            if (fp == 0) break;
            var retData = Client.ReadMemory(pid, fp + (ulong)ptr, (uint)ptr);
            if (retData == null || retData.Length < ptr) break;
            ulong ret = is32 ? BitConverter.ToUInt32(retData, 0) : BitConverter.ToUInt64(retData, 0);
            if (ret == 0) break;
            Frame(n, ret);

            var fpData = Client.ReadMemory(pid, fp, (uint)ptr);
            if (fpData == null || fpData.Length < ptr) break;
            ulong newFp = is32 ? BitConverter.ToUInt32(fpData, 0) : BitConverter.ToUInt64(fpData, 0);
            if (newFp <= fp) break;   // стек раскручивается вверх — защита от петли
            fp = newFp;
        }
    }

    private static void CmdAttach(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        if (!uint.TryParse(arg, out uint pid))
        {
            Print("usage: attach <pid>");
            return;
        }
        if (!Client.InstallHook(pid))
        {
            Err($"InstallHook(PID {Pid(pid)}) FAIL");
            return;
        }
        // Автоопределение разрядности: Peb32Address != 0 значит WoW64-target.
        var peb = Client.GetPebAddress(pid);
        bool is32 = peb.HasValue && peb.Value.Peb32 != 0;
        uint firstTid;
        lock (Sess.Lock)
        {
            Sess.TargetPid = pid;
            Sess.IsBreak = false;
            Sess.Is32Bit = is32;
            var threads = Client.EnumThreads(pid);
            Sess.CurrentTid = firstTid = threads.Count > 0 ? threads[0].ThreadId : 0;
        }
        Ok($"{Kw("attached")} to PID {Pid(pid)} ({Ansi.Wrap(Ansi.Yellow, is32 ? "x86 / WoW64" : "x64")}), "
            + $"hook installed. TID={Pid(firstTid)}");
        if (is32)
            Info("WoW64-target: для `t` и `g` используется EB-FE-spin-trap.");
        LoadSymbolsForTarget(pid);
        StartEventListener();
    }

    private static void CmdOpen(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        if (arg.Length == 0) { Print("usage: open <path-to-exe>"); return; }
        var path = arg.Trim('"');

        if (!Client.IsRemote)
        {
            Print("open: только через релей (TCP). Локальный запуск с EB-FE-патчем "
                + "не реализован — используй UI или KfRelay на той же машине.");
            return;
        }

        Info($"{Kw("creating process")}: {Yp(path)}");
        var info = Client.CreateProcess(path);
        if (info == null || info.Value.ProcessId == 0)
        {
            Err("CreateProcess FAIL (путь верный? файл существует на той стороне релея?)");
            return;
        }
        var p = info.Value;
        bool is32 = p.EntryIs32Bit != 0;
        lock (Sess.Lock) Sess.Is32Bit = is32;
        Ok($"{Kw("created")} PID={Pid(p.ProcessId)} TID={Pid(p.ThreadId)} "
            + $"ImageBase={Addr(p.ImageBase)} ({Ansi.Wrap(Ansi.Yellow, is32 ? "x86 / WoW64" : "x64")})");
        if (p.EntryPatchBytes == 0)
        {
            Info("entry-patch пропущен — поток уже исполняется, attach по PID");
        }
        else
        {
            Info($"entry={Addr(p.EntryPointAddress)} ({Kw("orig")}={Ansi.Wrap(Ansi.Orange, p.EntryOrigByte0.ToString("X2"))} "
                + $"{Ansi.Wrap(Ansi.Orange, p.EntryOrigByte1.ToString("X2"))}, patched {Num((uint)p.EntryPatchBytes)} byte EB FE)");
        }

        // Дальше повторяем UI-flow: InstallHook → ResumeThread → polling RIP →
        // SuspendThread → восстановление оригинальных байт.
        if (!Client.InstallHook(p.ProcessId))
        {
            Print("InstallHook FAIL — детач");
            return;
        }
        lock (Sess.Lock)
        {
            Sess.TargetPid = p.ProcessId;
            Sess.CurrentTid = p.ThreadId;
            Sess.IsBreak = false;
        }
        StartEventListener();

        if (p.EntryPatchBytes == 2 && p.EntryPointAddress != 0)
        {
            Info("resuming thread, polling RIP до entry...");
            Client.ResumeThread(p.ThreadId);

            bool reached = false;
            for (int i = 0; i < 80; i++)   // до 8 секунд
            {
                Thread.Sleep(100);
                Client.SuspendThread(p.ThreadId);
                var regs = Client.ReadRegisters(p.ProcessId, p.ThreadId);
                if (regs.HasValue && regs.Value.Rip == p.EntryPointAddress)
                {
                    reached = true;
                    Ok($"{Kw("reached entry")} after {Num((i + 1) * 100)} ms");
                    break;
                }
                Client.ResumeThread(p.ThreadId);
            }
            if (!reached)
            {
                Print("не дождался entry за 8с — поток приостановлен где есть");
                Client.SuspendThread(p.ThreadId);
            }

            // Восстановим 2 байта оригинала
            var orig = new[] { p.EntryOrigByte0, p.EntryOrigByte1 };
            if (!Client.WriteMemory(p.ProcessId, p.EntryPointAddress, orig))
                Print("WARN: не удалось восстановить байты entry");

            // Теперь поток в SUSPENDED-state на entry — это как `_isPausedViaSuspend` в UI.
            // Для шага из этого состояния нужно SingleStep + ResumeThread (т.к. поток не
            // в KfReportAndBlock), но в CLI пока нет специальной обёртки — пользователь
            // может стартовать через `g` (Run всё равно работает: при `g` мы делаем
            // ContinueDebugEvent, но в suspend-state он бесполезен; правильнее `ss`+`g`
            // или явный `interrupt`/`g` после первого break).
            lock (Sess.Lock) { Sess.IsBreak = true; Sess.IsPausedViaSuspend = true; }
            // Сразу читаем регистры: (1) заполняем Sess.LastRegs, чтобы парсер
            // выражений умел `rip`/`rsp`/etc без отдельного `r`; (2) драйверный
            // KfReadRegisters читает байты из KTHREAD->TrapFrame под __try —
            // это форсит kernel-MM page-in для kernel-stack потока, и дальнейшие
            // SingleStep/WriteRegisters работают без page-out race.
            var initRegs = Client.ReadRegisters(p.ProcessId, p.ThreadId);
            if (initRegs.HasValue)
            {
                var rr = initRegs.Value;
                lock (Sess.Lock) { Sess.LastRegs = rr; Sess.LastRip = rr.Rip; Sess.LastRsp = rr.Rsp; }
            }
            LoadSymbolsForTarget(p.ProcessId);
            Ok($"{Kw("stopped at entry point")} of {Yp(path)}");
            Info("Готово. `r` — регистры, `u rip` — дизасм, `t` — step into, `g` — run.");

            // Background keeper: каждые 2 секунды трогаем TrapFrame через ReadRegisters,
            // чтобы kernel не вытеснил страницу пока пользователь думает над командами.
            // Останавливается когда target отдетачен или поток уже не в suspend-state.
            StartTrapFrameKeeper();
        }
        else
        {
            Print($"attached to running PID {p.ProcessId}");
        }
    }

    private static void CmdDetach()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        StopTrapFrameKeeper();
        StopEventListener();
        Client.RemoveHook();
        Client.Reset();
        lock (Sess.Lock)
        {
            Sess.TargetPid = 0; Sess.CurrentTid = 0; Sess.IsBreak = false;
            Sess.IsPausedViaSuspend = false; Sess.Breakpoints.Clear();
        }
        Ok(Kw("detached"));
    }

    private static void CmdReset()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        Client.Reset();
        lock (Sess.Lock) Sess.Breakpoints.Clear();
        Ok(Kw("driver reset"));
    }

    private static void CmdSetTid(string arg)
    {
        if (!uint.TryParse(arg, out uint tid)) { Err("usage: tid <tid>"); return; }
        lock (Sess.Lock) Sess.CurrentTid = tid;
        Ok($"current TID = {Pid(tid)}");
    }

    private static void CmdColor(string arg)
    {
        switch (arg.Trim().ToLowerInvariant())
        {
            case "on":   Ansi.Enabled = true;  Ok($"color: {Ansi.Wrap(Ansi.Green, "on")}"); break;
            case "off":  Ansi.Enabled = false; Console.WriteLine("color: off"); break;
            case "":     Console.WriteLine($"color: " + (Ansi.Enabled ? Ansi.Wrap(Ansi.Green, "on") : "off")); break;
            default:     Err("usage: color [on|off]"); break;
        }
    }

    private static void CmdAntiDebug(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        var (sub, srest) = Split(arg);
        switch (sub.ToLowerInvariant())
        {
            case "clr_debug_port":
                if (!Require(pid != 0, "target не задан")) return;
                if (Client.ClearDebugPort(pid)) Ok($"{Kw("DebugPort cleared")} for PID {Pid(pid)}");
                else Err("ClearDebugPort FAIL");
                break;
            case "clr_thread_hide":
                if (!Require(pid != 0, "target не задан")) return;
                if (Client.ClearThreadHide(pid)) Ok($"{Kw("HideFromDebugger cleared")} for PID {Pid(pid)}");
                else Err("ClearThreadHide FAIL");
                break;
            case "ntqsi":
                if (srest.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    if (Client.InstallNtQsiHook())
                        Ok($"{Kw("NtQSI hook installed")} (class 0x23 spoofed)");
                    else Err("InstallNtQsiHook FAIL");
                }
                else if (srest.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (Client.RemoveNtQsiHook()) Ok(Kw("NtQSI hook removed"));
                    else Err("RemoveNtQsiHook FAIL");
                }
                else Err("usage: ad ntqsi on|off");
                break;
            case "spoof":
                if (srest.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    if (Client.SpoofSharedData(true))
                        Ok($"{Kw("KUSER_SHARED_DATA.KdDebuggerEnabled spoof")}: {Ansi.Wrap(Ansi.Green, "on")}");
                    else Err("SpoofSharedData FAIL");
                }
                else if (srest.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (Client.SpoofSharedData(false))
                        Ok($"{Kw("KUSER_SHARED_DATA.KdDebuggerEnabled spoof")}: off");
                    else Err("SpoofSharedData FAIL");
                }
                else Err("usage: ad spoof on|off");
                break;
            default:
                Err("usage: ad clr_debug_port | clr_thread_hide | ntqsi on|off | spoof on|off");
                break;
        }
    }

    // ── Регистры ─────────────────────────────────────────────────────────

    private static void CmdRegisters(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; lock (Sess.Lock) { pid = Sess.TargetPid; tid = Sess.CurrentTid; }
        if (!Require(pid != 0 && tid != 0, "target/TID не задан")) return;

        // Установка: r <name>=<value>
        if (arg.Contains('='))
        {
            var idx = arg.IndexOf('=');
            string name = arg[..idx].Trim().ToLowerInvariant();
            string val = arg[(idx + 1)..].Trim();
            if (!TryParseValue(val, out ulong newVal))
            {
                Print($"не могу разобрать число: {val}");
                return;
            }
            var cur = Client.ReadRegisters(pid, tid);
            if (cur == null) { Print("ReadRegisters FAIL"); return; }
            var updated = SetRegister(cur.Value, name, newVal);
            if (updated == null) { Print($"неизвестный регистр: {name}"); return; }
            if (Client.WriteRegisters(pid, tid, updated.Value))
                Print($"{name} = 0x{newVal:X}");
            else
                Print("WriteRegisters FAIL (проверь CS/SS/RIP/RSP — драйвер валидирует селекторы и каноничность)");
            return;
        }

        var regs = Client.ReadRegisters(pid, tid);
        if (regs == null) { Err("ReadRegisters FAIL"); return; }
        var r = regs.Value;
        lock (Sess.Lock) { Sess.LastRip = r.Rip; Sess.LastRsp = r.Rsp; Sess.LastRegs = r; }

        // Конкретный регистр
        if (arg.Length > 0)
        {
            var name = arg.ToLowerInvariant();
            var v = GetRegister(r, name);
            if (v.HasValue) Print($"{name} = 0x{v.Value:X16}");
            else Print($"неизвестный регистр: {name}");
            return;
        }

        bool is32; lock (Sess.Lock) is32 = Sess.Is32Bit;
        string Reg(string name, ulong val, int width = 16)
        {
            string hex = width == 8 ? $"{(uint)val:X8}" : $"{val:X16}";
            return Ansi.Wrap(Ansi.Cyan, name) + "=" + Ansi.Wrap(Ansi.Orange, hex);
        }
        if (is32)
        {
            Console.WriteLine($"  {Reg("EAX", r.Rax, 8)}  {Reg("EBX", r.Rbx, 8)}  {Reg("ECX", r.Rcx, 8)}  {Reg("EDX", r.Rdx, 8)}");
            Console.WriteLine($"  {Reg("ESI", r.Rsi, 8)}  {Reg("EDI", r.Rdi, 8)}  {Reg("EBP", r.Rbp, 8)}  {Reg("ESP", r.Rsp, 8)}");
            Console.WriteLine($"  {Reg("EIP", r.Rip, 8)}  {Reg("EFLAGS", r.Rflags, 8)}");
            Console.WriteLine($"  CS={Ansi.Wrap(Ansi.Orange, r.Cs.ToString("X4"))} DS={Ansi.Wrap(Ansi.Orange, r.Ds.ToString("X4"))} ES={Ansi.Wrap(Ansi.Orange, r.Es.ToString("X4"))} FS={Ansi.Wrap(Ansi.Orange, r.Fs.ToString("X4"))} GS={Ansi.Wrap(Ansi.Orange, r.Gs.ToString("X4"))} SS={Ansi.Wrap(Ansi.Orange, r.Ss.ToString("X4"))}");
            Console.WriteLine($"  {Reg("DR0", r.Dr0, 8)} {Reg("DR1", r.Dr1, 8)} {Reg("DR2", r.Dr2, 8)} {Reg("DR3", r.Dr3, 8)}");
            Console.WriteLine($"  {Reg("DR6", r.Dr6, 8)} {Reg("DR7", r.Dr7, 8)}");
        }
        else
        {
            Console.WriteLine($"  {Reg("RAX", r.Rax)}  {Reg("RBX", r.Rbx)}  {Reg("RCX", r.Rcx)}  {Reg("RDX", r.Rdx)}");
            Console.WriteLine($"  {Reg("RSI", r.Rsi)}  {Reg("RDI", r.Rdi)}  {Reg("RBP", r.Rbp)}  {Reg("RSP", r.Rsp)}");
            Console.WriteLine($"  {Reg("R8 ", r.R8)}  {Reg("R9 ", r.R9)}  {Reg("R10", r.R10)}  {Reg("R11", r.R11)}");
            Console.WriteLine($"  {Reg("R12", r.R12)}  {Reg("R13", r.R13)}  {Reg("R14", r.R14)}  {Reg("R15", r.R15)}");
            Console.WriteLine($"  {Reg("RIP", r.Rip)}  {Reg("RFLAGS", r.Rflags)}");
            Console.WriteLine($"  CS={Ansi.Wrap(Ansi.Orange, r.Cs.ToString("X4"))} DS={Ansi.Wrap(Ansi.Orange, r.Ds.ToString("X4"))} ES={Ansi.Wrap(Ansi.Orange, r.Es.ToString("X4"))} FS={Ansi.Wrap(Ansi.Orange, r.Fs.ToString("X4"))} GS={Ansi.Wrap(Ansi.Orange, r.Gs.ToString("X4"))} SS={Ansi.Wrap(Ansi.Orange, r.Ss.ToString("X4"))}");
            Console.WriteLine($"  {Reg("DR0", r.Dr0)} {Reg("DR1", r.Dr1)} {Reg("DR2", r.Dr2)} {Reg("DR3", r.Dr3)}");
            Console.WriteLine($"  {Reg("DR6", r.Dr6)} {Reg("DR7", r.Dr7)}");
        }
    }

    private static ulong? GetRegister(KF_REGISTERS r, string name) => name switch
    {
        // 64-bit имена
        "rax" => r.Rax, "rbx" => r.Rbx, "rcx" => r.Rcx, "rdx" => r.Rdx,
        "rsi" => r.Rsi, "rdi" => r.Rdi, "rbp" => r.Rbp, "rsp" => r.Rsp,
        "r8"  => r.R8,  "r9"  => r.R9,  "r10" => r.R10, "r11" => r.R11,
        "r12" => r.R12, "r13" => r.R13, "r14" => r.R14, "r15" => r.R15,
        "rip" => r.Rip, "rflags" => r.Rflags,
        // 32-bit алиасы для WoW64 (читаем нижние 32 бита того же поля)
        "eax" => r.Rax & 0xFFFFFFFF, "ebx" => r.Rbx & 0xFFFFFFFF,
        "ecx" => r.Rcx & 0xFFFFFFFF, "edx" => r.Rdx & 0xFFFFFFFF,
        "esi" => r.Rsi & 0xFFFFFFFF, "edi" => r.Rdi & 0xFFFFFFFF,
        "ebp" => r.Rbp & 0xFFFFFFFF, "esp" => r.Rsp & 0xFFFFFFFF,
        "eip" => r.Rip & 0xFFFFFFFF, "eflags" => r.Rflags & 0xFFFFFFFF,
        "cs" => r.Cs, "ds" => r.Ds, "es" => r.Es, "fs" => r.Fs, "gs" => r.Gs, "ss" => r.Ss,
        "dr0" => r.Dr0, "dr1" => r.Dr1, "dr2" => r.Dr2, "dr3" => r.Dr3,
        "dr6" => r.Dr6, "dr7" => r.Dr7,
        _ => null,
    };

    private static KF_REGISTERS? SetRegister(KF_REGISTERS r, string name, ulong v)
    {
        // 32-bit имена — присваиваем нижние 32 бита, верхние обнуляем (как делает CPU
        // при записи в EAX и т.п.).
        ulong v32 = v & 0xFFFFFFFF;
        switch (name)
        {
            case "rax": r.Rax = v; break;     case "eax": r.Rax = v32; break;
            case "rbx": r.Rbx = v; break;     case "ebx": r.Rbx = v32; break;
            case "rcx": r.Rcx = v; break;     case "ecx": r.Rcx = v32; break;
            case "rdx": r.Rdx = v; break;     case "edx": r.Rdx = v32; break;
            case "rsi": r.Rsi = v; break;     case "esi": r.Rsi = v32; break;
            case "rdi": r.Rdi = v; break;     case "edi": r.Rdi = v32; break;
            case "rbp": r.Rbp = v; break;     case "ebp": r.Rbp = v32; break;
            case "rsp": r.Rsp = v; break;     case "esp": r.Rsp = v32; break;
            case "r8":  r.R8  = v; break;     case "r9":  r.R9  = v; break;
            case "r10": r.R10 = v; break;     case "r11": r.R11 = v; break;
            case "r12": r.R12 = v; break;     case "r13": r.R13 = v; break;
            case "r14": r.R14 = v; break;     case "r15": r.R15 = v; break;
            case "rip": r.Rip = v; break;     case "eip": r.Rip = v32; break;
            case "rflags": case "eflags": r.Rflags = v; break;
            default: return null;
        }
        return r;
    }

    // ── Память ───────────────────────────────────────────────────────────

    private static void CmdDump(string arg, bool byteMode)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, scount) = Split(arg);
        if (!TryParseValue(saddr, out ulong addr)) { Print("usage: d <addr> [count]"); return; }
        int count = byteMode ? 64 : 8;
        if (scount.Length > 0 && int.TryParse(scount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            count = n;
        uint size = (uint)(byteMode ? count : count * 8);
        var data = Client.ReadMemory(pid, addr, size);
        if (data == null) { Print("ReadMemory FAIL"); return; }
        if (byteMode) PrintHexDump(addr, data);
        else PrintQwordDump(addr, data);
    }

    private static void PrintHexDump(ulong addr, byte[] data)
    {
        for (int i = 0; i < data.Length; i += 16)
        {
            int row = Math.Min(16, data.Length - i);
            var hex = string.Join(' ', Enumerable.Range(0, row).Select(j => data[i + j].ToString("x2")));
            hex = hex.PadRight(16 * 3 - 1);
            var ascii = new string(Enumerable.Range(0, row)
                .Select(j => { byte b = data[i + j]; return b >= 0x20 && b < 0x7F ? (char)b : '.'; }).ToArray());
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Gray, Fa(addr + (ulong)i))}  "
                            + $"{Ansi.Wrap(Ansi.Orange, hex)}  "
                            + Ansi.Wrap(Ansi.Green, ascii));
        }
    }

    private static void PrintQwordDump(ulong addr, byte[] data)
    {
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            ulong q = BitConverter.ToUInt64(data, i);
            string sym = Syms.Resolve(q) ?? "";
            string tail = sym.Length > 0 ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
            Console.WriteLine($"  {Ansi.Wrap(Ansi.Gray, Fa(addr + (ulong)i))}  "
                            + $"{Ansi.Wrap(Ansi.Orange, q.ToString("X16"))}{tail}");
        }
    }

    private static void CmdEdit(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;

        var (saddr, srest) = Split(arg);
        if (!TryParseValue(saddr, out ulong addr)) { Print("usage: e <addr> <bytes>"); return; }

        // Парсим оставшийся хвост как hex-байты — пробелы между байтами опциональны:
        // допустим и `90 90 c3`, и `9090c3`, и `e8 12 00 00 00`.
        var clean = new string(srest.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length == 0 || clean.Length % 2 != 0)
        { Print("hex длиной должны идти парами: e.g. `e 401570 90 90 c3`"); return; }

        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(clean.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            { Print($"не hex-байт по смещению {i}: {clean.Substring(i * 2, 2)}"); return; }
        }

        if (!Client.WriteMemory(pid, addr, bytes))
        { Err("WriteMemory FAIL"); return; }
        Ok($"{Kw("wrote")} {Num(bytes.Length)} byte(s) at {Addr(addr)}: "
            + Ansi.Wrap(Ansi.Orange, string.Join(' ', bytes.Select(b => b.ToString("x2")))));
    }

    // ── Дампы строк и типизированные дампы ───────────────────────────────

    /// <summary>da/du — вывод ASCII / UTF-16 строки по адресу (до NUL или count символов).</summary>
    private static void CmdDumpString(string arg, bool wide)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, scount) = Split(arg);
        if (!TryParseValue(saddr, out ulong addr))
        { Print($"usage: {(wide ? "du" : "da")} <addr> [count]"); return; }

        int count = 64;
        if (scount.Length > 0 && int.TryParse(scount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
            count = n;

        uint bytesToRead = (uint)(wide ? count * 2 : count);
        var data = Client.ReadMemory(pid, addr, bytesToRead);
        if (data == null) { Print("ReadMemory FAIL"); return; }

        string text;
        if (wide)
        {
            int chars = 0;
            while (chars * 2 + 1 < data.Length)
            {
                ushort c = (ushort)(data[chars * 2] | (data[chars * 2 + 1] << 8));
                if (c == 0) break;
                chars++;
            }
            text = System.Text.Encoding.Unicode.GetString(data, 0, chars * 2);
        }
        else
        {
            int len = 0;
            while (len < data.Length && data[len] != 0) len++;
            text = System.Text.Encoding.ASCII.GetString(data, 0, len);
        }
        Console.WriteLine($"  {Addr(addr)}  \"{Ansi.Wrap(Ansi.Green, text)}\"");
    }

    /// <summary>dd/dw — дамп DWORD/WORD значениями. unit = 4 или 2 байта.</summary>
    private static void CmdDumpUnits(string arg, int unit)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, scount) = Split(arg);
        if (!TryParseValue(saddr, out ulong addr))
        { Print($"usage: {(unit == 4 ? "dd" : "dw")} <addr> [count]"); return; }

        int count = unit == 4 ? 16 : 32;
        if (scount.Length > 0 && int.TryParse(scount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
            count = n;

        uint size = (uint)(count * unit);
        var data = Client.ReadMemory(pid, addr, size);
        if (data == null) { Print("ReadMemory FAIL"); return; }

        int perRow = unit == 4 ? 4 : 8;
        for (int i = 0; i + unit <= data.Length; i += unit * perRow)
        {
            var vals = new List<string>();
            for (int j = 0; j < perRow && i + (j + 1) * unit <= data.Length; j++)
            {
                int off = i + j * unit;
                string v = unit == 4
                    ? BitConverter.ToUInt32(data, off).ToString("X8")
                    : BitConverter.ToUInt16(data, off).ToString("X4");
                vals.Add(Ansi.Wrap(Ansi.Orange, v));
            }
            Console.WriteLine($"  {Addr(addr + (ulong)i)}  {string.Join(' ', vals)}");
        }
    }

    /// <summary>dp — дамп указателей (4 байта на x86, 8 на x64) с резолвом символов.</summary>
    private static void CmdDumpPointers(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; bool is32; lock (Sess.Lock) { pid = Sess.TargetPid; is32 = Sess.Is32Bit; }
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, scount) = Split(arg);
        if (!TryParseValue(saddr, out ulong addr)) { Print("usage: dp <addr> [count]"); return; }

        int count = 8;
        if (scount.Length > 0 && int.TryParse(scount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
            count = n;

        int unit = is32 ? 4 : 8;
        var data = Client.ReadMemory(pid, addr, (uint)(count * unit));
        if (data == null) { Print("ReadMemory FAIL"); return; }
        for (int i = 0; i + unit <= data.Length; i += unit)
        {
            ulong p = is32 ? BitConverter.ToUInt32(data, i) : BitConverter.ToUInt64(data, i);
            string sym = Syms.Resolve(p) ?? "";
            string tail = sym.Length > 0 ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
            Console.WriteLine($"  {Addr(addr + (ulong)i)}  {Ansi.Wrap(Ansi.Orange, FormatAddr(p, is32))}{tail}");
        }
    }

    /// <summary>
    /// Поиск по памяти: `s <addr> <len> <pattern>`.
    /// pattern — hex-байты с ?? wildcards (`s 401000 1000 48 8b ?? c3`),
    /// либо строка в кавычках (`s 401000 2000 "MZ"`), либо L"unicode".
    /// </summary>
    private static void CmdSearch(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;

        var (saddr, rest1) = Split(arg);
        var (slen, pat) = Split(rest1);
        if (saddr.Length == 0 || slen.Length == 0 || pat.Length == 0)
        { Print("usage: s <addr> <len> <pattern>   (pattern: hex c ?? | \"строка\" | L\"unicode\")"); return; }
        if (!TryParseValue(saddr, out ulong addr)) { Print($"не разобрать адрес '{saddr}'"); return; }
        if (!TryParseValue(slen, out ulong len) || len == 0) { Print($"не разобрать длину '{slen}'"); return; }
        if (len > 0x400000) { Print("длина > 4 МБ — сузь диапазон"); return; }

        if (!TryParsePattern(pat, out byte[] needle, out bool[] mask))
        { Print("не смог разобрать паттерн"); return; }

        var data = Client.ReadMemory(pid, addr, (uint)len);
        if (data == null) { Print("ReadMemory FAIL"); return; }

        int found = 0;
        for (int i = 0; i + needle.Length <= data.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (mask[j] && data[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) continue;
            ulong hitAddr = addr + (ulong)i;
            int show = Math.Min(needle.Length, 16);
            var bytesHex = string.Join(' ', Enumerable.Range(0, show).Select(k => data[i + k].ToString("x2")));
            Console.WriteLine($"  {Addr(hitAddr)}  {Ansi.Wrap(Ansi.Orange, bytesHex)}");
            if (++found >= 256) { Info("показаны первые 256 совпадений — сузь диапазон"); break; }
        }
        Info($"{Kw("найдено")}: {Num(found)} совпадений в {Hex(len)} байтах");
    }

    /// <summary>Парсит паттерн поиска в (байты + маску). mask[i]=false означает wildcard.</summary>
    private static bool TryParsePattern(string pat, out byte[] bytes, out bool[] mask)
    {
        bytes = Array.Empty<byte>(); mask = Array.Empty<bool>();
        pat = pat.Trim();

        // Строковый паттерн: "ascii" или L"unicode"
        bool wide = false;
        if (pat.StartsWith("L\"", StringComparison.OrdinalIgnoreCase)) { wide = true; pat = pat[1..]; }
        if (pat.StartsWith('"') && pat.EndsWith('"') && pat.Length >= 2)
        {
            string s = pat[1..^1];
            bytes = wide ? System.Text.Encoding.Unicode.GetBytes(s)
                         : System.Text.Encoding.ASCII.GetBytes(s);
            mask = Enumerable.Repeat(true, bytes.Length).ToArray();
            return bytes.Length > 0;
        }

        // Hex-паттерн с ?? wildcards (токены через пробел).
        var tokens = pat.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var bl = new List<byte>(); var ml = new List<bool>();
        foreach (var t in tokens)
        {
            if (t == "??" || t == "?")
            { bl.Add(0); ml.Add(false); continue; }
            if (byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            { bl.Add(b); ml.Add(true); continue; }
            return false;   // мусорный токен
        }
        bytes = bl.ToArray(); mask = ml.ToArray();
        return bytes.Length > 0;
    }

    // ── Выделение / защита памяти ────────────────────────────────────────

    private static void CmdAlloc(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (ssize, sprot) = Split(arg);
        if (!TryParseValue(ssize, out ulong size) || size == 0)
        { Print("usage: .alloc <size> [prot=rwx]"); return; }
        uint prot = sprot.Length > 0 ? ParseProtection(sprot) : 0x40 /* PAGE_EXECUTE_READWRITE */;
        if (prot == 0) { Print("не разобрать защиту (rwx|rw|rx|r|ro|na или hex)"); return; }

        var baseAddr = Client.AllocMemory(pid, size, prot);
        if (baseAddr == null || baseAddr.Value == 0) { Err("AllocMemory FAIL"); return; }
        Ok($"{Kw("allocated")} {Hex(size)} bytes @ {Addr(baseAddr.Value)} (prot={Hex(prot)})");
    }

    private static void CmdFree(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        if (!TryParseValue(arg, out ulong addr)) { Print("usage: .free <addr>"); return; }
        if (Client.FreeMemory(pid, addr)) Ok($"{Kw("freed")} @ {Addr(addr)}");
        else Err("FreeMemory FAIL (невалидный base?)");
    }

    private static void CmdProtect(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, rest1) = Split(arg);
        var (ssize, sprot) = Split(rest1);
        if (saddr.Length == 0 || ssize.Length == 0 || sprot.Length == 0)
        { Print("usage: .protect <addr> <size> <prot>   (prot: rwx|rw|rx|r|ro|na или hex)"); return; }
        if (!TryParseValue(saddr, out ulong addr)) { Print($"не разобрать адрес '{saddr}'"); return; }
        if (!TryParseValue(ssize, out ulong size) || size == 0) { Print($"не разобрать размер '{ssize}'"); return; }
        uint prot = ParseProtection(sprot);
        if (prot == 0) { Print("не разобрать защиту"); return; }

        var old = Client.ProtectMemory(pid, addr, (uint)size, prot);
        if (old == null) { Err("ProtectMemory FAIL"); return; }
        Ok($"{Kw("protect")} {Addr(addr)} +{Hex(size)} -> {Hex(prot)} (было {Hex(old.Value)})");
    }

    /// <summary>Мнемоника защиты страниц → значение PAGE_*. 0 = ошибка разбора.</summary>
    private static uint ParseProtection(string s)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "na": case "noaccess":    return 0x01;  // PAGE_NOACCESS
            case "r":  case "ro":          return 0x02;  // PAGE_READONLY
            case "rw":                     return 0x04;  // PAGE_READWRITE
            case "rx":                     return 0x20;  // PAGE_EXECUTE_READ
            case "rwx": case "wrx":        return 0x40;  // PAGE_EXECUTE_READWRITE
            case "x":  case "xo":          return 0x10;  // PAGE_EXECUTE
        }
        return TryParseValue(s, out ulong v) ? (uint)v : 0;
    }

    private static void CmdDisasm(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;
        var (saddr, scount) = Split(arg);
        ulong addr;
        if (saddr.Length == 0)
        {
            lock (Sess.Lock) addr = Sess.LastRip;
            if (addr == 0) { Print("usage: u <addr> [count]"); return; }
        }
        else if (!TryParseValue(saddr, out addr)) { Print("usage: u <addr> [count]"); return; }

        int count = 16;
        if (scount.Length > 0 && int.TryParse(scount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            count = n;

        // Грубая оценка: средняя длина x64 инструкции ~5 байт, читаем с запасом
        uint size = (uint)(count * 12);
        var data = Client.ReadMemory(pid, addr, size);
        if (data == null) { Print("ReadMemory FAIL"); return; }

        bool is32; lock (Sess.Lock) is32 = Sess.Is32Bit;
        var insns = Disasm.Decode(data, addr, count, is32);
        ulong currentRip; lock (Sess.Lock) currentRip = Sess.LastRip;
        foreach (var line in insns)
        {
            string marker = (line.Addr == currentRip) ? Ansi.Wrap(Ansi.Red, "►") : " ";
            string addrStr = Ansi.Wrap(Ansi.Gray, FormatAddr(line.Addr, is32));
            string? addrLbl = Syms.Resolve(line.Addr);
            string lbl     = addrLbl != null ? "  " + Ansi.Wrap(Ansi.Yellow, addrLbl) : "";
            string hexStr  = Ansi.Wrap(Ansi.Dim, string.Join(' ', line.Bytes.Select(b => b.ToString("x2"))).PadRight(24));
            string asmStr  = Ansi.ColorizeAsmLine(line.Text);

            // Аннотация call/jmp/jcc target'а — после операндов в стиле «; module!func».
            string targetAnnot = "";
            if (line.BranchTarget is ulong tgt)
            {
                var tgtSym = Syms.Resolve(tgt);
                if (tgtSym != null) targetAnnot = "  " + Ansi.Wrap(Ansi.Green, "; " + tgtSym);
            }

            Console.WriteLine($" {marker} {addrStr}  {hexStr}  {asmStr}{targetAnnot}{lbl}");
        }
    }

    // ── Точки останова ──────────────────────────────────────────────────

    private static void CmdBp(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;

        // Синтаксис: `bp <addr>` или `bp <addr> if <expr>`.
        var (saddr, srest) = Split(arg);
        if (saddr.Length == 0) { Print("usage: bp <addr> [if <cond>]"); return; }
        if (!Evaluator.TryEval(saddr, out ulong addr))
        {
            Print($"bp: не смог разобрать адрес '{saddr}'. "
                + "Допустимы: 0x... | hex | reg | reg+off | module!sym | [mem]. "
                + "Для символов проверь что PDB загружен (см. лог `attach`/`open`).");
            return;
        }

        string? cond = null;
        if (srest.Length > 0)
        {
            var (kw, rest2) = Split(srest);
            if (kw.Equals("if", StringComparison.OrdinalIgnoreCase) && rest2.Length > 0)
                cond = rest2;
            else { Print("usage: bp <addr> [if <cond>]"); return; }
        }

        var handle = Client.SetBreakpoint(pid, 0, addr, 0);
        if (handle == null) { Err($"SetBreakpoint @ {Addr(addr)} FAIL"); return; }
        lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(handle.Value, addr, cond: cond));
        string? sym = Syms.Resolve(addr);
        string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
        if (cond == null)
            Ok($"{Kw("bp")} {Ansi.Wrap(Ansi.Red, "[" + handle.Value + "]")} {Addr(addr)}{symPart}");
        else
            Ok($"{Kw("bp")} {Ansi.Wrap(Ansi.Red, "[" + handle.Value + "]")} {Addr(addr)}{symPart}  "
              + Ansi.Wrap(Ansi.Magenta, "if ") + Ansi.Wrap(Ansi.Green, cond));
    }

    private static void CmdBpList()
    {
        lock (Sess.Lock)
        {
            if (Sess.Breakpoints.Count == 0) { Info("нет активных BP"); return; }
            Info($"{Num(Sess.Breakpoints.Count)} breakpoints:");
            foreach (var b in Sess.Breakpoints)
            {
                string tag = b.IsTemp ? Ansi.Wrap(Ansi.Dim, " [temp]") : "";
                string kind = b.Kind != "sw" ? " " + Ansi.Wrap(Ansi.Blue, "<" + b.Kind + ">") : "";
                string cond = b.Condition != null
                    ? "  " + Ansi.Wrap(Ansi.Magenta, "if ") + Ansi.Wrap(Ansi.Green, b.Condition)
                    : "";
                string? sym = Syms.Resolve(b.Addr);
                string symTail = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
                Console.WriteLine($"  {Ansi.Wrap(Ansi.Red, "[" + b.Handle + "]")}  "
                                + Ansi.Wrap(Ansi.Gray, FormatAddr(b.Addr, Sess.Is32Bit))
                                + kind + tag + symTail + cond);
            }
        }
    }

    private static void CmdBpClear(string arg)
    {
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            List<BpRec> all;
            lock (Sess.Lock) all = Sess.Breakpoints.ToList();
            foreach (var b in all) Client.RemoveBreakpoint(b.Handle);
            lock (Sess.Lock) Sess.Breakpoints.Clear();
            Ok($"{Kw("cleared")} {Num(all.Count)} BPs");
            return;
        }
        if (!TryParseValue(arg, out ulong v)) { Err("usage: bc <addr | handle | all>"); return; }
        lock (Sess.Lock)
        {
            int idx = Sess.Breakpoints.FindIndex(b => b.Handle == (uint)v || b.Addr == v);
            if (idx < 0) { Err($"BP не найдена: {Hex(v)}"); return; }
            var entry = Sess.Breakpoints[idx];
            Sess.Breakpoints.RemoveAt(idx);
            if (Client.RemoveBreakpoint(entry.Handle))
                Ok($"{Kw("removed")} BP {Ansi.Wrap(Ansi.Red, "[" + entry.Handle + "]")} {Addr(entry.Addr)}");
            else Err("RemoveBreakpoint FAIL");
        }
    }

    /// <summary>
    /// Аппаратная точка останова / watchpoint через DR0-3.
    /// Синтаксис в стиле WinDbg: `ba <e|r|w><len> <addr>`
    ///   e  — исполнение (execute), длина обычно 1
    ///   w  — срабатывание на запись
    ///   r  — срабатывание на чтение/запись
    /// len ∈ {1,2,4,8}. Драйвер ставит DR0-3 с нужным условием/длиной.
    /// </summary>
    private static void CmdBpHw(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;

        var (spec, saddr) = Split(arg);
        if (spec.Length < 2 || saddr.Length == 0)
        { Print("usage: ba <e|r|w><len> <addr>   (например: ba w4 401000)"); return; }

        char mode = char.ToLowerInvariant(spec[0]);
        if (!int.TryParse(spec[1..], out int len) || (len != 1 && len != 2 && len != 4 && len != 8))
        { Print("длина должна быть 1, 2, 4 или 8: ba <e|r|w><len> <addr>"); return; }

        uint type; string kind;
        switch (mode)
        {
            case 'e': type = KfBpType.Hardware;    kind = "hw-e";  break;
            case 'w': type = KfBpType.HwWrite;     kind = "hw-w";  break;
            case 'r': type = KfBpType.HwReadWrite; kind = "hw-rw"; break;
            default:  Print("режим должен быть e (execute), w (write) или r (read/write)"); return;
        }

        if (!Evaluator.TryEval(saddr, out ulong addr))
        { Print($"ba: не смог разобрать адрес '{saddr}'"); return; }

        var handle = Client.SetBreakpoint(pid, 0, addr, type, (uint)len);
        if (handle == null) { Err($"ba {spec} @ {Addr(addr)} FAIL (свободных DR нет?)"); return; }
        lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(handle.Value, addr, kind: kind));
        string? sym = Syms.Resolve(addr);
        string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
        Ok($"{Kw("ba")} {Ansi.Wrap(Ansi.Red, "[" + handle.Value + "]")} "
            + Ansi.Wrap(Ansi.Blue, "<" + kind + " len=" + len + ">") + $" {Addr(addr)}{symPart}");
    }

    /// <summary>Memory breakpoint (PAGE_GUARD): `bm <addr> [size=1]`.</summary>
    private static void CmdBpMem(string arg)
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid; lock (Sess.Lock) pid = Sess.TargetPid;
        if (!Require(pid != 0, "target не задан")) return;

        var (saddr, ssize) = Split(arg);
        if (saddr.Length == 0) { Print("usage: bm <addr> [size]"); return; }
        if (!Evaluator.TryEval(saddr, out ulong addr))
        { Print($"bm: не смог разобрать адрес '{saddr}'"); return; }

        uint size = 1;
        if (ssize.Length > 0 && Evaluator.TryEval(ssize, out ulong sz) && sz > 0) size = (uint)sz;

        var handle = Client.SetBreakpoint(pid, 0, addr, KfBpType.Memory, size);
        if (handle == null) { Err($"bm @ {Addr(addr)} FAIL"); return; }
        lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(handle.Value, addr, kind: "mem"));
        string? sym = Syms.Resolve(addr);
        string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
        Ok($"{Kw("bm")} {Ansi.Wrap(Ansi.Red, "[" + handle.Value + "]")} "
            + Ansi.Wrap(Ansi.Blue, "<mem size=" + size + ">") + $" {Addr(addr)}{symPart}");
    }

    // ── Execution ────────────────────────────────────────────────────────

    private static void CmdGo(string arg = "")
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; bool susp, is32;
        lock (Sess.Lock)
        {
            pid = Sess.TargetPid; tid = Sess.CurrentTid;
            susp = Sess.IsPausedViaSuspend; is32 = Sess.Is32Bit;
        }
        if (!Require(pid != 0, "target не задан")) return;

        // Run-to-cursor: `g <addr>` ставит временную SW BP на адрес и продолжает.
        // Снимется автоматически в OnDebugEvent (как temp у Step Over/Out).
        if (arg.Length > 0)
        {
            if (is32)
            {
                Print("g <addr> на WoW64 не поддержан (KdTrap не ловит 32-битные исключения). "
                    + "Поставь `bp <addr>`, затем `g`.");
                return;
            }
            if (!Evaluator.TryEval(arg, out ulong target))
            { Print($"g: не смог разобрать адрес '{arg}'"); return; }
            var h = Client.SetBreakpoint(pid, 0, target, KfBpType.Software);
            if (h == null) { Err($"g: SetBreakpoint @ {Addr(target)} FAIL"); return; }
            lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(h.Value, target, temp: true));
            string? rsym = Syms.Resolve(target);
            string rtail = rsym != null ? "  " + Ansi.Wrap(Ansi.Yellow, rsym) : "";
            Info($"{Kw("run to")} {Addr(target)}{rtail} (temp BP [{h.Value}])");
        }

        if (is32)
        {
            // WoW64: KdTrap-хук не ловит 32-битные исключения. Делаем EB-FE-Run:
            // конвертируем активные BP в спин-trap'ы, отпускаем все потоки,
            // поллим EIP до попадания в BP. Это блокирует REPL на время выполнения
            // (в UI это в background-задаче — для CLI пока синхронно).
            Wow64Run(pid);
            return;
        }

        if (susp)
        {
            if (!Client.ResumeThread(tid)) { Err("ResumeThread FAIL"); return; }
            lock (Sess.Lock) { Sess.IsBreak = false; Sess.IsPausedViaSuspend = false; }
            Ok($"{Kw("running")} (PID {Pid(pid)}, TID {Pid(tid)}, resumed from suspend)");
        }
        else
        {
            if (!Client.ContinueDebugEvent(ContinueMode.Run))
            { Err("ContinueDebugEvent FAIL (поток не в хуке — `interrupt`?)"); return; }
            lock (Sess.Lock) { Sess.IsBreak = false; Sess.IsPausedViaSuspend = false; }
            Ok($"{Kw("running")} (PID {Pid(pid)}, TID {Pid(tid)})");
        }
    }

    /// <summary>
    /// WoW64 Run: конвертирует активные SW BP в EB FE спин-trap'ы, отпускает все
    /// потоки процесса, поллит EIP каждого до попадания в один из адресов.
    /// Блокирующий (нет ответного KdTrap-event'а в WoW64). Таймаут — пока нет.
    /// Прерывание — Ctrl+C приведёт к завершению CLI; для штатного «paus» нужно
    /// дополнительно ставить флаг отмены через консольный handler (пока опускаю).
    /// </summary>
    private static void Wow64Run(uint pid)
    {
        List<BpRec> bpsSnap;
        lock (Sess.Lock) bpsSnap = Sess.Breakpoints.ToList();

        var threadsList = Client.EnumThreads(pid);
        if (threadsList.Count == 0) { Print("WoW64 Run: нет потоков"); return; }

        if (bpsSnap.Count == 0)
        {
            // Без BP — просто отпускаем потоки и говорим что отладчик «running».
            foreach (var t in threadsList) Client.ResumeThread(t.ThreadId);
            lock (Sess.Lock) { Sess.IsBreak = false; Sess.IsPausedViaSuspend = false; }
            Print("WoW64 Run: BP не заданы — потоки отпущены, REPL не отслеживает остановку. "
                + "Используй `interrupt` для ручной приостановки.");
            return;
        }

        // Перед записью EB FE прочитаем текущие байты (могут быть с 0xCC от прошлой
        // установки BP — релей патчит .text через CR0.WP, так что 0xCC реально лежит).
        var saved = new Dictionary<ulong, byte[]>();
        var spin = new byte[] { 0xEB, 0xFE };
        foreach (var b in bpsSnap)
        {
            var cur = Client.ReadMemory(pid, b.Addr, 2);
            if (cur == null || cur.Length < 2) continue;
            saved[b.Addr] = cur;
            Client.WriteMemory(pid, b.Addr, spin);
        }
        Print($"WoW64 Run: {saved.Count} BP -> EB FE trap, resuming...");

        foreach (var t in threadsList) Client.ResumeThread(t.ThreadId);
        lock (Sess.Lock) { Sess.IsBreak = false; Sess.IsPausedViaSuspend = false; }

        var targetSet = new HashSet<ulong>(bpsSnap.Select(b => b.Addr));
        ulong hitAddr = 0;
        uint hitTid = 0;
        // Поллим EIP всех потоков. Без явного таймаута — выходим только по попаданию
        // или если пользователь нажмёт Ctrl+C (тогда CLI завершится).
        while (true)
        {
            Thread.Sleep(50);
            foreach (var t in threadsList)
            {
                var regs = Client.ReadRegisters(pid, t.ThreadId);
                if (!regs.HasValue) continue;
                ulong eip = regs.Value.Rip & 0xFFFFFFFF;
                if (targetSet.Contains(eip))
                {
                    hitAddr = eip;
                    hitTid = t.ThreadId;
                    break;
                }
            }
            if (hitAddr != 0) break;
        }

        // Останавливаем все потоки, восстанавливаем байты.
        foreach (var t in threadsList) Client.SuspendThread(t.ThreadId);
        foreach (var kv in saved) Client.WriteMemory(pid, kv.Key, kv.Value);

        lock (Sess.Lock)
        {
            Sess.IsBreak = true;
            Sess.IsPausedViaSuspend = true;
            Sess.CurrentTid = hitTid;
            Sess.LastRip = hitAddr;
        }

        Console.WriteLine();
        Console.WriteLine($"*** BP (WoW64) at {FormatAddr(hitAddr, true)}  PID={pid} TID={hitTid}");
        var newRegs = Client.ReadRegisters(pid, hitTid);
        if (newRegs.HasValue)
        {
            var r = newRegs.Value;
            Console.WriteLine($"    EAX={(uint)r.Rax:X8}  EBX={(uint)r.Rbx:X8}  ECX={(uint)r.Rcx:X8}  EDX={(uint)r.Rdx:X8}");
            Console.WriteLine($"    EIP={(uint)r.Rip:X8}  ESP={(uint)r.Rsp:X8}  EBP={(uint)r.Rbp:X8}  EFLAGS={(uint)r.Rflags:X8}");
        }
    }

    private static void CmdStepInto()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; bool susp, is32;
        lock (Sess.Lock)
        {
            pid = Sess.TargetPid; tid = Sess.CurrentTid;
            susp = Sess.IsPausedViaSuspend; is32 = Sess.Is32Bit;
        }
        if (!Require(pid != 0, "target не задан")) return;

        // WoW64 (x86): KdTrap-хук не ловит исключения 32-битного кода, поэтому
        // классический Continue/SingleStep+TF не работает. Используем EB-FE-trap:
        // декодируем текущую инструкцию, ставим спин-петлю на следующий адрес,
        // отпускаем поток, ждём пока EIP его достигнет, suspend, восстанавливаем.
        if (is32)
        {
            if (!Wow64StepInto(pid, tid)) Print("WoW64 step into FAIL");
            return;
        }

        if (susp)
        {
            // Native x64 + suspend (после `open`): SingleStep + ResumeThread + Wait.
            // Состояние меняем только при УСПЕХЕ каждого шага, иначе prompt будет
            // показывать «run» с приостановленным потоком.
            if (!Client.SingleStep(pid, tid)) { Err("SingleStep IOCTL FAIL"); return; }
            if (!Client.ResumeThread(tid))    { Err("ResumeThread FAIL");    return; }
            lock (Sess.Lock) { Sess.IsBreak = false; Sess.IsPausedViaSuspend = false; }
            Ok($"{Kw("step into")} (suspend-path: TF set + ResumeThread)");
        }
        else
        {
            if (!Client.ContinueDebugEvent(ContinueMode.StepInto))
                Err("ContinueDebugEvent FAIL");
            else { lock (Sess.Lock) Sess.IsBreak = false; Ok(Kw("step into")); }
        }
    }

    private static void CmdStepOver()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; bool susp, is32;
        lock (Sess.Lock)
        {
            pid = Sess.TargetPid; tid = Sess.CurrentTid;
            susp = Sess.IsPausedViaSuspend; is32 = Sess.Is32Bit;
        }
        if (!Require(pid != 0, "target не задан")) return;

        // Получаем текущий IP и декодируем одну инструкцию.
        var regs = Client.ReadRegisters(pid, tid);
        if (regs == null) { Print("ReadRegisters FAIL"); return; }
        ulong ip = is32 ? (regs.Value.Rip & 0xFFFFFFFF) : regs.Value.Rip;
        var mem = Client.ReadMemory(pid, ip, 16);
        if (mem == null) { Print("ReadMemory(ip) FAIL"); return; }
        var info = Iced.Intel.FlowControl.Next;  // используется ниже
        var insn = Disasm.DecodeOne(mem, ip, is32);
        if (insn == null) { Print("Disasm: не удалось декодировать инструкцию"); return; }
        info = insn.Value.Flow;

        // Step Over — мы хотим оказаться на «следующей логической» инструкции
        // ПОСЛЕ исполнения текущей. Для большинства инструкций это IP + length.
        // Для условных ветвей (jcc) мы не знаем заранее куда пойдёт, поэтому
        // ставим target и сюда и в branch — wow64 spin или temp-BP сработает там,
        // где поток реально окажется.
        var targets = new List<ulong> { insn.Value.NextAddress };
        if (info == Iced.Intel.FlowControl.ConditionalBranch && insn.Value.BranchTarget is { } bt)
            targets.Add(bt);
        // Безусловный jmp с известным target — только туда, без NextAddress (он недостижим).
        if (info == Iced.Intel.FlowControl.UnconditionalBranch && insn.Value.BranchTarget is { } bt2)
            targets = new List<ulong> { bt2 };

        Ok($"{Kw("step over")}: {Ansi.Wrap(Ansi.Blue, insn.Value.Mnemonic)} @ {Addr(ip)} -> "
            + string.Join(", ", targets.Select(t => Addr(t))));

        if (is32)
        {
            if (!Wow64SpinStep(pid, tid, targets.ToArray()))
                Print("WoW64 step over FAIL");
            else
                ReportSuspendStop(pid, tid, "STEP-OVER (WoW64)");
            return;
        }

        // Native x64: ставим temp BP (флаг IsTemp=true) на каждый target.
        // После события listener вызовет ClearTempBreakpoints() и они снимутся
        // автоматически — пользователю их через `bl` будут видны как «[temp]».
        foreach (var addr in targets)
        {
            var h = Client.SetBreakpoint(pid, 0, addr, 0);
            if (h.HasValue)
                lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(h.Value, addr, temp: true));
        }

        lock (Sess.Lock) { Sess.IsBreak = false; }
        if (susp)
        {
            lock (Sess.Lock) Sess.IsPausedViaSuspend = false;
            Client.ResumeThread(tid);
        }
        else
        {
            Client.ContinueDebugEvent(ContinueMode.Run);
        }

        // Listener поймает событие → OnDebugEvent → ClearTempBreakpoints.
    }

    private static void CmdStepOut()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; bool susp, is32;
        lock (Sess.Lock)
        {
            pid = Sess.TargetPid; tid = Sess.CurrentTid;
            susp = Sess.IsPausedViaSuspend; is32 = Sess.Is32Bit;
        }
        if (!Require(pid != 0, "target не задан")) return;

        // Читаем return address с верха стека (RSP для x64, ESP для x86).
        var regs = Client.ReadRegisters(pid, tid);
        if (regs == null) { Print("ReadRegisters FAIL"); return; }
        ulong sp = is32 ? (regs.Value.Rsp & 0xFFFFFFFF) : regs.Value.Rsp;
        uint ptrSize = is32 ? 4u : 8u;
        var retData = Client.ReadMemory(pid, sp, ptrSize);
        if (retData == null || retData.Length < ptrSize)
        { Print($"ReadMemory({FormatAddr(sp, is32)}, {ptrSize}) FAIL"); return; }

        ulong retAddr = is32 ? BitConverter.ToUInt32(retData, 0)
                             : BitConverter.ToUInt64(retData, 0);
        Ok($"{Kw("step out")}: return-address @ [{Addr(sp)}] = {Addr(retAddr)}");

        if (is32)
        {
            if (!Wow64SpinStep(pid, tid, retAddr))
                Print("WoW64 step out FAIL");
            else
                ReportSuspendStop(pid, tid, "STEP-OUT (WoW64)");
            return;
        }

        // Native x64: temp BP на return + Continue/Resume — авто-снимется в OnDebugEvent.
        var h = Client.SetBreakpoint(pid, 0, retAddr, 0);
        if (h.HasValue) lock (Sess.Lock) Sess.Breakpoints.Add(new BpRec(h.Value, retAddr, temp: true));

        lock (Sess.Lock) Sess.IsBreak = false;
        if (susp)
        {
            lock (Sess.Lock) Sess.IsPausedViaSuspend = false;
            Client.ResumeThread(tid);
        }
        else
        {
            Client.ContinueDebugEvent(ContinueMode.Run);
        }
    }

    /// <summary>
    /// Печатает сводку «остановились на адресе X» — используется после WoW64-операций,
    /// у которых нет настоящего KdTrap-event'а (мы остановились через SuspendThread).
    /// </summary>
    private static void ReportSuspendStop(uint pid, uint tid, string kind)
    {
        // В WoW64 Wow64SpinStep сам восстанавливает байты, в Sess.Breakpoints temp-
        // записей он не делает. Но если пользователь смешал режимы (например
        // сделал StepOver в x64-target, потом attach к WoW64) — на всякий случай
        // чистим temp-записи и здесь тоже.
        ClearTempBreakpoints();

        var regs = Client.ReadRegisters(pid, tid);
        if (!regs.HasValue) return;
        var r = regs.Value;
        lock (Sess.Lock)
        {
            Sess.IsBreak = true; Sess.IsPausedViaSuspend = true;
            Sess.CurrentTid = tid; Sess.LastRip = r.Rip; Sess.LastRsp = r.Rsp; Sess.LastRegs = r;
        }
        bool is32; lock (Sess.Lock) is32 = Sess.Is32Bit;
        Console.WriteLine();
        string? sym = Syms.Resolve(r.Rip);
        string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
        Console.WriteLine($"*** {Ansi.Wrap(Ansi.Magenta, kind)} at {Ansi.Wrap(Ansi.Gray, FormatAddr(r.Rip, is32))}  "
                        + $"PID={Ansi.Wrap(Ansi.Cyan, pid.ToString())} "
                        + $"TID={Ansi.Wrap(Ansi.Cyan, tid.ToString())}{symPart}");
        PrintEventRegs(r, is32);
    }

    private static volatile bool _keeperRunning;
    private static Thread? _keeperThread;

    /// <summary>
    /// Каждые 2 секунды дёргает ReadRegisters на текущий TID. Это не для отображения,
    /// а чтобы kernel-MM держала kernel-stack потока резидентным — иначе SUSPENDED
    /// поток через 10–30 секунд получает page-out, и SingleStep/WriteRegisters
    /// начинают падать с STATUS_UNSUCCESSFUL.
    /// </summary>
    private static void StartTrapFrameKeeper()
    {
        if (_keeperRunning) return;
        _keeperRunning = true;
        _keeperThread = new Thread(() =>
        {
            while (_keeperRunning && Client.IsConnected)
            {
                Thread.Sleep(2000);
                uint pid, tid; bool active;
                lock (Sess.Lock)
                {
                    pid = Sess.TargetPid; tid = Sess.CurrentTid;
                    active = Sess.IsPausedViaSuspend && pid != 0 && tid != 0;
                }
                if (!active) continue;
                try
                {
                    var r = Client.ReadRegisters(pid, tid);
                    if (r.HasValue)
                        lock (Sess.Lock) Sess.LastRegs = r.Value;
                }
                catch { /* транзиентное — продолжаем */ }
            }
            _keeperRunning = false;
        }) { IsBackground = true, Name = "kf-trapframe-keeper" };
        _keeperThread.Start();
    }

    private static void StopTrapFrameKeeper() => _keeperRunning = false;

    /// <summary>Грузит PDB для всех модулей target'а через dbghelp.</summary>
    private static void LoadSymbolsForTarget(uint pid)
    {
        try
        {
            var mods = Client.EnumModules(pid);
            int ok = 0;
            foreach (var m in mods)
            {
                if (Syms.LoadModule(pid, m.Name, m.Base, m.Size)) ok++;
            }
            Print($"symbols: {ok}/{mods.Count} modules loaded (path: {Syms.SymbolPath})");
        }
        catch (Exception ex) { Print($"symbol load: {ex.Message}"); }
    }

    /// <summary>
    /// Снимает все BP, помеченные IsTemp=true (Step Over / Step Out / Run-to-Cursor).
    /// Вызывается из OnDebugEvent после получения события и из ReportSuspendStop
    /// после WoW64-операций — единая модель для x64 и WoW64.
    /// </summary>
    private static void ClearTempBreakpoints()
    {
        List<BpRec> temps;
        lock (Sess.Lock)
        {
            temps = Sess.Breakpoints.Where(b => b.IsTemp).ToList();
            Sess.Breakpoints.RemoveAll(b => b.IsTemp);
        }
        foreach (var b in temps) Client.RemoveBreakpoint(b.Handle);
    }

    // ── WoW64 helpers ────────────────────────────────────────────────────

    /// <summary>
    /// WoW64 шаг: декодирует инструкцию по EIP, ставит EB FE на её конец,
    /// отпускает поток, ждёт пока EIP туда придёт, suspend, восстанавливает байты.
    /// </summary>
    private static bool Wow64StepInto(uint pid, uint tid)
    {
        var regs = Client.ReadRegisters(pid, tid);
        if (regs == null) { Print("ReadRegisters FAIL"); return false; }
        ulong eip = regs.Value.Rip & 0xFFFFFFFF;

        // Читаем небольшое окно от EIP, декодируем одну инструкцию чтобы узнать её длину.
        var memBytes = Client.ReadMemory(pid, eip, 16);
        if (memBytes == null) { Print($"ReadMemory(eip={eip:X8}) FAIL"); return false; }
        var decoded = Disasm.Decode(memBytes, eip, 1, is32Bit: true);
        if (decoded.Count == 0) { Print("Disasm: не удалось декодировать инструкцию по EIP"); return false; }

        ulong nextEip = eip + (ulong)decoded[0].Bytes.Length;
        if (!Wow64SpinStep(pid, tid, nextEip)) return false;

        // После остановки — печатаем краткую сводку (полноценного KdTrap-event
        // не будет: мы остановились через SuspendThread, как при `open`).
        var newRegs = Client.ReadRegisters(pid, tid);
        if (newRegs.HasValue)
        {
            var r = newRegs.Value;
            lock (Sess.Lock) { Sess.IsBreak = true; Sess.IsPausedViaSuspend = true; Sess.LastRip = r.Rip; Sess.LastRsp = r.Rsp; Sess.LastRegs = r; }
            Console.WriteLine();
            Console.WriteLine($"*** STEP (WoW64) at {FormatAddr(r.Rip, true)}  PID={pid} TID={tid}");
            Console.WriteLine($"    EAX={(uint)r.Rax:X8}  EBX={(uint)r.Rbx:X8}  ECX={(uint)r.Rcx:X8}  EDX={(uint)r.Rdx:X8}");
            Console.WriteLine($"    EIP={(uint)r.Rip:X8}  ESP={(uint)r.Rsp:X8}  EBP={(uint)r.Rbp:X8}  EFLAGS={(uint)r.Rflags:X8}");
        }
        return true;
    }

    /// <summary>
    /// Общий helper: записывает EB FE по каждому из <paramref name="targetAddrs"/>,
    /// возобновляет поток, поллит EIP до попадания в один из них (таймаут 5с),
    /// останавливает поток и восстанавливает оригинальные байты.
    /// </summary>
    private static bool Wow64SpinStep(uint pid, uint tid, params ulong[] targetAddrs)
    {
        if (targetAddrs.Length == 0) return false;
        var spin = new byte[] { 0xEB, 0xFE };
        var saved = new Dictionary<ulong, byte[]>();

        foreach (var addr in targetAddrs)
        {
            var orig = Client.ReadMemory(pid, addr, 2);
            if (orig == null || orig.Length < 2)
            {
                Print($"WoW64: не смог прочитать байты по {FormatAddr(addr, true)}");
                foreach (var kv in saved) Client.WriteMemory(pid, kv.Key, kv.Value);
                return false;
            }
            saved[addr] = orig;
            if (!Client.WriteMemory(pid, addr, spin))
            {
                Print($"WoW64: не смог записать EB FE по {FormatAddr(addr, true)}");
                foreach (var kv in saved) Client.WriteMemory(pid, kv.Key, kv.Value);
                return false;
            }
        }

        Client.ResumeThread(tid);

        var targetSet = new HashSet<ulong>(targetAddrs);
        bool hit = false;
        for (int i = 0; i < 100; i++)   // до 5 секунд
        {
            Thread.Sleep(50);
            var regs = Client.ReadRegisters(pid, tid);
            if (regs.HasValue && targetSet.Contains(regs.Value.Rip & 0xFFFFFFFF))
            {
                hit = true;
                break;
            }
        }

        Client.SuspendThread(tid);
        foreach (var kv in saved) Client.WriteMemory(pid, kv.Key, kv.Value);

        if (!hit) Print("WoW64: таймаут 5с — EIP не достиг target");
        return hit;
    }

    private static void CmdSingleStep()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint pid, tid; lock (Sess.Lock) { pid = Sess.TargetPid; tid = Sess.CurrentTid; }
        if (!Require(pid != 0 && tid != 0, "target/TID не задан")) return;
        if (Client.SingleStep(pid, tid)) Ok($"{Kw("TF set")} on TID {Pid(tid)}");
        else Err("SingleStep IOCTL FAIL");
    }

    private static void CmdWait()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        Info("ожидание debug event'а (Ctrl+C для прерывания)...");
        var ev = Client.WaitDebugEvent();
        if (ev == null) { Err("WaitDebugEvent FAIL / cancelled"); return; }
        OnDebugEvent(ev.Value);
    }

    private static void CmdInterrupt()
    {
        if (!Require(Client.IsConnected, "не подключено")) return;
        uint tid; lock (Sess.Lock) tid = Sess.CurrentTid;
        if (!Require(tid != 0, "TID не задан")) return;
        if (Client.SuspendThread(tid)) Ok($"{Kw("suspended")} TID {Pid(tid)}");
        else Err("SuspendThread FAIL");
    }

    // ── Background event listener ────────────────────────────────────────

    private static void StartEventListener()
    {
        if (_eventListenerRunning) return;
        _eventListenerRunning = true;
        _eventListener = new Thread(EventListenerLoop) { IsBackground = true, Name = "kf-event-listener" };
        _eventListener.Start();
    }

    private static void StopEventListener()
    {
        _eventListenerRunning = false;
        // Не делаем Join — поток сидит в блокирующем WaitDebugEvent;
        // он завершится сам при разрыве соединения / disconnect.
    }

    private static void EventListenerLoop()
    {
        while (_eventListenerRunning && Client.IsConnected)
        {
            try
            {
                var ev = Client.WaitDebugEvent();
                if (ev == null) { Thread.Sleep(100); continue; }
                if (!_eventListenerRunning) break;
                OnDebugEvent(ev.Value);
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
        _eventListenerRunning = false;
    }

    private static void OnDebugEvent(KF_DEBUG_EVENT ev)
    {
        // Авто-очистка временных точек (Step Over / Step Out / Run-to-Cursor).
        // Снимаем ДО Sess-update, чтобы в Sess.Breakpoints остались только пользовательские.
        ClearTempBreakpoints();

        bool conditionFailed = false;
        BpRec? hitBp = null;
        lock (Sess.Lock)
        {
            Sess.IsBreak = true;
            Sess.IsPausedViaSuspend = false;
            Sess.CurrentTid = ev.ThreadId;
            Sess.LastRip = ev.Address;
            Sess.LastRsp = ev.Registers.Rsp;
            Sess.LastRegs = ev.Registers;

            // Условные BP: ищем активную точку по адресу события и проверяем условие.
            // Драйвер декрементит RIP к адресу INT3 сам, поэтому матчим напрямую.
            hitBp = Sess.Breakpoints.FirstOrDefault(b => b.Addr == ev.Address);
            if (hitBp != null && hitBp.Condition != null)
            {
                if (!Evaluator.TryEval(hitBp.Condition, out var cv) || cv == 0)
                    conditionFailed = true;
            }
        }

        if (conditionFailed)
        {
            // Условие не сработало — тихо отпускаем поток дальше, без вывода события.
            // Драйвер сам делает step-past 0xCC, поэтому ContinueDebugEvent(Run) достаточно.
            Client.ContinueDebugEvent(ContinueMode.Run);
            return;
        }
        string kind = ev.Type switch
        {
            DbgEventType.Breakpoint      => "BP",
            DbgEventType.SingleStep      => "STEP",
            DbgEventType.HwBreakpoint    => "HW-BP",
            DbgEventType.HwWatchpoint    => "WATCHPOINT",
            DbgEventType.MemoryBp        => "MEM-BP",
            DbgEventType.AccessViolation => $"AV (code=0x{ev.ExceptionCode:X8}, fault=0x{ev.FaultAddress:X16})",
            _ => $"type={ev.Type}",
        };
        Console.WriteLine();
        bool is32; lock (Sess.Lock) is32 = Sess.Is32Bit;
        var r = ev.Registers;
        string? sym = Syms.Resolve(ev.Address);
        string symPart = sym != null ? "  " + Ansi.Wrap(Ansi.Yellow, sym) : "";
        string kindColored = ev.Type == DbgEventType.AccessViolation
            ? Ansi.Wrap(Ansi.Red, kind)
            : Ansi.Wrap(Ansi.Magenta, kind);
        Console.WriteLine($"*** {kindColored} at {Ansi.Wrap(Ansi.Gray, FormatAddr(ev.Address, is32))}  "
                        + $"PID={Ansi.Wrap(Ansi.Cyan, ev.ProcessId.ToString())} "
                        + $"TID={Ansi.Wrap(Ansi.Cyan, ev.ThreadId.ToString())}{symPart}");
        PrintEventRegs(r, is32);
        Console.Write(Prompt());
    }

    private static void PrintEventRegs(KF_REGISTERS r, bool is32)
    {
        string Reg(string n, ulong v, bool w32)
        {
            string hex = w32 ? $"{(uint)v:X8}" : $"{v:X16}";
            return Ansi.Wrap(Ansi.Cyan, n) + "=" + Ansi.Wrap(Ansi.Orange, hex);
        }
        if (is32)
        {
            Console.WriteLine($"    {Reg("EAX", r.Rax, true)}  {Reg("EBX", r.Rbx, true)}  {Reg("ECX", r.Rcx, true)}  {Reg("EDX", r.Rdx, true)}");
            Console.WriteLine($"    {Reg("EIP", r.Rip, true)}  {Reg("ESP", r.Rsp, true)}  {Reg("EBP", r.Rbp, true)}  {Reg("EFLAGS", r.Rflags, true)}");
        }
        else
        {
            Console.WriteLine($"    {Reg("RAX", r.Rax, false)}  {Reg("RBX", r.Rbx, false)}  {Reg("RCX", r.Rcx, false)}  {Reg("RDX", r.Rdx, false)}");
            Console.WriteLine($"    {Reg("RIP", r.Rip, false)}  {Reg("RSP", r.Rsp, false)}  {Reg("RBP", r.Rbp, false)}  {Reg("RFLAGS", r.Rflags, false)}");
        }
    }

    // ── Парсер ───────────────────────────────────────────────────────────

    private static (string head, string rest) Split(string s)
    {
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        string head = s[..i];
        string rest = i < s.Length ? s[(i + 1)..].TrimStart() : "";
        return (head, rest);
    }

    /// <summary>
    /// Полный парсер: hex/decimal числа, регистры, [mem]-разыменование, символы
    /// (module!Name), арифметика. Это просто шорткат к Evaluator.TryEval.
    /// </summary>
    private static bool TryParseValue(string s, out ulong value)
        => Evaluator.TryEval(s, out value);

    private static void Print(string msg) => Console.WriteLine(msg);
}
