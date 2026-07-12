namespace xiaoxu_music_bridge.Common;

public static class TaskTimeout
{
    public static async Task<(T Result, bool TimedOut, Exception? SyncFault)> Run<T>(
        Func<Task<T>> taskFactory,
        int milliseconds)
    {
        Task<T> task;
        try
        {
            task = taskFactory();
        }
        catch (Exception ex)
        {
            return (default!, true, ex);
        }

        if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task)
            return (default!, true, null);

        try
        {
            return (await task, false, null);
        }
        catch (Exception ex)
        {
            return (default!, true, ex);
        }
    }
}
