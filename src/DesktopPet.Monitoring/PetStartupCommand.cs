namespace DesktopPet.Monitoring;

public static class PetStartupCommand
{
    public static bool BelongsTo(string? command, string? executable)
    {
        if (command is null || executable is null || !command.StartsWith('"')) return false;
        var end = command.IndexOf('"', 1);
        return end > 1 && (end == command.Length - 1 || command[end + 1] == ' ')
            && string.Equals(command[1..end], executable, StringComparison.OrdinalIgnoreCase);
    }
    public static string Create(string executable, string directory)
    {
        if (executable.Contains('"') || directory.Contains('"') || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Invalid startup path");
        var data = Path.TrimEndingDirectorySeparator(directory);
        var trailing = data.Length - data.TrimEnd('\\').Length;
        return $"\"{executable}\" --data-dir \"{data}{new string('\\', trailing)}\"";
    }
}
