using RuleOpen.Core.Interfaces;

namespace RuleOpen.Windows;

/// <summary>
/// Concrete Windows filesystem adapter.
/// </summary>
public sealed class WindowsFileSystem : IFileSystem
{
    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
}

/// <summary>
/// Executes Shell COM operations on an STA apartment thread when invoked from MTA thread-pool tasks.
/// </summary>
internal static class StaThreadRunner
{
    public static T Run<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return action();
        }

        T? result = default;
        Exception? captured = null;

        Thread thread = new(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }

        return result!;
    }
}
