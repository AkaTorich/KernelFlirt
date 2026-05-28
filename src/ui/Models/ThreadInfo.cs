using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KernelFlirt.UI.Models;

public class ThreadInfo : INotifyPropertyChanged
{
    public uint ThreadId { get; set; }
    public ulong StartAddress { get; set; }
    public uint State { get; set; }
    public uint Priority { get; set; }

    // Заморожен ли поток отладчиком (вёл учёт сам отладчик через SuspendThread/ResumeThread).
    // OS-уровень состояния (Waiting/Running/...) показывается отдельным свойством OsStateText.
    private bool _isFrozen;
    public bool IsFrozen
    {
        get => _isFrozen;
        set
        {
            if (_isFrozen == value) return;
            _isFrozen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string StartAddressHex => $"{StartAddress:X16}";

    // Основная индикация: красным значком "SUSPENDED" если поток заморожен,
    // иначе "RUNNING". OS-уровень состояния доступен через OsStateText (для tooltip
    // или отдельной колонки, если когда-то понадобится).
    public string StateText => IsFrozen ? "SUSPENDED" : "RUNNING";

    public string OsStateText => State switch
    {
        0 => "Initialized",
        1 => "Ready",
        2 => "Running",
        3 => "Standby",
        4 => "Terminated",
        5 => "Waiting",
        6 => "Transition",
        7 => "DeferredReady",
        _ => $"Unknown({State})"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
