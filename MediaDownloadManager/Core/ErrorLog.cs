using System.Text;

namespace MediaDownloadManager.Core;

public static class ErrorLog
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Buffer = new();

    public static event Action<string>? Appended;

    public static string Text
    {
        get { lock (Gate) return Buffer.ToString(); }
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Gate)
        {
            Buffer.AppendLine(line);
            if (Buffer.Length > 200_000)
                Buffer.Remove(0, Buffer.Length - 150_000);
        }
        Appended?.Invoke(line);
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        Appended?.Invoke("");
    }
}
