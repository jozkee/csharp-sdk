#if !NET
using System.Diagnostics;
#endif

namespace ModelContextProtocol.Client;

/// <summary>
/// Resolves a Windows command to a concrete executable path using the same directory precedence as
/// .NET's Unix process launcher, with Windows <c>PATHEXT</c> probing and path handling from Node and Python.
/// </summary>
internal static class WindowsCommandResolver
{
    /// <summary>Gets the full path of the current process executable, matching <c>Environment.ProcessPath</c>.</summary>
    public static string? GetCurrentProcessPath()
    {
#if NET
        return Environment.ProcessPath;
#else
        using Process currentProcess = Process.GetCurrentProcess();
        return currentProcess.MainModule?.FileName;
#endif
    }

    public static string? Resolve(
        string command,
        string? processPath,
        string currentDirectory,
        string? path,
        string? pathExt,
        string? noDefaultCurrentDirectoryInExePath)
    {
        string[] extensions = (pathExt ?? string.Empty)
            .Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries)
            .Select(static ext => ext.Trim())
            .Where(static ext => ext.Length != 0)
            .Select(static ext => ext[0] == '.' ? ext : "." + ext)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string fileName = Path.GetFileName(command);
        bool probeExactName = extensions.Length == 0 || fileName.Contains('.') ||
            extensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        if (Path.IsPathRooted(command))
        {
            return FindFirstCandidate(command, probeExactName, extensions);
        }

        bool containsDirectorySeparator = command.IndexOf('\\') >= 0 || command.IndexOf('/') >= 0;
        if (!containsDirectorySeparator && processPath is not null)
        {
            try
            {
                string? processDirectory = Path.GetDirectoryName(processPath);
                if (processDirectory is not null && FindFirstCandidate(Path.Combine(processDirectory, command), probeExactName, extensions) is { } resolved)
                {
                    return resolved;
                }
            }
            catch (ArgumentException) { }
        }

        if ((containsDirectorySeparator || ShouldSearchCurrentDirectory(noDefaultCurrentDirectoryInExePath)) &&
            FindFirstCandidate(Path.Combine(currentDirectory, command), probeExactName, extensions) is { } currentDirectoryResult)
        {
            return currentDirectoryResult;
        }

        // Match the Win32 CreateProcess search order: the system directories are probed after the
        // application and current directories but before PATH. Searching them here prevents a
        // user-controlled PATH entry from shadowing a trusted system executable such as cmd.exe.
        if (!containsDirectorySeparator)
        {
            foreach (string systemDirectory in GetSystemSearchDirectories())
            {
                if (FindFirstCandidate(Path.Combine(systemDirectory, command), probeExactName, extensions) is { } systemDirectoryResult)
                {
                    return systemDirectoryResult;
                }
            }
        }

        if (!containsDirectorySeparator && path is not null)
        {
            HashSet<string> seenDirectories = new(StringComparer.OrdinalIgnoreCase);
            foreach (string rawDirectory in path.Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length != 0 && seenDirectories.Add(directory) &&
                    FindFirstCandidate(Path.Combine(currentDirectory, directory, command), probeExactName, extensions) is { } pathResult)
                {
                    return pathResult;
                }
            }
        }

        return null;
    }

    private static string? FindFirstCandidate(string baseName, bool probeExactName, string[] extensions)
    {
        if (probeExactName && TryGetExistingFullPath(baseName) is { } exact)
        {
            return exact;
        }

        foreach (string extension in extensions)
        {
            if (TryGetExistingFullPath(baseName + extension) is { } candidate)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryGetExistingFullPath(string candidate)
    {
        try
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    // Matches Win32 NeedCurrentDirectoryForExePath: skip the current directory when the caller opted out.
    // The value comes from the finalized child environment so it reflects what the launched process sees.
    private static bool ShouldSearchCurrentDirectory(string? noDefaultCurrentDirectoryInExePath) =>
        string.IsNullOrEmpty(noDefaultCurrentDirectoryInExePath);

    // The trusted directories CreateProcess searches after the application and current directories and
    // before PATH: the system directory, the 16-bit system directory, and the Windows directory.
    private static IEnumerable<string> GetSystemSearchDirectories()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(systemDirectory))
        {
            yield return systemDirectory;
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "System");
            yield return windowsDirectory;
        }
    }
}
