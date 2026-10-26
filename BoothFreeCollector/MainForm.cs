using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace BoothFreeCollector;

public sealed class MainForm : Form
{
    readonly Settings settings;
    readonly CollectorState state;
    readonly FeedClient feed = new();
    readonly EmbeddedSession session = new();
    readonly SessionDownloadQueue downloadQueue;
    readonly OfficialManagerLauncher managerLauncher = new();
    readonly Label loginStatus = new() { AutoSize = true, Text = "ログイン未確認" };
    readonly Label setupLibraryStatus = new() { AutoSize = true, Text = "① 公式ライブラリを確認してください", MaximumSize = new Size(850, 0) };
    readonly List<Control> sessionActions = [];
    Button? logoutButton;
    Button? libraryCheckButton;
    bool setupBusy;
    readonly Func<LibraryRequirement>? testRequirement;
    internal Func<Task>? BeforeForgetForVerification;
    Task? sessionInitialization;
    bool libraryReady, libraryChecking;
    Task<bool>? libraryCheckTask, activeLibraryCheckTask;
    Task<LibraryRequirement>? libraryProbeTask, installationProbeTask;
    internal Func<LibraryRequirement>? ActiveRequirementForVerification;
    internal TimeSpan LibraryCheckTimeoutForVerification = TimeSpan.FromSeconds(15);
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30000 };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label schedule = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label folderLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ListBox events = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly DataGridView grid = new CollectorGrid { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    readonly CollectorNavigation tabs = new() { Dock = DockStyle.Fill };
    readonly Label emptyQueue = new() { Dock = DockStyle.Fill, Text = "待機中の商品はありません", TextAlign = ContentAlignment.MiddleCenter };
    (string Id, string Title, string Shop)[] displayedRows = [];
    string? confirmedFolder;
    bool folderConfirmed;
    readonly Label libraryFolderStatus = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    readonly CheckBox auto = new CollectorCheckBox() { Text = "新着の無料ファイルを自動ダウンロード", AutoSize = true };
    readonly CheckBox adult = new CollectorCheckBox() { Text = "成人向け・年齢区分不明も対象にする（18歳以上）", AutoSize = true };
    readonly CheckBox existing = new CollectorCheckBox() { Text = "初回に既存の無料商品も対象にする（大量のファイルを取得します）", AutoSize = true };
    readonly CheckBox startup = new CollectorCheckBox() { Text = "Windows ログイン時に常駐する", AutoSize = true };
    readonly CheckBox startManager = new CollectorCheckBox() { Text = "必要なとき公式ライブラリを自動起動する", AutoSize = true };
    readonly TextBox search = new() { Width = 500, PlaceholderText = "例: 衣装 Unity（スペース区切りでAND）" };
    readonly Dictionary<string, CheckBox> categoryChecks = [];
    readonly List<Control> explanations = [];
    readonly Button explanationToggle = new CollectorButton { Name = "explanationToggle", AccessibleName = "説明文の表示切り替え", AutoSize = false, Size = new Size(120, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
    readonly List<Button> primaryButtons = [];
    Button? resumeButton, pauseButton;
    readonly TextBox endpoint = new() { Width = 700 };
    CancellationTokenSource cancellation = new();
    bool busy, exiting;
    readonly bool startHidden;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CollectorTheme.ApplyWindow(this);
    }

    public MainForm(bool startHidden, Func<LibraryRequirement>? testRequirement = null)
    {
        if (testRequirement is not null && Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException("テスト専用の依存関係です");
        this.testRequirement = testRequirement;
        this.startHidden = startHidden;
        settings = Storage.Load<Settings>("settings.json");
        if (Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) settings.FollowLibraryFolder = true;
        state = Storage.Load<CollectorState>("state.json");
        DownloadScheduler.Reconcile(state, settings, DateTimeOffset.UtcNow);
        Storage.Save("state.json", state);
        Text = "BOOTH Free Downloader"; Width = 1120; Height = 840; MinimumSize = new Size(960, 720);
        DoubleBuffered = true;
        Font = new Font("Yu Gothic UI", 10); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        downloadQueue = new(session);
        BuildUi();
        CollectorTheme.Apply(this);
        tabs.ApplyPalette();
        emptyQueue.ForeColor = CollectorTheme.Muted;
        foreach (var primary in primaryButtons) CollectorTheme.Primary(primary);
        foreach (var hint in explanations) hint.ForeColor = CollectorTheme.Muted;
        ApplyExplanationVisibility();
        Controls.Add(session.Worker);
        session.Changed += () => { loginStatus.Text = session.NeedsLogin ? "ログイン・アクセス確認が必要です" : "アプリ専用のログイン状態を確認しました"; UpdateView(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => ShowApp());
        menu.Items.Add("終了", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "BOOTH Free Downloader", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowApp();
        timer.Tick += async (_, _) => await Tick();
        Shown += async (_, _) =>
        {
            setupBusy = true; RefreshSessionActions();
            try
            {
                BrowserBridge.Unregister(); timer.Start();
                if (await CheckLibrary())
                {
                    await EnsureSession();
                    if (settings.DedicatedSessionEnabled) await session.CheckLoginAsync(CancellationToken.None);
                    if (settings.SetupCompleted && !session.NeedsLogin && !Storage.Load<SessionImportVerification>("session-import-verification.json").PagePlanVerified)
                        await VerifyImportConnection();
                    if (settings.DedicatedSessionEnabled && session.NeedsLogin) session.OpenLogin();
                }
                if (!settings.Configured || !settings.SetupCompleted) { tabs.SelectedIndex = tabs.TabPages.Count - 1; ShowApp(); }
                else
                {
                    if (!libraryReady || !settings.SetupCompleted || session.NeedsLogin) { tabs.SelectedIndex = tabs.TabPages.Count - 1; ShowApp(); }
                    else if (startHidden) Hide(); else ShowApp();
                }
            }
            catch (Exception) { status.Text = "起動設定を確認してください"; loginStatus.Text = "ログイン画面を準備できません。WebView2 Runtimeを確認してください"; tabs.SelectedIndex = tabs.TabPages.Count - 1; ShowApp(); }
            finally { setupBusy = false; RefreshSessionActions(); }
            if (settings.Configured && settings.SetupCompleted) await Tick();
        };
        FormClosing += (_, e) =>
        {
            if (!exiting) { e.Cancel = true; Hide(); return; }
            timer.Stop(); cancellation.Cancel(); tray.Visible = false;
        };
        FormClosed += (_, _) => { session.Dispose(); managerLauncher.Dispose(); tray.Dispose(); feed.Dispose(); cancellation.Dispose(); };
        UpdateView();
    }
    static Button Button(string text, EventHandler click)
    {
        var b = new CollectorButton { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 5, 10, 5) }; b.Click += click; return b;
    }
    static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
    Label Hint(string text)
    {
        var label = Label(text);
        label.MaximumSize = new Size(760, 0);
        explanations.Add(label);
        return label;
    }
    void ApplyExplanationVisibility()
    {
        foreach (var hint in explanations) hint.Visible = settings.ShowExplanations;
        explanationToggle.Text = settings.ShowExplanations ? "説明：ON" : "説明：OFF";
        explanationToggle.AccessibleDescription = settings.ShowExplanations ? "補足説明を非表示にします" : "補足説明を表示します";
    }
    void BuildUi()
    {
        var header = new Panel { Dock = DockStyle.Top, Width = ClientSize.Width, Height = 82, Padding = new Padding(22, 16, 22, 12) };
        header.Controls.Add(new Label { Text = "BOOTH Free Downloader", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true, Location = new Point(22, 23) });
        explanationToggle.Location = new Point(ClientSize.Width - 158, 24);
        explanationToggle.Click += (_, _) =>
        {
            settings.ShowExplanations = !settings.ShowExplanations;
            Storage.Save("settings.json", settings);
            ApplyExplanationVisibility();
        };
        header.Controls.Add(explanationToggle);
        Controls.Add(tabs); Controls.Add(header);
        var overview = new BufferedPanel { Text = "ダウンロード", Padding = new Padding(28, 14, 28, 20) };
        var overviewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        status.Font = new Font(Font.FontFamily, 14, FontStyle.Bold); status.Margin = new Padding(0, 0, 0, 14);
        overviewLayout.Controls.Add(status); overviewLayout.Controls.Add(schedule); overviewLayout.Controls.Add(folderLabel);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        resumeButton = Button("確認・再開", async (_, _) =>
        {
            settings.AutoDownload = true; auto.Checked = true; Storage.Save("settings.json", settings);
            await Tick(true);
        });
        primaryButtons.Add(resumeButton); actions.Controls.Add(resumeButton);
        pauseButton = Button("一時停止", (_, _) => { cancellation.Cancel(); settings.AutoDownload = false; auto.Checked = false; Storage.Save("settings.json", settings); Log("自動ダウンロードを一時停止しました"); });
        actions.Controls.Add(pauseButton);
        actions.Controls.Add(Button("保存先を開く", async (_, _) =>
        {
            var library = await Task.Run(LibraryInfo.Read);
            if (library.Error is null && library.Folder is string path && Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Log("保存先は取得・一覧登録の直前に確認します。公式ライブラリで設定してください");
        }));
        actions.Controls.Add(Button("過去分を追加", async (_, _) => await QueueHistory()));
        overviewLayout.Controls.Add(actions);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "待機中の商品", DataPropertyName = "Title", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ショップ", DataPropertyName = "Shop", Width = 220 });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is Product p && Feed.SafeId(p.Id)) Process.Start(new ProcessStartInfo("https://booth.pm/ja/items/" + p.Id) { UseShellExecute = true }); };
        grid.DataSource = new List<Product>();
        var queueSurface = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 12) };
        queueSurface.Controls.Add(grid); queueSurface.Controls.Add(emptyQueue);
        overviewLayout.Controls.Add(queueSurface); overviewLayout.Controls.Add(events); overview.Controls.Add(overviewLayout); tabs.TabPages.Add(overview);

        var config = new BufferedPanel { Text = "設定", Padding = new Padding(28, 14, 28, 20) };
        var configScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(Label("保存先（公式Library Managerから自動取得）"));
        layout.Controls.Add(libraryFolderStatus);
        layout.Controls.Add(Hint("保存先は公式ライブラリの設定で変更できます。変更は自動で反映されます。"));
        layout.Controls.Add(Label("取得する商品")); layout.Controls.Add(auto); layout.Controls.Add(adult); layout.Controls.Add(existing);
        layout.Controls.Add(Label("キーワード（商品名・ショップ・タグ）")); layout.Controls.Add(search);
        layout.Controls.Add(Hint("カテゴリは「ダウンロード対象」画面で選択できます。"));
        layout.Controls.Add(Label("配信URL")); layout.Controls.Add(endpoint);
        layout.Controls.Add(startup);
        layout.Controls.Add(startManager);
        layout.Controls.Add(Hint("一覧の取得間隔は12時間です。選んだカテゴリの無料商品を、公式ライブラリで保存・展開します。"));
        var save = Button("設定を保存して開始", async (_, _) => await SaveSettings());
        save.Dock = DockStyle.Bottom; save.Height = 44; primaryButtons.Add(save);
        configScroll.Controls.Add(layout); config.Controls.Add(configScroll); config.Controls.Add(save); tabs.TabPages.Add(config);
        BuildCategoryTab();
        BuildLoginTab();
        auto.Checked = settings.AutoDownload; adult.Checked = settings.IncludeAdult; existing.Checked = settings.IncludeExisting;
        startManager.Checked = settings.AutoStartOfficialManager;
        existing.Enabled = !state.BaselineCreated; startup.Checked = settings.Startup; search.Text = settings.Search; endpoint.Text = settings.FeedUrl;
    }
    void BuildCategoryTab()
    {
        var page = new BufferedPanel { Text = "ダウンロード対象", Padding = new Padding(28, 14, 28, 20) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(Hint("初期設定は衣装・髪型のみです。チェックをすべて外すと取得しません。変更後は下のボタンで保存してください。"));
        foreach (string priority in new[] { "高", "中", "低" })
        {
            var group = new CollectorCheckBox { Text = "優先度：" + priority + "（まとめて選択）", AutoSize = true, ThreeState = true, AutoCheck = false, Margin = new Padding(0, 20, 0, 6) };
            var children = new List<CheckBox>();
            bool syncing = false;
            void Sync()
            {
                syncing = true;
                group.CheckState = children.All(c => c.Checked) ? CheckState.Checked : children.Any(c => c.Checked) ? CheckState.Indeterminate : CheckState.Unchecked;
                syncing = false;
            }
            panel.Controls.Add(group);
            foreach (var item in DownloadCategories.All.Where(c => c.Priority == priority))
            {
                var check = new CollectorCheckBox { Text = item.Label, AutoSize = true, Checked = settings.DownloadCategories.Contains(item.Id), Margin = new Padding(26, 3, 0, 3) };
                categoryChecks[item.Id] = check; children.Add(check); panel.Controls.Add(check);
                check.CheckedChanged += (_, _) => { if (!syncing) Sync(); };
            }
            group.Click += (_, _) =>
            {
                bool selected = !children.All(c => c.Checked);
                syncing = true; foreach (var child in children) child.Checked = selected; syncing = false; Sync();
            };
            Sync();
        }
        panel.Controls.Add(Hint("追加したカテゴリの過去分も取得する場合は、ダウンロード画面の「過去分を追加」を選んでください。"));
        var save = Button("対象カテゴリを保存して開始", async (_, _) => await SaveSettings());
        save.Dock = DockStyle.Bottom; primaryButtons.Add(save); page.Controls.Add(panel); page.Controls.Add(save); tabs.TabPages.Add(page);
    }
    async Task SaveSettings()
    {
        if (busy) { MessageBox.Show(this, "取得が終わるか、一時停止してから設定を保存してください。"); return; }
        try
        {
            var uri = new Uri(endpoint.Text.Trim());
            if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/catalog.json") throw new IOException("HTTPS のカタログURLを指定してください");
            settings.FollowLibraryFolder = true;
            settings.AutoDownload = auto.Checked; settings.IncludeAdult = adult.Checked; settings.IncludeExisting = existing.Checked;
            settings.Search = search.Text.Trim(); settings.Category = ""; settings.FeedUrl = uri.AbsoluteUri;
            settings.DownloadCategories = categoryChecks.Where(c => c.Value.Checked).Select(c => c.Key).ToArray();
            settings.Startup = startup.Checked; settings.Configured = true;
            settings.AutoStartOfficialManager = startManager.Checked;
            settings.BrowserHandoff = false;
            using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (settings.Startup) run.SetValue("BoothFreeCollector", $"\"{Environment.ProcessPath}\" --tray");
                else run.DeleteValue("BoothFreeCollector", false);
            }
            Storage.Save("settings.json", settings); tabs.SelectedIndex = 0; Log("設定を保存しました"); await Tick();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を確認してください"); }
    }
    async Task QueueHistory()
    {
        if (busy) { MessageBox.Show(this, "取得が終わるか、一時停止してから追加してください。"); return; }
        try
        {
            if (!File.Exists(Path.Combine(Storage.Root, "catalog.json"))) throw new IOException("一覧をまだ取得していません。最初に設定を保存してください");
            var catalog = Storage.Load<Feed>("catalog.json"); catalog.Validate();
            int added = 0;
            var pending = state.Queue.Select(p => p.Id).ToHashSet();
            foreach (var product in catalog.Items.Where(p => p.Matches(settings)))
                if (pending.Add(product.Id)) { state.Queue.Add(product); added++; }
            state.Status = $"過去分を{added:N0}商品追加しました。現在の取得条件で順次処理します";
            Storage.Save("state.json", state); Log(state.Status); UpdateView();
            await Tick(true);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { MessageBox.Show(this, ex.Message); }
    }
    void BuildLoginTab()
    {
        var page = new BufferedPanel { Text = "セットアップ", Padding = new Padding(28, 14, 28, 20) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.Add(setupLibraryStatus);
        libraryCheckButton = Button("① 公式ライブラリを確認", async (_, _) => await SetupAction(async () =>
        { if (await CheckLibrary()) await EnsureSession(); }));
        top.Controls.Add(libraryCheckButton);
        var install = new LinkLabel { Text = "公式ライブラリの入手方法（BOOTH公式案内）", AutoSize = true };
        install.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("https://booth.pm/announcements/893") { UseShellExecute = true });
        top.Controls.Add(install);
        top.Controls.Add(Label("② 専用アカウントでログインし、ログイン状態を保存します"));
        top.Controls.Add(new Label { Text = EmbeddedSession.LoginWarning, ForeColor = Color.Firebrick,
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        top.Controls.Add(new Label { Text = "メインアカウントの購入履歴や個人情報と分けるためです。\nこのアプリはログイン状態でBOOTHへアクセスするため、専用アカウントの利用でも安全を保証するものではありません。",
            ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(850, 0) });
        top.Controls.Add(Label("別アカウントの利用はBOOTHの規約に従ってください。普段のChromeのログイン情報はコピーしません。"));
        top.Controls.Add(Hint("ログイン状態は次回起動でも利用します。期限が切れたら、この画面でログインし直してください。"));
        var article = new LinkLabel { Text = "詳しい説明（Zenn記事）：" + EmbeddedSession.ArticleUrl, AutoSize = true, Margin = new Padding(0, 5, 0, 5) };
        article.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(EmbeddedSession.ArticleUrl) { UseShellExecute = true });
        top.Controls.Add(article); top.Controls.Add(loginStatus);
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.Add(Button("説明を確認してログインする", async (_, _) => await SetupAction(async () =>
        {
            if (!await CheckLibrary()) return;
            settings.SetupCompleted = false; settings.DedicatedSessionEnabled = true;
            Storage.Save("settings.json", settings);
            Storage.Save("session-import-verification.json", new SessionImportVerification());
            await EnsureSession(); session.OpenLogin();
        })));
        actions.Controls.Add(Button("ログインと接続を確認", async (_, _) =>
        {
            await SetupAction(async () =>
            {
                if (!await CheckLibrary()) return;
                await EnsureSession();
                if (await session.CheckLoginAsync(CancellationToken.None))
                {
                    await VerifyImportConnection();
                    settings.DedicatedSessionEnabled = true; settings.SetupCompleted = true; settings.Configured = true; Storage.Save("settings.json", settings);
                    loginStatus.Text = "初回セットアップ完了。ログイン状態を専用領域に保存しました";
                }
            });
            if (settings.SetupCompleted) await Tick(true);
        }));
        logoutButton = Button("ログアウト・認証データを削除", async (_, _) => await SetupAction(async () =>
        {
            // Disable acquisition before clearing data, including when a clear
            // fails. Removing credentials must not depend on Manager running.
            settings.DedicatedSessionEnabled = false; settings.SetupCompleted = false;
            Storage.Save("settings.json", settings);
            Storage.Save("session-import-verification.json", new SessionImportVerification());
            if (session.Ready || Directory.Exists(session.ProfileFolder))
            {
                await EnsureSession();
                if (testRequirement is not null && BeforeForgetForVerification is not null) await BeforeForgetForVerification();
                await session.ForgetAsync(CancellationToken.None);
            }
            loginStatus.Text = "アプリ専用のCookie・キャッシュなどを削除しました";
        }));
        foreach (Control action in actions.Controls) { sessionActions.Add(action); action.Enabled = false; }
        actions.Controls.Add(logoutButton);
        top.Controls.Add(actions); page.Controls.Add(session.LoginView); page.Controls.Add(top); tabs.TabPages.Add(page);
    }
    Task<bool> CheckLibrary(bool forImport = false) => forImport ? activeLibraryCheckTask ??= CheckLibraryCore(true) : libraryCheckTask ??= CheckLibraryCore(false);
    async Task<bool> CheckLibraryCore(bool forImport)
    {
        libraryChecking = true;
        setupLibraryStatus.Text = forImport ? "① 取得・一覧登録のため公式ライブラリと保存先を確認中…" : "① 公式ライブラリの導入・署名を確認中…";
        setupLibraryStatus.ForeColor = CollectorTheme.Muted;
        if (libraryCheckButton is not null) libraryCheckButton.Text = "確認中…";
        RefreshSessionActions();
        // Yield before any filesystem/process work so the checking state can paint.
        await Task.Yield();
        try
        {
            async Task<LibraryRequirement> Probe()
            {
                if (testRequirement is not null) return forImport ? (ActiveRequirementForVerification ?? testRequirement)() : testRequirement();
                if (!forImport) return OfficialLibraryRequirement.CheckInstalled(settings);
                bool started = managerLauncher.TryStart(settings);
                var elapsed = Stopwatch.StartNew();
                LibraryRequirement result;
                do
                {
                    result = managerLauncher.Confirm(OfficialLibraryRequirement.Check());
                    if (result.Ready || !result.Retryable || elapsed.ElapsedMilliseconds >= (started ? 5000 : 600)) break;
                    await Task.Delay(200);
                } while (true);
                return result;
            }
            var probe = forImport ? libraryProbeTask ??= Task.Run(Probe) : installationProbeTask ??= Task.Run(Probe);
            var requirement = await probe.WaitAsync(testRequirement is null ? TimeSpan.FromSeconds(15) : LibraryCheckTimeoutForVerification);
            if (requirement.Ready && requirement.Executable is string path && settings.OfficialManagerExecutable != path)
            { settings.OfficialManagerExecutable = path; Storage.Save("settings.json", settings); }
            libraryReady = requirement.Ready;
            if (forImport) confirmedFolder = requirement.Ready ? requirement.Folder : null;
            folderConfirmed = forImport && requirement.Ready;
            setupLibraryStatus.Text = "① " + requirement.Message;
            setupLibraryStatus.ForeColor = libraryReady ? CollectorTheme.Success : CollectorTheme.Warning;
            if (!libraryReady) { session.Suspend(); state.Status = requirement.Message; }
            else session.Resume();
            return libraryReady;
        }
        catch (TimeoutException)
        {
            libraryReady = false; confirmedFolder = null; folderConfirmed = false; session.Suspend();
            state.Status = "公式ライブラリの起動・確認に時間がかかっています。しばらくして再確認してください";
            setupLibraryStatus.Text = "① " + state.Status;
            setupLibraryStatus.ForeColor = CollectorTheme.Warning;
            return false;
        }
        catch
        {
            libraryReady = false; confirmedFolder = null; folderConfirmed = false; session.Suspend();
            setupLibraryStatus.Text = "① 公式ライブラリを確認できません。もう一度確認してください";
            setupLibraryStatus.ForeColor = CollectorTheme.Warning;
            throw;
        }
        finally
        {
            libraryChecking = false;
            if (forImport) { activeLibraryCheckTask = null; if (libraryProbeTask?.IsCompleted == true) libraryProbeTask = null; }
            else { libraryCheckTask = null; if (installationProbeTask?.IsCompleted == true) installationProbeTask = null; }
            if (libraryCheckButton is not null) libraryCheckButton.Text = "① 公式ライブラリを確認";
            RefreshSessionActions();
        }
    }
    void RefreshSessionActions()
    {
        if (libraryCheckButton is not null) libraryCheckButton.Enabled = !busy && !setupBusy && !libraryChecking;
        foreach (var action in sessionActions) action.Enabled = libraryReady && !busy && !setupBusy && !libraryChecking;
        if (logoutButton is not null) logoutButton.Enabled = !busy && !setupBusy && !libraryChecking && (session.Ready || Directory.Exists(session.ProfileFolder));
    }
    async Task SetupAction(Func<Task> action)
    {
        if (busy || setupBusy) { loginStatus.Text = "処理の完了を待ってから操作してください"; return; }
        setupBusy = true; RefreshSessionActions();
        try { await action(); }
        catch (Exception) { loginStatus.Text = "操作を完了できません。ログイン画面とWebView2 Runtimeを確認して、もう一度お試しください"; }
        finally { setupBusy = false; RefreshSessionActions(); }
    }
    async Task EnsureSession()
    {
        try { await (sessionInitialization ??= session.InitializeAsync()); }
        catch { sessionInitialization = null; throw; }
    }
    async Task VerifyImportConnection()
    {
        if (!File.Exists(Path.Combine(Storage.Root, "catalog.json"))) return;
        var catalog = Storage.Load<Feed>("catalog.json"); catalog.Validate();
        var receipt = state.ManagerImports.Values.Where(r => r.Confirmed && catalog.Items.Any(p => p.Id == r.ProductId && p.Matches(settings) && p.Variants.Any(v => v.Id == r.VariantId)))
            .OrderByDescending(r => r.At).FirstOrDefault();
        if (receipt is null) return;
        var product = catalog.Items.First(p => p.Id == receipt.ProductId);
        string html = await session.GetProductHtmlAsync(product.Url, CancellationToken.None);
        var plan = await Task.Run(() => SessionDownloadQueue.Plan(html, product));
        if (!plan.Any(f => f.FileId == receipt.FileId && f.VariantId == receipt.VariantId && f.Name == receipt.Name))
            throw new IOException("確認対象ファイルの無料条件が変わった可能性があります");
        // Validate one already-known free entrance without launching Manager or
        // downloading the file again. The capability never enters saved state.
        string uri = await session.GetImportAsync(receipt.FileId, receipt.VariantId, CancellationToken.None);
        ManagerIntegration.ValidateBoundLink(uri, receipt.ProductId, receipt.FileId, receipt.VariantId, receipt.Name);
        Storage.Save("session-import-verification.json", new { ok = true, page_plan_verified = true, checked_at = DateTimeOffset.UtcNow, product_id = receipt.ProductId, file_id = receipt.FileId });
        loginStatus.Text = "アプリ専用ログインと取り込み情報を確認しました（再ダウンロードなし）";
    }
    async Task Tick(bool resume = false)
    {
        if (busy || setupBusy) return;
        busy = true; RefreshSessionActions(); cancellation.Dispose(); cancellation = new();
        try
        {
            if (!await CheckLibrary()) return;
            if (!settings.Configured || !settings.SetupCompleted) { state.Status = "初回セットアップで公式ライブラリと専用ログインを確認してください"; return; }
            if (!session.Ready) { await EnsureSession(); if (settings.DedicatedSessionEnabled) await session.CheckLoginAsync(cancellation.Token); }
            if (session.NeedsLogin) { state.Status = "アプリ内のログインを確認してください"; return; }
            if (state.Due(DateTimeOffset.UtcNow)) await feed.Fetch(settings, state, cancellation.Token);
            if (OfficialLibraryRequirement.NeedsRunningManager(settings, state))
            {
                if (!await CheckLibrary(forImport: true)) return;
                await downloadQueue.StepAsync(settings, state, cancellation.Token);
            }
            else if (!settings.AutoDownload) state.Status = "自動ダウンロードを一時停止しています";
            if (settings.AutoDownload && state.Queue.All(p => !p.Matches(settings))) state.Status = "取得完了。次の新着確認まで待機しています";
        }
        catch (OperationCanceledException) { state.Status = "一時停止しました"; }
        catch (IOException ex) { state.Status = ex.Message; }
        catch (Exception) { state.Status = "処理を完了できません。ログイン状態・公式Manager・通信を確認してください"; }
        finally
        {
            busy = false; RefreshSessionActions(); Storage.Save("state.json", state); UpdateView();
            timer.Interval = DownloadScheduler.PollIntervalMs(state, settings, DateTimeOffset.UtcNow);
        }
    }

    void UpdateView()
    {
        status.Text = state.Status;
        string scheduleText = state.LastAttempt is null ? "次回の新着確認: 設定保存後" : $"次回の新着確認: {state.NextAttempt.ToLocalTime():yyyy/MM/dd HH:mm}　前回成功: {state.LastSuccess?.ToLocalTime():MM/dd HH:mm}";
        var now = DateTimeOffset.UtcNow;
        var deadlines = state.Queue.Where(p => p.Matches(settings) && state.ProductBatches.ContainsKey(p.Id))
            .Select(p => DownloadScheduler.Deadline(state, p.Id, now)).ToArray();
        if (deadlines.Length > 0)
        {
            var deadline = deadlines.Min();
            scheduleText += $"\n取得期限: {deadline.ToLocalTime():MM/dd HH:mm}　待機 {deadlines.Length}商品　取得中 {DownloadScheduler.Active(state, now)}/{DownloadScheduler.ConcurrentImports}";
            if (deadline < now) scheduleText += "　期限超過：取得結果を確認中";
        }
        if (schedule.Text != scheduleText) schedule.Text = scheduleText;
        folderLabel.Text = folderConfirmed ? "保存先: " + confirmedFolder : "保存先: 取得・一覧登録の直前に公式設定を確認します";
        libraryFolderStatus.Text = folderConfirmed ? confirmedFolder : "公式ライブラリで指定します（取得・一覧登録の直前に確認）";
        var rows = state.Queue.Where(p => p.Matches(settings)).ToList();
        var keys = rows.Select(p => (p.Id, p.Title, p.Shop)).ToArray();
        if (!keys.SequenceEqual(displayedRows))
        {
            string? selected = (grid.CurrentRow?.DataBoundItem as Product)?.Id;
            int first = grid.FirstDisplayedScrollingRowIndex;
            grid.SuspendLayout();
            grid.DataSource = rows; displayedRows = keys;
            if (selected is not null)
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.DataBoundItem is Product item && item.Id == selected) { grid.CurrentCell = row.Cells[0]; break; }
            if (first >= 0 && rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(first, rows.Count - 1);
            grid.ResumeLayout();
        }
        grid.Visible = rows.Count > 0; emptyQueue.Visible = rows.Count == 0;
    }
    void ShowApp() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void WndProc(ref Message message)
    {
        if (AppActivation.MessageId != 0 && message.Msg == AppActivation.MessageId) { ShowApp(); return; }
        base.WndProc(ref message);
    }
    internal async Task<bool> ActivateForVerification()
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        Hide(); AppActivation.Signal();
        for (int i = 0; i < 50 && !Visible; i++) await Task.Delay(20);
        return Visible;
    }
    internal bool LibraryCheckingForVerification => libraryChecking && setupLibraryStatus.Text.Contains("確認中") && libraryCheckButton?.Text == "確認中…" && !libraryCheckButton.Enabled;
    internal string LibraryStatusForVerification => setupLibraryStatus.Text;
    internal EmbeddedSession VerificationSession => session;
    internal async Task VerifyDependencyForTest()
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        if (await CheckLibrary()) await EnsureSession();
    }
    internal Task<bool> VerifyImportDependencyForTest()
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        return CheckLibrary(forImport: true);
    }
    internal void ShowLoginForVerification() { tabs.SelectedIndex = tabs.TabPages.Count - 1; ShowApp(); }
    internal bool LogoutAvailableForVerification => logoutButton?.Enabled == true;
    internal async Task WaitSetupIdleForVerification()
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        while (setupBusy) await Task.Delay(20);
    }
    internal bool MeasurePresentationForVerification(string output)
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        var savedQueue = state.Queue.ToArray(); string savedStatus = state.Status; var savedSuccess = state.LastSuccess;
        var latencies = new List<double>(); bool stable = true;
        try
        {
            state.Queue.Clear(); state.Queue.Add(new Product { Id = "1", Title = "サンプル衣装", Shop = "Sample Shop", AgeRating = "all_ages", Categories = ["衣装"] });
            tabs.SelectedIndex = 0; UpdateView(); PerformLayout();
            object? binding = grid.DataSource; Point origin = grid.PointToScreen(Point.Empty);
            for (int i = 0; i < 24; i++)
            {
                state.LastSuccess = DateTimeOffset.UtcNow.AddMinutes(i);
                state.Status = i % 2 == 0 ? "次の確認まで待機しています" : "保存結果を確認しています：" + new string('衣', 90);
                UpdateView();
                stable &= ReferenceEquals(binding, grid.DataSource) && grid.PointToScreen(Point.Empty) == origin && grid.CurrentRow?.DataBoundItem is Product { Id: "1" };
                var watch = Stopwatch.StartNew();
                tabs.SelectedIndex = i % tabs.TabPages.Count;
                tabs.Update(); watch.Stop(); latencies.Add(watch.Elapsed.TotalMilliseconds);
                tabs.SelectedIndex = 0;
            }
            state.Status = "保存結果を確認しています"; UpdateView();
            using var screenshot = new Bitmap(Width, Height);
            DrawToBitmap(screenshot, new Rectangle(0, 0, Width, Height)); screenshot.Save(Path.Combine(output, "queue-preview.png"));
            File.WriteAllText(Path.Combine(output, "presentation-performance.json"), JsonSerializer.Serialize(new { updates = 24,
                stable_layout_and_selection = stable, navigation_max_ms = latencies.Max(), navigation_average_ms = latencies.Average() }, Storage.Json));
            return stable;
        }
        finally
        {
            state.Queue.Clear(); state.Queue.AddRange(savedQueue); state.Status = savedStatus; state.LastSuccess = savedSuccess;
            UpdateView(); ShowLoginForVerification();
        }
    }
    internal async Task SetPausedForVerification(bool paused)
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        tabs.SelectedIndex = 0;
        (paused ? pauseButton : resumeButton)!.PerformClick();
        while (busy) await Task.Delay(20);
    }
    internal async Task InvokeSetupForVerification(bool logout)
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        if (logout) logoutButton!.PerformClick(); else ((Button)sessionActions[0]).PerformClick();
        while (setupBusy) await Task.Delay(20);
    }
    internal async Task CapturePresentationForVerification(string output)
    {
        if (testRequirement is null || Environment.GetEnvironmentVariable("BOOTH_COLLECTOR_TEST_STATE") is null) throw new InvalidOperationException();
        for (int i = 0; i < tabs.TabPages.Count; i++)
        {
            tabs.SelectedIndex = i;
            await Task.Delay(100);
            using var screenshot = new Bitmap(Width, Height);
            DrawToBitmap(screenshot, new Rectangle(0, 0, Width, Height));
            screenshot.Save(Path.Combine(output, $"screen-{i}.png"));
        }
        explanationToggle.PerformClick();
        await Task.Delay(100);
        using (var screenshot = new Bitmap(Width, Height))
        {
            DrawToBitmap(screenshot, new Rectangle(0, 0, Width, Height));
            screenshot.Save(Path.Combine(output, "explanations-on.png"));
        }
        Storage.Save("ui-presentation.json", new { explanations_enabled = settings.ShowExplanations,
            saved_preference = Storage.Load<Settings>("settings.json").ShowExplanations,
            toggle_text = explanationToggle.Text, background = BackColor.ToArgb() });
        explanationToggle.PerformClick();
    }
    internal void CloseForVerification() { exiting = true; Close(); }
    void Notify(string text) { tray.BalloonTipTitle = "BOOTH Free Downloader"; tray.BalloonTipText = text; tray.ShowBalloonTip(5000); }
    void Log(string text)
    {
        string line = $"{DateTime.Now:MM/dd HH:mm:ss}　{text}";
        events.Items.Insert(0, line); while (events.Items.Count > 300) events.Items.RemoveAt(events.Items.Count - 1);
    }
}
