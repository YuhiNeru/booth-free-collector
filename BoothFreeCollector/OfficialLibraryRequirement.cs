namespace BoothFreeCollector;

public sealed record LibraryRequirement(bool Ready, string Message, string? Folder = null, string? Executable = null, bool Retryable = false);
public static class OfficialLibraryRequirement
{
    public static LibraryRequirement Evaluate(LibraryInfo library, bool running, bool signed)
    {
        if (!running) return new(false, "公式BOOTH Library Managerをインストールして起動してください", Retryable: true);
        if (!signed) return new(false, "公式Managerの署名を確認できません。公式配布版を使用してください");
        if (library.Error is not null) return new(false, "公式Managerの設定を確認できません。取得を停止しています", Retryable: true);
        if (string.IsNullOrWhiteSpace(library.Folder) || !Path.IsPathFullyQualified(library.Folder) || library.Folder.StartsWith(@"\\"))
            return new(false, "公式Managerの設定で、ローカルの保存先を指定してください");
        return new(true, "公式Manager・署名・保存先を確認しました: " + library.Folder, library.Folder);
    }
    public static LibraryRequirement EvaluateInstallation(string? executable, bool signed)
    {
        if (string.IsNullOrEmpty(executable) || OfficialManagerLauncher.ExecutableFromCommand("\"" + executable + "\"") is null)
            return new(false, "公式BOOTH Library Managerをインストールしてください");
        if (!signed) return new(false, "公式ライブラリの署名を確認できません。公式配布版を使用してください");
        return new(true, "公式ライブラリの導入・署名を確認しました(起動しません)", Executable: executable);
    }
    public static LibraryRequirement CheckInstalled(Settings settings)
    {
        // Resolve verifies the installed file. No process creation or live DB
        // is needed to unlock the dedicated login during initial setup.
        try
        {
            string? path = OfficialManagerLauncher.Resolve(settings);
            return EvaluateInstallation(path, path is not null);
        }
        catch { return new(false, "公式ライブラリの導入・署名を確認できません"); }
    }
    public static bool NeedsRunningManager(Settings settings, CollectorState state)
        => settings.Configured && settings.SetupCompleted && settings.DedicatedSessionEnabled && settings.AutoDownload &&
            (state.Queue.Any(p => p.Matches(settings)) || state.ManagerImports.Values.Any(r => !r.Confirmed));
    public static LibraryRequirement Check()
    {
        string executable;
        try { executable = ManagerIntegration.RunningExecutable(); }
        catch { return Evaluate(new(null, [], null), false, false); }
        try { using var verified = OfficialExecutableTrust.OpenVerified(executable); }
        catch { return Evaluate(new(null, [], null), true, false); }
        return Evaluate(LibraryInfo.ReadActive(), true, true) with { Executable = executable };
    }
}
