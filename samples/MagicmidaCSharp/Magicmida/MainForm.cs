using static Magicmida.NativeApi;

namespace Magicmida;

public class MainForm : Form
{
    private Button _btnUnpack = null!;
    private Button _btnShrink = null!;
    private Button _btnDumpProcess = null!;
    private CheckBox _cbDataSections = null!;
    private ListView _lv = null!;
    private ImageList _imageList = null!;
    private OpenFileDialog _od = null!;
    private ContextMenuStrip _pmRight = null!;

    public MainForm()
    {
        InitializeComponent();
        Utils.Log = GUILog;

#if CPUX64
        _btnDumpProcess.Visible = false;
        _btnShrink.Visible = false;
        _cbDataSections.Visible = false;
        Text += "64";
#endif
    }

    private void InitializeComponent()
    {
        Text = "Magicmida";
        ClientSize = new System.Drawing.Size(640, 400);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _imageList = new ImageList { ImageSize = new System.Drawing.Size(16, 16) };
        // 0=Info (blue), 1=Good (green), 2=Fatal (red)
        var bmpInfo = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmpInfo)) g.Clear(Color.DodgerBlue);
        _imageList.Images.Add(bmpInfo);
        var bmpGood = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmpGood)) g.Clear(Color.Green);
        _imageList.Images.Add(bmpGood);
        var bmpFatal = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmpFatal)) g.Clear(Color.Red);
        _imageList.Images.Add(bmpFatal);

        _lv = new ListView
        {
            Location = new System.Drawing.Point(8, 8),
            Size = new System.Drawing.Size(620, 320),
            View = View.Details,
            FullRowSelect = true,
            SmallImageList = _imageList,
            HeaderStyle = ColumnHeaderStyle.None
        };
        _lv.Columns.Add("Message", 600);

        _pmRight = new ContextMenuStrip();
        var miCopyLog = new ToolStripMenuItem("Copy Log");
        miCopyLog.Click += MiCopyLog_Click;
        _pmRight.Items.Add(miCopyLog);
        _lv.ContextMenuStrip = _pmRight;

        _btnUnpack = new Button
        {
            Text = "Unpack",
            Location = new System.Drawing.Point(8, 340),
            Size = new System.Drawing.Size(100, 30)
        };
        _btnUnpack.Click += BtnUnpack_Click;

        _btnShrink = new Button
        {
            Text = "Shrink",
            Location = new System.Drawing.Point(116, 340),
            Size = new System.Drawing.Size(100, 30)
        };
        _btnShrink.Click += BtnShrink_Click;

        var pmSections = new ContextMenuStrip();
        var miCreateSections = new ToolStripMenuItem("Create data sections");
        miCreateSections.Click += MiCreateSections_Click;
        pmSections.Items.Add(miCreateSections);

        _btnDumpProcess = new Button
        {
            Text = "Dump Process",
            Location = new System.Drawing.Point(224, 340),
            Size = new System.Drawing.Size(100, 30)
        };
        _btnDumpProcess.Click += BtnDumpProcess_Click;

        _cbDataSections = new CheckBox
        {
            Text = "Create data sections",
            Location = new System.Drawing.Point(340, 345),
            Size = new System.Drawing.Size(160, 20),
            ContextMenuStrip = pmSections
        };

        _od = new OpenFileDialog
        {
            Filter = "Executables (*.exe;*.dll)|*.exe;*.dll|All files (*.*)|*.*"
        };

        Controls.AddRange(new Control[] { _lv, _btnUnpack, _btnShrink, _btnDumpProcess, _cbDataSections });
    }

    private void GUILog(LogMsgType msgType, string msg)
    {
        if (_lv.InvokeRequired)
        {
            _lv.Invoke(new Action(() => GUILog(msgType, msg)));
            return;
        }

        var item = _lv.Items.Add(msg);
        item.ImageIndex = (int)msgType;
        item.EnsureVisible();
    }

    private void BtnUnpack_Click(object? sender, EventArgs e)
    {
        if (_od.ShowDialog() == DialogResult.OK)
        {
#if CPUX86
            var dbg = new TTMDebugger(_od.FileName, "", _cbDataSections.Checked);
            dbg.FreeOnTerminate = true;
#else
            var dbg = new TTMDebugger64(_od.FileName, "", _cbDataSections.Checked);
            dbg.FreeOnTerminate = true;
#endif
        }
    }

    private void BtnShrink_Click(object? sender, EventArgs e)
    {
#if CPUX86
        if (_od.ShowDialog() == DialogResult.OK)
        {
            var patcher = new Patcher(_od.FileName);
            patcher.ProcessShrink();
        }
#endif
    }

    private void MiCreateSections_Click(object? sender, EventArgs e)
    {
#if CPUX86
        if (_od.ShowDialog() == DialogResult.OK)
        {
            var patcher = new Patcher(_od.FileName);
            try { patcher.ProcessMkData(); }
            catch (Exception ex) { Utils.Log?.Invoke(LogMsgType.Fatal, ex.Message); }
        }
#endif
    }

    private void BtnDumpProcess_Click(object? sender, EventArgs e)
    {
#if CPUX86
        string pidInput = Microsoft.VisualBasic.Interaction.InputBox("PID:", "Dump Olly Process", "");
        if (string.IsNullOrEmpty(pidInput)) return;

        int pid = int.Parse(pidInput);
        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)pid);
        if (hProcess == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();

        if (_od.ShowDialog() == DialogResult.OK)
        {
            var patcher = new Patcher(_od.FileName);
            try { patcher.DumpProcessCode(hProcess); }
            finally { CloseHandle(hProcess); }
        }
        else
            CloseHandle(hProcess);
#endif
    }

    private void MiCopyLog_Click(object? sender, EventArgs e)
    {
        var lines = new System.Text.StringBuilder();
        foreach (ListViewItem item in _lv.Items)
            lines.AppendLine(item.Text);

        if (lines.Length > 0)
            Clipboard.SetText(lines.ToString());
    }
}
