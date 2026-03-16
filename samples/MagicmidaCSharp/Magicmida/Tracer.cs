using static Magicmida.NativeApi;

namespace Magicmida;

public delegate bool TracePredicate(Tracer tracer, ref CONTEXT context);

public class Tracer
{
    private readonly uint _processId;
    private readonly uint _threadId;
    private readonly IntPtr _threadHandle;
    private readonly TracePredicate _predicate;
    private readonly LogProc _log;

    private uint _counter;
    private uint _limit;
    private bool _limitReached;
    private nuint _startAddress;

    public Tracer(uint processId, uint threadId, IntPtr threadHandle, TracePredicate predicate, LogProc log)
    {
        _processId = processId;
        _threadId = threadId;
        _threadHandle = threadHandle;
        _predicate = predicate;
        _log = log;
    }

    public nuint StartAddress => _startAddress;
    public uint Counter => _counter;
    public bool LimitReached => _limitReached;

    public void Trace(nuint address, uint limit)
    {
        _counter = 0;
        _limit = limit;
        _limitReached = false;
        _startAddress = address;

        var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
        if (!GetThreadContext(_threadHandle, ref c))
            throw new System.ComponentModel.Win32Exception();

        c.IP = address;
        c.EFlags |= 0x100; // Trap flag
        if (!SetThreadContext(_threadHandle, ref c))
            throw new System.ComponentModel.Win32Exception();

        if (!ContinueDebugEvent(_processId, _threadId, DBG_CONTINUE))
            return;

        while (WaitForDebugEvent(out var ev, INFINITE))
        {
            if (ev.dwThreadId != _threadId)
            {
                _log(LogMsgType.Info, $"Suspending spurious thread {ev.dwThreadId}");
                var hThread = OpenThread(2, false, ev.dwThreadId); // THREAD_SUSPEND_RESUME
                if (hThread != IntPtr.Zero && hThread != (IntPtr)(-1))
                {
                    SuspendThread(hThread);
                    CloseHandle(hThread);
                }
                ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, DBG_CONTINUE);
                continue;
            }

            uint status;
            if (ev.dwDebugEventCode == EXCEPTION_DEBUG_EVENT)
            {
                if (ev.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_SINGLE_STEP)
                {
                    status = OnSingleStep(ref ev);
                    if (status == DBG_CONTROL_BREAK)
                        break;
                }
                else
                {
                    _log(LogMsgType.Fatal, $"Unexpected exception during tracing: {ev.Exception.ExceptionRecord.ExceptionCode:X8} at {ev.Exception.ExceptionRecord.ExceptionAddress} in thread {ev.dwThreadId}");
                    return;
                }
            }
            else
            {
                status = DBG_CONTINUE;
            }

            ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, status);
        }
    }

    private uint OnSingleStep(ref DEBUG_EVENT ev)
    {
        _counter++;
        if (_limit != 0 && _counter > _limit)
        {
            _limitReached = true;
            _log(LogMsgType.Info, "Giving up trace due to instruction limit");
            return DBG_CONTROL_BREAK;
        }

        var c = new CONTEXT { ContextFlags = CONTEXT_CONTROL };
        if (!GetThreadContext(_threadHandle, ref c))
            throw new System.ComponentModel.Win32Exception();

        uint result;
        if (_predicate(this, ref c))
            result = DBG_CONTROL_BREAK;
        else
            result = DBG_CONTINUE;

        c.EFlags |= 0x100;
        if (!SetThreadContext(_threadHandle, ref c))
            throw new System.ComponentModel.Win32Exception();

        return result;
    }
}
