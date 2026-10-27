# Security verification: 2026-10-27

The published project already pins RuntimeFrameworkVersion 10.0.14, which includes the Brotli fix. No application source or package version change was needed. The shared development PC was updated to .NET 10.0.14 / 8.0.33, Git for Windows 2.56.0.windows.4 (Brotli 1.2.1) and WebView2 156.0.4314.50.

The corresponding local distribution passed --self-test and --session-self-test using dedicated folders, fabricated cookies and fixture responses. The 3 Worker tests passed. No real authentication store or external publication was used. Existing distributed copies on other PCs remain unverified. No PHP application was found in this checked source. Repository visibility remains public.

Official references: [.NET advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9), [Git security release](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.4), [PHP changelog](https://www.php.net/ChangeLog-8.php).
