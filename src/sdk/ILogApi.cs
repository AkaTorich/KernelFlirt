namespace KernelFlirt.SDK;

public interface ILogApi
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
