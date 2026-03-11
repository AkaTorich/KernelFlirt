using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        VM.Instructions.CollectionChanged += (_, _) => RefreshDisasmView();
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HexData))
                UpdateHexDumpDisplay();
        };
    }

    private void RefreshDisasmView()
    {
        var rip = VM.Registers.FirstOrDefault(r => r.Name == "RIP")?.Value;
        DisasmControl.SetInstructions(VM.Instructions, rip);
    }

    /* ================================================================== */
    /*  Menu / Toolbar handlers                                            */
    /* ================================================================== */

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show("KernelFlirt - Kernel Debugger", "About", MessageBoxButton.OK);

    private void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        VM.RefreshModules();
        VM.RefreshKernelModules();
        VM.RefreshThreads();
        VM.RefreshRegisters();
        VM.RefreshDisassembly();
        VM.RefreshStack();
        VM.RefreshCallStack();
        VM.RefreshHexDump();
        VM.RefreshImports();
        UpdateHexDumpDisplay();
    }

    private void OnRefreshSehClick(object sender, RoutedEventArgs e)
    {
        VM.RefreshSehChain();
    }

    private void OnGoToClick(object sender, RoutedEventArgs e)
    {
        VM.GoToAddressCommand.Execute(GoToBox.Text);
    }

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.GoToAddressCommand.Execute(GoToBox.Text);
            e.Handled = true;
        }
    }

    private void OnHexGoClick(object sender, RoutedEventArgs e)
    {
        VM.GoToHexAddressCommand.Execute(HexAddrBox.Text);
        UpdateHexDumpDisplay();
    }

    private void OnHexAddrKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.GoToHexAddressCommand.Execute(HexAddrBox.Text);
            UpdateHexDumpDisplay();
            e.Handled = true;
        }
    }

    /* ================================================================== */
    /*  Module context menu                                                */
    /* ================================================================== */

    private void OnModuleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ModuleInfo module)
        {
            VM.DisasmAddress = VM.ResolveEntryPoint(module.BaseAddress);
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnModuleFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.FollowInDisasmCommand.Execute(mod.BaseAddress);
    }

    private void OnModuleFollowInDump(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
        {
            VM.FollowInDumpCommand.Execute(mod.BaseAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnModuleCopyBase(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            Clipboard.SetText($"{mod.BaseAddress:X16}");
    }

    private void OnModuleShowImports(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.RefreshImports(mod.BaseAddress);
    }

    /* ================================================================== */
    /*  Thread context menu                                                */
    /* ================================================================== */

    private void OnThreadDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is ThreadInfo thread)
            VM.SwitchThreadCommand.Execute(thread.ThreadId);
    }

    private void OnThreadSwitch(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.SwitchThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadSuspend(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.SuspendThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadResume(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.ResumeThreadCommand.Execute(t.ThreadId);
    }

    private void OnThreadFollowStart(object sender, RoutedEventArgs e)
    {
        if (ThreadsGrid.SelectedItem is ThreadInfo t)
            VM.FollowInDisasmCommand.Execute(t.StartAddress);
    }

    /* ================================================================== */
    /*  Register context menu                                              */
    /* ================================================================== */

    private void OnRegisterFollowInDump(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
        {
            VM.FollowInDumpCommand.Execute(reg.Value);
            UpdateHexDumpDisplay();
        }
    }

    private void OnRegisterFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (RegistersGrid.SelectedItem is Register reg)
            VM.FollowInDisasmCommand.Execute(reg.Value);
    }

    /* ================================================================== */
    /*  Stack context menu                                                 */
    /* ================================================================== */

    private void OnStackFollowInDump(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is string entry)
        {
            ulong addr = ParseStackValue(entry);
            if (addr != 0)
            {
                VM.FollowInDumpCommand.Execute(addr);
                UpdateHexDumpDisplay();
            }
        }
    }

    private void OnStackFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is string entry)
        {
            ulong addr = ParseStackValue(entry);
            if (addr != 0)
                VM.FollowInDisasmCommand.Execute(addr);
        }
    }

    private void OnStackCopy(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is string entry)
            Clipboard.SetText(entry);
    }

    private static ulong ParseStackValue(string entry)
    {
        var parts = entry.Split("  ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            ulong.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var val))
            return val;
        return 0;
    }

    /* ================================================================== */
    /*  Breakpoint context menu                                            */
    /* ================================================================== */

    private void OnBreakpointDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.DisasmAddress = bp.Address;
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnBreakpointGoTo(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.DisasmAddress = bp.Address;
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnBreakpointFollowInDump(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.FollowInDumpCommand.Execute(bp.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnBreakpointRemove(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
        {
            VM.SelectedDisasmAddress = bp.Address;
            VM.ToggleBreakpointCommand.Execute(null);
        }
    }

    /* ================================================================== */
    /*  Call Stack context menu                                             */
    /* ================================================================== */

    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            VM.FollowInDisasmCommand.Execute(frame.ReturnAddress);
    }

    private void OnCallStackFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            VM.FollowInDisasmCommand.Execute(frame.ReturnAddress);
    }

    private void OnCallStackFollowDump(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
        {
            VM.FollowInDumpCommand.Execute(frame.StackAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnCallStackCopy(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame frame)
            Clipboard.SetText($"{frame.ReturnAddressHex} {frame.Symbol}");
    }

    /* ================================================================== */
    /*  Bookmark context menu                                              */
    /* ================================================================== */

    private void OnBookmarkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarksGrid.SelectedItem is Bookmark bm)
            VM.GoToBookmarkCommand.Execute(bm);
    }

    private void OnBookmarkGoTo(object sender, RoutedEventArgs e)
    {
        if (BookmarksGrid.SelectedItem is Bookmark bm)
            VM.GoToBookmarkCommand.Execute(bm);
    }

    private void OnBookmarkFollowDump(object sender, RoutedEventArgs e)
    {
        if (BookmarksGrid.SelectedItem is Bookmark bm)
        {
            VM.FollowInDumpCommand.Execute(bm.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnBookmarkRemove(object sender, RoutedEventArgs e)
    {
        if (BookmarksGrid.SelectedItem is Bookmark bm)
            VM.RemoveBookmarkCommand.Execute(bm);
    }

    /* ================================================================== */
    /*  Patches context menu                                               */
    /* ================================================================== */

    private void OnPatchRestore(object sender, RoutedEventArgs e)
    {
        if (PatchesGrid.SelectedItem is Patch p)
            VM.RestorePatchCommand.Execute(p);
    }

    private void OnPatchGoTo(object sender, RoutedEventArgs e)
    {
        if (PatchesGrid.SelectedItem is Patch p)
            VM.FollowInDisasmCommand.Execute(p.Address);
    }

    /* ================================================================== */
    /*  Search context menu                                                */
    /* ================================================================== */

    private void OnSearchResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            VM.FollowInDisasmCommand.Execute(sr.Address);
    }

    private void OnSearchFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            VM.FollowInDisasmCommand.Execute(sr.Address);
    }

    private void OnSearchFollowDump(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
        {
            VM.FollowInDumpCommand.Execute(sr.Address);
            UpdateHexDumpDisplay();
        }
    }

    private void OnSearchSetBp(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
        {
            VM.SelectedDisasmAddress = sr.Address;
            VM.ToggleBreakpointCommand.Execute(null);
        }
    }

    /* ================================================================== */
    /*  Hex dump context menu                                              */
    /* ================================================================== */

    private void OnHexCopy(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(HexDumpText.Text))
            Clipboard.SetText(HexDumpText.Text);
    }

    private void OnHexFollowInDisasm(object sender, RoutedEventArgs e)
    {
        VM.FollowInDisasmCommand.Execute(VM.HexAddress);
    }

    /* ================================================================== */
    /*  Imports context menu                                               */
    /* ================================================================== */

    private void OnImportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
    }

    private void OnImportFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
    }

    private void OnImportFollowDump(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDumpCommand.Execute(imp.IatAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnImportSetBp(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.SelectedDisasmAddress = imp.ResolvedAddress;
            VM.ToggleBreakpointCommand.Execute(null);
        }
    }

    private void OnImportCopy(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            Clipboard.SetText($"{imp.Module}!{imp.Display} IAT={imp.IatHex} -> {imp.ResolvedHex}");
    }

    /* ================================================================== */
    /*  Log context menu                                                   */
    /* ================================================================== */

    private void OnLogCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var msg in VM.LogMessages)
            sb.AppendLine(msg);
        Clipboard.SetText(sb.ToString());
    }

    private void OnLogClear(object sender, RoutedEventArgs e)
    {
        VM.LogMessages.Clear();
    }

    /* ================================================================== */
    /*  Hex dump display                                                   */
    /* ================================================================== */

    private void UpdateHexDumpDisplay()
    {
        var data = VM.HexData;
        if (data == null || data.Length == 0)
        {
            HexDumpText.Text = "";
            return;
        }

        var sb = new StringBuilder();
        ulong baseAddr = VM.HexAddress;

        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"{baseAddr + (ulong)i:X16}  ");

            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }

            sb.Append(' ');

            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        HexDumpText.Text = sb.ToString();
    }

    protected override void OnClosed(EventArgs e)
    {
        VM.Dispose();
        base.OnClosed(e);
    }
}
