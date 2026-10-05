using System.IO;
using System.Text;

namespace DesktopPet;

public sealed class DiagnosticLog(string directory)
{
    private readonly object _gate = new();
    public string FilePath { get; } = Path.Combine(directory, "diagnostics.log");

    public void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var detail = exception is null ? "" : $" | {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}";
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} [{level}] {AppInfo.Label} {message}{detail}\n", Encoding.UTF8);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Diagnostic log unavailable: {failure.Message}");
        }
    }
}
