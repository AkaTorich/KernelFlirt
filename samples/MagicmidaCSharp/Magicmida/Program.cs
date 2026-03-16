using static Magicmida.NativeApi;

namespace Magicmida;

static class Program
{
    static void ConsoleLog(LogMsgType msgType, string msg)
    {
        string prefix = msgType switch
        {
            LogMsgType.Info => "Info",
            LogMsgType.Good => "Good",
            LogMsgType.Fatal => "Fatal",
            _ => "?"
        };
        Console.WriteLine($"[{prefix}] {msg}");
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "/unpack")
        {
            AttachConsole(unchecked((uint)-1) /*ATTACH_PARENT_PROCESS*/);

            Utils.Log = ConsoleLog;

            if (args.Length < 2)
            {
                Console.WriteLine($"Usage: {System.Reflection.Assembly.GetEntryAssembly()?.Location ?? ""} /unpack <filename>");
                Environment.Exit(1);
            }

            try
            {
#if CPUX86
                var dbg = new TTMDebugger(args[1], "", true);
#else
                var dbg = new TTMDebugger64(args[1], "", true);
#endif
                dbg.WaitFor();
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
