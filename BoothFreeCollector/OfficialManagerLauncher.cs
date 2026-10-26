using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BoothFreeCollector;

public sealed class OfficialManagerLauncher : IDisposable
{
    FileStream? pendingTrust;
    DateTimeOffset? lastLaunch;
    string? expectedFolder;
    public static string? ExecutableFromCommand(string value)
    {
        if (value.Length > 4096 || value.Any(char.IsControl)) return null;
        var quoted = Regex.Match(value, "^\\s*\"([^\"]+\\.exe)\"(?:\\s|$)", RegexOptions.IgnoreCase);
        var plain = Regex.Match(value, @"^\s*(\S+\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
        string? path = quoted.Success ? quoted.Groups[1].Value : plain.Success ? plain.Groups[1].Value : null;
        return path is not null && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") &&
            Path.GetFileName(path).Equals("booth_library_manager_client.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    static bool Trusted(string path)
    {
        try { DownloadEngine.EnsureNoLinks(path); using var file = OfficialExecutableTrust.OpenVerified(path); return true; }
        catch { return false; }
    }
    public static string? Resolve(Settings settings)
    {
        if (!string.IsNullOrEmpty(settings.OfficialManagerExecutable) && Trusted(settings.OfficialManagerExecutable)) return settings.OfficialManagerExecutable;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var key = Registry.ClassesRoot.OpenSubKey(@"booth-library-manager\shell\open\command"))
            if (key?.GetValue("") is string command && ExecutableFromCommand(command) is string path) paths.Add(path);
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BOOTH Library Manager", "booth_library_manager_client.exe"));
        var valid = paths.Where(Trusted).ToArray();
        return valid.Length == 1 ? valid[0] : null;
    }
    public bool TryStart(Settings settings)
    {
        if (!settings.AutoStartOfficialManager || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is not null) return false;
        var running = System.Diagnostics.Process.GetProcessesByName("booth_library_manager_client");
        if (running.Length > 0) { foreach (var process in running) process.Dispose(); return false; }
        var now = DateTimeOffset.UtcNow;
        var stamp = Storage.Load<LaunchStamp>("manager-launch-state.json");
        if (now - (lastLaunch ?? stamp.At ?? DateTimeOffset.MinValue) < TimeSpan.FromMinutes(1)) return false;
        string? executable = Resolve(settings);
        if (executable is null) return false;
        pendingTrust?.Dispose(); pendingTrust = OfficialExecutableTrust.OpenVerified(executable);
        expectedFolder = ExpectedFolderForLaunch(stamp, Storage.Load<ActiveLibraryDatabase.SettingsCache>("manager-settings.json").Folder);
        lastLaunch = now; Storage.Save("manager-launch-state.json", new LaunchStamp { At = now, ExpectedFolder = expectedFolder, AwaitingConfirmation = true });
        object? shell = null;
        try
        {
            // Launch the verified installation through the shell. The active DB
            // and expected destination are checked again before importing.
            var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new IOException("Windowsのアプリ起動機能を確認できません");
            shell = Activator.CreateInstance(type) ?? throw new IOException("Windowsのアプリ起動機能を確認できません");
            ((dynamic)shell).ShellExecute(executable, "", Path.GetDirectoryName(executable)!, "open", 0);
            return true;
        }
        catch { pendingTrust.Dispose(); pendingTrust = null; throw new IOException("公式ライブラリを自動起動できません。公式配布版のインストール先を確認してください"); }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }
    public LibraryRequirement Confirm(LibraryRequirement requirement)
    {
        var stamp = Storage.Load<LaunchStamp>("manager-launch-state.json");
        if (stamp.AwaitingConfirmation && expectedFolder is null) expectedFolder = stamp.ExpectedFolder;
        if (!requirement.Ready)
        {
            if (lastLaunch is DateTimeOffset at && DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(30)) { pendingTrust?.Dispose(); pendingTrust = null; }
            return requirement;
        }
        pendingTrust?.Dispose(); pendingTrust = null;
        if (!string.IsNullOrEmpty(expectedFolder) && !string.Equals(expectedFolder, requirement.Folder, StringComparison.OrdinalIgnoreCase))
            return new(false, "自動起動後の保存先が前回と異なります。公式Managerの保存先を確認してください（前回: " + expectedFolder + "）");
        expectedFolder = null;
        if (stamp.AwaitingConfirmation) { stamp.AwaitingConfirmation = false; stamp.ExpectedFolder = null; Storage.Save("manager-launch-state.json", stamp); }
        return requirement;
    }
    internal static string? ExpectedFolderForLaunch(LaunchStamp stamp, string? cachedFolder)
        => stamp.AwaitingConfirmation && !string.IsNullOrEmpty(stamp.ExpectedFolder) ? stamp.ExpectedFolder : cachedFolder;
    public void Dispose() => pendingTrust?.Dispose();
    public sealed class LaunchStamp
    {
        public DateTimeOffset? At { get; set; }
        public string? ExpectedFolder { get; set; }
        public bool AwaitingConfirmation { get; set; }
    }
}
