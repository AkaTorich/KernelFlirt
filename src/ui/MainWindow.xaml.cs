using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private readonly List<ContentControl> _pluginWrappers = [];

    public MainWindow()
    {
        InitializeComponent();
        LoadDecompilerHighlighting();
        if (VM.ThemeColors.Count > 0)
            ApplyThemeColors(VM.ThemeColors);
        VM.Instructions.CollectionChanged += (_, _) => RefreshDisasmView();
        VM.BreakpointMarkersChanged += () =>
        {
            ImportsGrid.Items.Refresh();
            FunctionsGrid.Items.Refresh();
            SearchGrid.Items.Refresh();
            ExceptionsGrid.Items.Refresh();
            SectionsGrid.Items.Refresh();
        };
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HexData))
                UpdateHexDumpDisplay();
            if (e.PropertyName == nameof(MainViewModel.IsDecompiling) && VM.IsDecompiling)
                MainTabControl.SelectedItem = DecompilerTab;
            if (e.PropertyName == nameof(MainViewModel.DecompiledCode))
                UpdateDecompilerText();
        };

        // Plugin UI integration
        VM.AddPluginMenuItem = (header, callback) =>
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => callback();
            PluginsMenu.Items.Add(item);
        };
        VM.AddPluginToolPanel = (title, content) =>
        {
            var wrapper = new ContentControl { Content = content };
            ApplyPluginResources(wrapper);
            _pluginWrappers.Add(wrapper);
            var tab = new TabItem { Header = title, Content = wrapper };
            MainTabControl.Items.Insert(MainTabControl.Items.Count - 1, tab); // Before Log tab
        };
        VM.LoadPlugins();

        // Re-apply tab colors now that plugin tabs exist
        if (VM.ThemeColors.Count > 0)
            ApplyTabColors(VM.ThemeColors);

        // Add Settings item to Plugins menu
        if (PluginsMenu.Items.Count > 0)
            PluginsMenu.Items.Insert(0, new Separator());
        var settingsItem = new MenuItem { Header = "_Settings..." };
        settingsItem.Click += (_, _) =>
        {
            var win = new PluginSettingsWindow(VM.PluginManager) { Owner = this };
            win.ShowDialog();
        };
        PluginsMenu.Items.Insert(0, settingsItem);
    }

    private void LoadDecompilerHighlighting()
    {
        var asm = typeof(MainWindow).Assembly;
        using var stream = asm.GetManifestResourceStream("KernelFlirt.UI.Themes.CDecompiler.xshd");
        if (stream != null)
        {
            using var reader = new XmlTextReader(stream);
            DecompilerOutput.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        DecompilerOutput.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));

        // Context menu
        var ctx = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copyItem.Click += (_, _) => DecompilerOutput.Copy();
        var selectAllItem = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
        selectAllItem.Click += (_, _) => DecompilerOutput.SelectAll();
        var copyAllItem = new MenuItem { Header = "Copy All" };
        copyAllItem.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(VM.DecompiledCode))
                Clipboard.SetText(VM.DecompiledCode);
        };
        ctx.Items.Add(copyItem);
        ctx.Items.Add(selectAllItem);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(copyAllItem);
        DecompilerOutput.ContextMenu = ctx;
    }

    private void UpdateDecompilerText()
    {
        DecompilerOutput.Text = VM.DecompiledCode ?? "";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 || e.SystemKey == Key.F2)
        {
            ulong addr = GetSelectedAddressFromActiveTab();
            if (addr != 0)
            {
                VM.SetBreakpointAtAddress(addr);
                e.Handled = true;
                return;
            }
            // Disassembly tab: use the standard command
            VM.ToggleBreakpointCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private WindowState _preFullscreenState;
    private WindowStyle _preFullscreenStyle;
    private bool _isFullscreen;

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = _preFullscreenStyle;
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }
        else
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void RefreshDisasmView()
    {
        var rip = VM.Registers.FirstOrDefault(r => r.Name == VM.IpRegName)?.Value;
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

    private void OnModuleShowFunctions(object sender, RoutedEventArgs e)
    {
        if (ModulesGrid.SelectedItem is ModuleInfo mod)
            VM.RefreshFunctionsForModule(mod.BaseAddress, mod.Name);
    }

    /* ================================================================== */
    /*  Kernel module context menu                                         */
    /* ================================================================== */

    private void OnKernelModuleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is KernelModuleInfo mod)
            NavigateToKernelModule(mod);
    }

    private void OnKernelModuleGoToEntry(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            NavigateToKernelModule(mod);
    }

    private void OnKernelModuleGoToBase(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
        {
            VM.DisasmAddress = mod.BaseAddress;
            VM.TargetPid = 4; // kernel PID for memory reads
            VM.RefreshDisassembly();
            RefreshDisasmView();
        }
    }

    private void OnKernelModuleFollowInDump(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
        {
            VM.HexAddress = mod.BaseAddress;
            VM.TargetPid = 4;
            VM.RefreshHexDump();
        }
    }

    private void OnKernelModuleCopyBase(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            Clipboard.SetText($"{mod.BaseAddress:X16}");
    }

    private void OnKernelModuleCopyName(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            Clipboard.SetText(mod.Name);
    }

    private void OnKernelModuleShowImports(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            VM.RefreshImports(mod.BaseAddress, 4);
    }

    private void OnKernelModuleShowFunctions(object sender, RoutedEventArgs e)
    {
        if (KernelModulesGrid.SelectedItem is KernelModuleInfo mod)
            VM.RefreshFunctionsForModule(mod.BaseAddress, mod.Name);
    }

    private void NavigateToKernelModule(KernelModuleInfo mod)
    {
        // Read PE entry point from kernel memory (PID 4)
        var ep = VM.ResolveKernelEntryPoint(mod.BaseAddress);
        VM.DisasmAddress = ep;
        VM.TargetPid = 4;
        VM.RefreshDisassembly();
        RefreshDisasmView();
        VM.Log($"Kernel module: {mod.Name} entry at {ep:X16}");
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
        if (StackList.SelectedItem is Models.StackEntry entry)
        {
            ulong addr = ParseStackAddress(entry.Address);
            if (addr != 0)
            {
                VM.FollowInDumpCommand.Execute(addr);
                UpdateHexDumpDisplay();
            }
        }
    }

    private void OnStackFollowInDisasm(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is Models.StackEntry entry)
        {
            ulong addr = ParseStackAddress(entry.Address);
            if (addr != 0)
                VM.FollowInDisasmCommand.Execute(addr);
        }
    }

    private void OnStackCopy(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedItem is Models.StackEntry entry)
            Clipboard.SetText(entry.ToString());
    }

    private static ulong ParseStackAddress(string hexAddr)
    {
        if (ulong.TryParse(hexAddr.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var val))
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
    /*  Hex dump — now handled by HexDumpView control                       */
    /* ================================================================== */

    /* ================================================================== */
    /*  Shared: right-click selects DataGrid row under cursor              */
    /* ================================================================== */

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dg) return;
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            dg.SelectedItem = row.Item;
    }

    /* ================================================================== */
    /*  Imports context menu                                               */
    /* ================================================================== */

    private void OnImportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnImportFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDisasmCommand.Execute(imp.ResolvedAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnImportFollowDump(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
        {
            VM.FollowInDumpCommand.Execute(imp.IatAddress);
            UpdateHexDumpDisplay();
        }
    }

    private void OnBpButtonClick(object sender, RoutedEventArgs e)
    {
        ulong addr = GetSelectedAddressFromActiveTab();
        if (addr != 0)
            VM.SetBreakpointAtAddress(addr);
        else
            VM.ToggleBreakpointCommand.Execute(null);
    }

    private ulong GetSelectedAddressFromActiveTab()
    {
        var tab = MainTabControl.SelectedItem as TabItem;
        if (tab == null) return 0;
        var header = tab.Header?.ToString();
        return header switch
        {
            "Imports" => (ImportsGrid.SelectedItem as ImportEntry)?.ResolvedAddress ?? 0,
            "Functions" => (FunctionsGrid.SelectedItem as FunctionEntry)?.Address ?? 0,
            "Search" => (SearchGrid.SelectedItem as SearchResult)?.Address ?? 0,
            "Exceptions" => (ExceptionsGrid.SelectedItem as ExceptionEntry)?.FunctionStart ?? 0,
            _ => 0
        };
    }

    private void OnImportSetBp(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            VM.SetBreakpointAtAddress(imp.ResolvedAddress);
    }

    private void OnImportCopy(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            Clipboard.SetText($"{imp.Module}!{imp.Display} IAT={imp.IatHex} -> {imp.ResolvedHex}");
    }

    /* ================================================================== */
    /*  Functions tab                                                       */
    /* ================================================================== */

    private void OnFunctionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
        {
            VM.FollowInDisasmCommand.Execute(fn.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnFunctionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
        {
            VM.FollowInDisasmCommand.Execute(fn.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnFunctionSetBp(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            VM.SetBreakpointAtAddress(fn.Address);
    }

    private void OnFunctionCopy(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            Clipboard.SetText($"{fn.Name} {fn.AddressHex}");
    }

    private void OnFunctionDecompile(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is FunctionEntry fn)
            DecompileAddress(fn.Address, fn.Size);
    }

    private void OnImportDecompile(object sender, RoutedEventArgs e)
    {
        if (ImportsGrid.SelectedItem is ImportEntry imp)
            DecompileAddress(imp.ResolvedAddress);
    }

    private void OnExceptionDecompile(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            DecompileAddress(ex.FunctionStart, (uint)(ex.FunctionEnd - ex.FunctionStart));
    }

    private void OnCallStackDecompile(object sender, RoutedEventArgs e)
    {
        if (CallStackGrid.SelectedItem is CallStackFrame f)
            DecompileAddress(f.ReturnAddress);
    }

    private void OnSearchDecompile(object sender, RoutedEventArgs e)
    {
        if (SearchGrid.SelectedItem is SearchResult sr)
            DecompileAddress(sr.Address);
    }

    private void OnBreakpointDecompile(object sender, RoutedEventArgs e)
    {
        if (BpGrid.SelectedItem is Breakpoint bp)
            DecompileAddress(bp.Address);
    }

    private void DecompileAddress(ulong address, uint size = 0)
    {
        if (address == 0) return;
        VM.DecompileFunction(address, size);
        MainTabControl.SelectedItem = DecompilerTab;
    }

    /* ================================================================== */
    /*  Exceptions tab                                                     */
    /* ================================================================== */

    private void OnExceptionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            VM.FollowInDisasmCommand.Execute(ex.FunctionStart);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            VM.FollowInDisasmCommand.Execute(ex.FunctionStart);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowEnd(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
        {
            // Go to last instruction (end - 1 byte, typically ret)
            VM.FollowInDisasmCommand.Execute(ex.FunctionEnd > 0 ? ex.FunctionEnd - 1 : ex.FunctionEnd);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnExceptionFollowDump(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.FollowInDumpCommand.Execute(ex.FunctionStart);
    }

    private void OnExceptionSetBp(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.SetBreakpointAtAddress(ex.FunctionStart);
    }

    private void OnExceptionSetBpEnd(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex && ex.FunctionEnd > 0)
            VM.SetBreakpointAtAddress(ex.FunctionEnd - 1);
    }

    private void OnExceptionCopy(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText(ex.StartHex);
    }

    private void OnExceptionCopyName(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText(ex.Display);
    }

    private void OnExceptionCopyLine(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            Clipboard.SetText($"{ex.ModuleName}\t{ex.Display}\t{ex.StartHex}\t{ex.EndHex}\t{ex.SizeHex}");
    }

    private void OnExceptionShowUnwind(object sender, RoutedEventArgs e)
    {
        if (ExceptionsGrid.SelectedItem is ExceptionEntry ex)
            VM.ShowUnwindInfo(ex);
    }

    /* ================================================================== */
    /*  Sections tab handlers                                              */
    /* ================================================================== */

    private void OnSectionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            VM.FollowInDisasmCommand.Execute(sec.VirtualAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnSectionFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            VM.FollowInDisasmCommand.Execute(sec.VirtualAddress);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnSectionFollowDump(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.FollowInDumpCommand.Execute(sec.VirtualAddress);
    }

    private void OnSectionMemoryBpAll(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            // Set PAGE_GUARD on every page in the section
            uint size = sec.VirtualSize > 0 ? sec.VirtualSize : sec.RawDataSize;
            if (size == 0) size = 0x1000;
            uint pageCount = (size + 0xFFF) / 0x1000;
            for (uint i = 0; i < pageCount; i++)
            {
                ulong pageAddr = sec.VirtualAddress + i * 0x1000;
                VM.SetBreakpointAtAddressWithType(pageAddr, Models.BreakpointType.Memory);
            }
        }
    }

    private void OnSectionDumpToFile(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.DumpSectionToFile(sec);
    }

    private void OnSectionFillNops(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            var result = MessageBox.Show(
                $"Fill {sec.ModuleName}:{sec.Name} ({sec.VirtualSizeHex}) with NOPs (0x90)?\n\nThis is destructive and cannot be undone!",
                "Fill Section", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                VM.FillSection(sec, 0x90);
        }
    }

    private void OnSectionFillZeros(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
        {
            var result = MessageBox.Show(
                $"Fill {sec.ModuleName}:{sec.Name} ({sec.VirtualSizeHex}) with zeros?\n\nThis is destructive and cannot be undone!",
                "Fill Section", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                VM.FillSection(sec, 0x00);
        }
    }

    private void OnSectionSearchBinary(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.SearchBinaryInSection(sec);
    }

    private void OnSectionSearchString(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            VM.SearchStringInSection(sec);
    }

    private void OnSectionCopyAddress(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText(sec.VaHex);
    }

    private void OnSectionCopyName(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText($"{sec.ModuleName}:{sec.Name}");
    }

    private void OnSectionCopyLine(object sender, RoutedEventArgs e)
    {
        if (SectionsGrid.SelectedItem is SectionEntry sec)
            Clipboard.SetText($"{sec.ModuleName}\t{sec.Name}\t{sec.VaHex}\t{sec.VirtualSizeHex}\t{sec.RawSizeHex}\t{sec.CharacteristicsHex}\t{sec.Flags}");
    }

    /* ================================================================== */
    /*  Strings tab                                                        */
    /* ================================================================== */

    private void OnStringDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
        {
            VM.FollowInDisasmCommand.Execute(str.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnStringFollowDisasm(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
        {
            VM.FollowInDisasmCommand.Execute(str.Address);
            MainTabControl.SelectedIndex = 0;
        }
    }

    private void OnStringFollowDump(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            VM.FollowInDumpCommand.Execute(str.Address);
    }

    private void OnStringSetBreakpoint(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            VM.ToggleBreakpointCommand.Execute(str.Address);
    }

    private void OnStringCopyAddress(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText(str.AddressHex);
    }

    private void OnStringCopyValue(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText(str.Value);
    }

    private void OnStringCopyLine(object sender, RoutedEventArgs e)
    {
        if (StringsGrid.SelectedItem is StringEntry str)
            Clipboard.SetText($"{str.ModuleName}\t{str.SectionName}\t{str.AddressHex}\t{str.TypeName}\t{str.Length}\t{str.Value}");
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
            HexDumpControl.Clear();
            return;
        }

        // Collect memory/HW breakpoint addresses for highlighting
        var bpAddrs = new HashSet<ulong>(
            VM.Breakpoints
                .Where(b => b.Type is Models.BreakpointType.Memory
                         or Models.BreakpointType.HwWrite
                         or Models.BreakpointType.HwReadWrite)
                .Select(b => b.Address));
        HexDumpControl.SetData(data, VM.HexAddress, bpAddrs);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var builtIn = new HashSet<string>(SettingsWindow.TabNames);
        var pluginTabs = MainTabControl.Items.OfType<TabItem>()
            .Select(t => t.Header?.ToString() ?? "")
            .Where(h => !string.IsNullOrEmpty(h) && !builtIn.Contains(h));
        var dlg = new SettingsWindow(VM.ThemeColors, pluginTabs) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            VM.ThemeColors = dlg.ResultColors;
            VM.SaveThemeColors();
            ApplyThemeColors(dlg.ResultColors);
            RefreshDisasmView();
        }
    }

    internal void ApplyThemeColors(Dictionary<string, string> colors)
    {
        var dict = Application.Current.Resources.MergedDictionaries[0];

        void SetBrush(string brushKey, string colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorHex);
                dict[brushKey] = new SolidColorBrush(color);
            }
            catch { /* ignore invalid */ }
        }

        // Settings key → Dark.xaml brush key
        var map = new Dictionary<string, string>
        {
            // General
            ["Bg"]              = "BgBrush",
            ["BgLight"]         = "BgLightBrush",
            ["BgPanel"]         = "BgPanelBrush",
            ["Border"]          = "BorderBrush",
            ["Fg"]              = "FgBrush",
            ["FgDim"]           = "FgDimBrush",
            ["Accent"]          = "AccentBrush",
            ["Selection"]       = "SelectionBrush",
            ["Toolbar"]         = "ToolbarBgBrush",
            ["StatusBar"]       = "StatusBarBrush",
            ["ValueChanged"]    = "ValueChangedBrush",
            // Disassembly
            ["DsmAddress"]      = "AddressBrush",
            ["DsmMnemonic"]     = "MnemonicBrush",
            ["DsmRegister"]     = "RegisterBrush",
            ["DsmBytes"]        = "HexBrush",
            ["DsmNumber"]       = "DsmNumberBrush",
            ["DsmJump"]         = "DsmJumpBrush",
            ["DsmPunctuation"]  = "DsmPunctuationBrush",
            ["DsmString"]       = "DsmStringBrush",
            ["DsmComment"]      = "DsmCommentBrush",
            ["DsmSymbol"]       = "DsmSymbolBrush",
            ["DsmBpMarker"]     = "BreakpointBrush",
            ["DsmBpRow"]        = "BpRowBrush",
            ["DsmCurrentLine"]  = "DsmCurrentLineBrush",
            ["DsmFunction"]     = "DsmFunctionBrush",
            // Stack
            ["StackOffset"]     = "StackOffsetBrush",
            ["StackAddress"]    = "StackAddressBrush",
            ["StackAnnotation"] = "StackAnnotationBrush",
            // Plugin controls
            ["PluginBg"]          = "PluginBgBrush",
            ["PluginFg"]          = "PluginFgBrush",
            ["PluginFgDim"]       = "PluginFgDimBrush",
            ["PluginBorder"]      = "PluginBorderBrush",
            ["PluginAccent"]      = "PluginAccentBrush",
            ["PluginControlBg"]   = "PluginControlBgBrush",
            ["PluginButtonBg"]    = "PluginButtonBgBrush",
            ["PluginButtonHover"] = "PluginButtonHoverBrush",
            ["PluginSelection"]   = "PluginSelectionBrush",
            ["PluginGridAltRow"]  = "PluginGridAltRowBrush",
            ["PluginGroupHeader"] = "PluginGroupHeaderBrush",
            ["PluginGroupBg"]     = "PluginGroupBgBrush",
        };

        int applied = 0;
        foreach (var (settingKey, brushKey) in map)
        {
            if (colors.TryGetValue(settingKey, out var hex))
            {
                SetBrush(brushKey, hex);
                applied++;
            }
        }
        VM.Log($"[Theme] Applied {applied}/{map.Count} brushes from {colors.Count} color entries");
        if (dict.Contains("BgBrush") && dict["BgBrush"] is SolidColorBrush bgb)
            VM.Log($"[Theme] BgBrush = {bgb.Color}");
        if (colors.TryGetValue("Bg", out var dbgBg))
            VM.Log($"[Theme] Bg setting = {dbgBg}");
        if (dict.Contains("MnemonicBrush") && dict["MnemonicBrush"] is SolidColorBrush mb)
            VM.Log($"[Theme] MnemonicBrush = {mb.Color}");
        if (colors.TryGetValue("DsmMnemonic", out var dbgHex))
            VM.Log($"[Theme] DsmMnemonic setting = {dbgHex}");

        // Tab header colors (global TabStyle + per-tab overrides)
        ApplyTabColors(colors);

        // Update plugin wrapper scopes with new plugin brush values
        foreach (var wrapper in _pluginWrappers)
            ApplyPluginResources(wrapper);
    }

    /// <summary>
    /// Overrides standard brush keys (BgBrush, FgBrush, etc.) inside the plugin wrapper scope
    /// so that implicit styles resolve to PluginXxx brushes instead of the main app brushes.
    /// </summary>
    private static void ApplyPluginResources(ContentControl wrapper)
    {
        var app = Application.Current.Resources.MergedDictionaries[0];
        var rd = wrapper.Resources;

        void Map(string standardKey, string pluginKey)
        {
            if (app.Contains(pluginKey))
                rd[standardKey] = app[pluginKey];
        }

        Map("BgBrush",        "PluginBgBrush");
        Map("BgLightBrush",   "PluginButtonBgBrush");
        Map("BgPanelBrush",   "PluginControlBgBrush");
        Map("FgBrush",        "PluginFgBrush");
        Map("FgDimBrush",     "PluginFgDimBrush");
        Map("BorderBrush",    "PluginBorderBrush");
        Map("AccentBrush",    "PluginAccentBrush");
        Map("SelectionBrush", "PluginSelectionBrush");
    }

    private static SolidColorBrush? TryParseBrush(Dictionary<string, string> colors, string key)
    {
        if (!colors.TryGetValue(key, out var hex) || string.IsNullOrWhiteSpace(hex)) return null;
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return null; }
    }

    private void ApplyTabColors(Dictionary<string, string> colors)
    {
        // Global tab style colors
        var globalBg       = TryParseBrush(colors, "TabBg");
        var globalFg       = TryParseBrush(colors, "TabFg");
        var globalSelBg    = TryParseBrush(colors, "TabSelBg");
        var globalSelFg    = TryParseBrush(colors, "TabSelFg");
        var globalSelBorder = TryParseBrush(colors, "TabSelBorder");
        var globalHoverBg  = TryParseBrush(colors, "TabHoverBg");

        foreach (TabItem tab in MainTabControl.Items)
        {
            var name = tab.Header?.ToString() ?? "";

            // Per-tab overrides (individual tab colors)
            var perTabFg = TryParseBrush(colors, $"Tab.{name}.Fg");
            var perTabBg = TryParseBrush(colors, $"Tab.{name}.Bg");

            // Effective colors: per-tab override > global > fallback from resources
            var tabBg      = perTabBg ?? globalBg;
            var tabFg      = perTabFg ?? globalFg;
            var selBg      = globalSelBg;
            var selFg      = perTabFg ?? globalSelFg;
            var selBorder  = globalSelBorder ?? (FindResource("AccentBrush") as SolidColorBrush);
            var hoverBg    = globalHoverBg;

            // Build custom template
            var borderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "TabBorder");
            borderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
                FindResource("BorderBrush") as Brush ?? Brushes.Gray);
            borderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
            borderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12, 5, 12, 5));
            borderFactory.SetValue(System.Windows.Controls.Border.MarginProperty, new Thickness(0, 0, -1, 0));
            borderFactory.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);

            if (tabBg != null)
                borderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, tabBg);
            else
                borderFactory.SetBinding(System.Windows.Controls.Border.BackgroundProperty,
                    new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = borderFactory };

            // Selected trigger
            var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            if (selBg != null)
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, selBg) { TargetName = "TabBorder" });
            if (selFg != null)
                selectedTrigger.Setters.Add(new Setter(TabItem.ForegroundProperty, selFg));
            if (selBorder != null)
                selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, selBorder) { TargetName = "TabBorder" });
            selectedTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderThicknessProperty,
                new Thickness(1, 2, 1, 0)) { TargetName = "TabBorder" });
            template.Triggers.Add(selectedTrigger);

            // Hover trigger
            if (hoverBg != null)
            {
                var hoverTrigger = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, hoverBg) { TargetName = "TabBorder" });
                template.Triggers.Add(hoverTrigger);
            }

            var style = new Style(typeof(TabItem));
            if (tabFg != null)
                style.Setters.Add(new Setter(TabItem.ForegroundProperty, tabFg));
            if (tabBg != null)
                style.Setters.Add(new Setter(TabItem.BackgroundProperty, tabBg));
            style.Setters.Add(new Setter(TabItem.TemplateProperty, template));

            tab.Style = style;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        VM.Dispose();
        base.OnClosed(e);
    }
}
