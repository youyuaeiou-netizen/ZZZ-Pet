using System.IO;
using System.Text.Json;

namespace DesktopPet.Tools;

public sealed class ToolsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? Error { get; private set; }
    public bool Available => Error is null;

    public ToolsState Load()
    {
        try
        {
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<ToolsState>(File.ReadAllText(path), Options)
                    ?? throw new InvalidDataException("工具数据为空。")
                : new ToolsState();
            if (state.SchemaVersion != 1 || state.Reminders is null || state.Focus is null ||
                state.Reminders.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Title) ||
                    r.Title.Length > 200 || r.ScheduledLocal == default || !Enum.IsDefined(r.Repeat)) ||
                state.Reminders.Select(r => r.Id).Distinct().Count() != state.Reminders.Count ||
                !Enum.IsDefined(state.Focus.Stage) || state.Focus.FocusMinutes is < 1 or > 240 ||
                state.Focus.BreakMinutes is < 1 or > 240 || !double.IsFinite(state.Focus.RemainingSeconds) ||
                state.Focus.RemainingSeconds < 0 || state.Focus.RemainingSeconds > 240 * 60)
                throw new InvalidDataException("工具数据格式或版本不兼容。原文件已保留。");
            if (state.BubbleSeconds is not (10 or 20 or 30 or 60)) state.BubbleSeconds = 20;
            // Persisted sessions always restart paused, even after an unexpected exit.
            state.Focus.Running = false;
            return state;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Error = $"提醒与计时暂不可用：{ex.Message}";
            return new ToolsState();
        }
    }

    public bool Save(ToolsState state)
    {
        if (!Available) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var json = JsonSerializer.Serialize(state, Options);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"工具数据无法保存，提醒与计时已暂停：{ex.Message}";
            return false;
        }
    }
}
