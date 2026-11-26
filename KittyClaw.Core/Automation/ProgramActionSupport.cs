using System.Diagnostics;

namespace KittyClaw.Core.Automation;

public static class ProgramActionSupport
{
    public static void Validate(ExecuteProgramActionSpec spec)
    {
        if (spec.TimeoutSeconds < 1) throw new InvalidOperationException("Timeout must be positive.");
        if (Path.IsPathRooted(spec.WorkingDirectory)
            || spec.WorkingDirectory.Replace('\\', '/').Split('/').Contains(".."))
            throw new InvalidOperationException("Working directory must be relative to the project.");
        if (spec is ExecutePythonActionSpec python)
        {
            if (new[] { python.ScriptFile, python.Module, python.Script }.Count(value => !string.IsNullOrWhiteSpace(value)) != 1)
                throw new InvalidOperationException("Choose exactly one Python file, module, or embedded script.");
            if (python.Module.StartsWith('-') || python.ScriptFile.StartsWith('-'))
                throw new InvalidOperationException("Python targets cannot start with an option marker.");
        }
        else if (string.IsNullOrWhiteSpace(spec.Program))
            throw new InvalidOperationException("Program is required.");
    }

    public static bool IsAvailable(ExecuteProgramActionSpec spec, string workspace)
    {
        try
        {
            var start = BuildStartInfo(spec, workspace);
            if (Path.IsPathRooted(start.FileName)) return File.Exists(start.FileName);
            var extensions = OperatingSystem.IsWindows() ? new[] { "", ".exe", ".com" } : new[] { "" };
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Any(folder => extensions.Any(extension => File.Exists(Path.Combine(folder, start.FileName + extension))));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return false; }
    }

    public static ProcessStartInfo BuildStartInfo(ExecuteProgramActionSpec spec, string workspace)
    {
        Validate(spec);
        var directory = Path.GetFullPath(Path.Combine(workspace, spec.WorkingDirectory));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Working directory does not exist.");
        var program = spec.Program;
        if (spec is ExecutePythonActionSpec && string.IsNullOrWhiteSpace(program))
        {
            program = Path.Combine(workspace, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
            if (!File.Exists(program)) program = OperatingSystem.IsWindows() ? "python" : "python3";
        }
        if (program.Contains('/') || program.Contains('\\')) program = Path.GetFullPath(Path.Combine(workspace, program));
        var start = new ProcessStartInfo(program) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        if (spec is ExecutePythonActionSpec python)
        {
            start.ArgumentList.Add("-u");
            if (!string.IsNullOrWhiteSpace(python.Module))
            {
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add(python.Module);
            }
            else if (!string.IsNullOrWhiteSpace(python.Script))
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(python.Script);
            }
            else start.ArgumentList.Add(Path.GetFullPath(Path.Combine(workspace, python.ScriptFile)));
        }
        return start;
    }
}
