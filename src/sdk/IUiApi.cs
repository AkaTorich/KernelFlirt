namespace KernelFlirt.SDK;

public interface IUiApi
{
    void NavigateDisassembly(ulong address);
    void AddMenuItem(string header, Action callback);
    void AddToolPanel(string title, object wpfContent);
}
