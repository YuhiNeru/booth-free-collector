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
