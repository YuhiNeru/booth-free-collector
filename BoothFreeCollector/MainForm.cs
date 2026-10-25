using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace BoothFreeCollector;

public sealed class MainForm : Form
{
    readonly Settings settings;
    readonly CollectorState state;
    readonly FeedClient feed = new();
    readonly BridgeServer bridge;
    readonly DirectFileTransfer directFiles = new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30000 };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    readonly Label schedule = new() { AutoSize = true };
    readonly Label folderLabel = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    readonly ListBox events = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly CheckBox follow = new() { Text = "Library Manager の保存先に自動追従する（後からインストールした場合も）", AutoSize = true };
    readonly Label libraryFolderStatus = new() { AutoSize = true, MaximumSize = new Size(850, 0) };
    string fixedFolderDraft;
    readonly TextBox folder = new() { Width = 600 };
    readonly CheckBox auto = new() { Text = "新着の無料ファイルを自動ダウンロード", AutoSize = true };
    readonly CheckBox adult = new() { Text = "成人向け・年齢区分不明も対象にする（18歳以上）", AutoSize = true };
    readonly CheckBox existing = new() { Text = "初回に既存の無料商品も対象にする（大量のファイルを取得します）", AutoSize = true };
    readonly CheckBox startup = new() { Text = "Windows ログイン時に常駐する", AutoSize = true };
    readonly CheckBox browserHandoff = new() { Text = "Windows単体時は既定ブラウザへ自動で受け渡す（画面が開く場合があります）", AutoSize = true };
    readonly TextBox search = new() { Width = 500, PlaceholderText = "例: 衣装 Unity（スペース区切りでAND）" };
    readonly Dictionary<string, CheckBox> categoryChecks = [];
    readonly TextBox endpoint = new() { Width = 700 };
    DownloadEngine engine;
    CancellationTokenSource cancellation = new();
    bool busy, exiting;
    readonly bool startHidden;

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        settings = Storage.Load<Settings>("settings.json");
        fixedFolderDraft = settings.DownloadFolder;
        state = Storage.Load<CollectorState>("state.json");
        Text = "BOOTH Free Collector"; Width = 1030; Height = 770; MinimumSize = new Size(850, 650);
        Font = new Font("Yu Gothic UI", 10); StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        engine = new(Log);
        bridge = new(DispatchBridge);
        BuildUi();
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => ShowApp());
        menu.Items.Add("終了", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "BOOTH Free Collector", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowApp();
        timer.Tick += async (_, _) => await Tick();
        Shown += async (_, _) =>
        {
            try
            {
                await engine.Initialize(); BrowserBridge.Register(); bridge.Start(); timer.Start();
                if (!settings.Configured) { tabs.SelectedIndex = 1; ShowApp(); }
                else { if (startHidden) Hide(); else ShowApp(); await Tick(); }
            }
            catch (Exception ex) { Log("起動エラー: " + ex.Message); status.Text = "起動設定を確認してください"; }
        };
        FormClosing += (_, e) =>
        {
            if (!exiting) { e.Cancel = true; Hide(); return; }
            timer.Stop(); cancellation.Cancel(); engine.Stop(); tray.Visible = false;
        };
        FormClosed += (_, _) => { directFiles.Dispose(); bridge.Dispose(); engine.Dispose(); tray.Dispose(); feed.Dispose(); cancellation.Dispose(); };
        UpdateView();
    }
    static Button Button(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 5, 10, 5) }; b.Click += click; return b;
    }
    static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
    void BuildUi()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 98, Padding = new Padding(22, 16, 22, 12), BackColor = Color.FromArgb(33, 38, 54) };
        header.Controls.Add(new Label { Text = "BOOTH Free Collector", ForeColor = Color.White, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Location = new Point(22, 12) });
        header.Controls.Add(new Label { Text = "無料の新着を、12時間ごとに。", ForeColor = Color.FromArgb(195, 203, 220), AutoSize = true, Location = new Point(25, 57) });
        Controls.Add(tabs); Controls.Add(header);
        var overview = new TabPage("状況") { Padding = new Padding(20) };
        var overviewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); overviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        status.Font = new Font(Font, FontStyle.Bold); status.Margin = new Padding(0, 0, 0, 10);
        overviewLayout.Controls.Add(status); overviewLayout.Controls.Add(schedule); overviewLayout.Controls.Add(folderLabel);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(Button("予定を確認・待機分を再開", async (_, _) => { engine.Resume(); await Tick(true); }));
        actions.Controls.Add(Button("一時停止", (_, _) => { cancellation.Cancel(); engine.Stop(); settings.AutoDownload = false; auto.Checked = false; Storage.Save("settings.json", settings); Log("自動ダウンロードを一時停止しました"); }));
        actions.Controls.Add(Button("保存先を開く", (_, _) => { try { var p = settings.ResolveFolder(); if (Directory.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (IOException ex) { MessageBox.Show(this, ex.Message); } }));
        actions.Controls.Add(Button("保存済み一覧から過去分を追加", async (_, _) => await QueueHistory()));
        actions.Controls.Add(Button("取得済みファイルを登録", (_, _) => RegisterBrowserFile()));
        overviewLayout.Controls.Add(actions);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "待機中の商品", DataPropertyName = "Title", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ショップ", DataPropertyName = "Shop", Width = 220 });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].DataBoundItem is Product p && Feed.SafeId(p.Id)) Process.Start(new ProcessStartInfo("https://booth.pm/ja/items/" + p.Id) { UseShellExecute = true }); };
        overviewLayout.Controls.Add(grid); overviewLayout.Controls.Add(events); overview.Controls.Add(overviewLayout); tabs.TabPages.Add(overview);

        var config = new TabPage("設定") { Padding = new Padding(20) };
        var configScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(Label("保存先")); layout.Controls.Add(follow);
        var lib = LibraryInfo.Read();
        layout.Controls.Add(libraryFolderStatus);
        layout.Controls.Add(Label("未導入時は下の保存先を使います。チェックを外すと、表示中の保存先に固定します。"));
        var folderRow = new FlowLayoutPanel { AutoSize = true }; folderRow.Controls.Add(folder);
        folderRow.Controls.Add(Button("選択", (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) { follow.Checked = false; folder.Text = dialog.SelectedPath; } }));
        layout.Controls.Add(folderRow);
        layout.Controls.Add(Label("取得する商品")); layout.Controls.Add(auto); layout.Controls.Add(adult); layout.Controls.Add(existing);
        layout.Controls.Add(Label("キーワード（商品名・ショップ・タグ）")); layout.Controls.Add(search);
        layout.Controls.Add(Label("対象カテゴリは「ダウンロード対象」タブで選択できます。"));
        layout.Controls.Add(Label("配信URL")); layout.Controls.Add(endpoint);
        layout.Controls.Add(startup);
        layout.Controls.Add(browserHandoff);
        layout.Controls.Add(Label("一覧の取得は失敗時・再起動後も12時間に1度です。\n初回は保存済み一覧の対象カテゴリ、以降も選択カテゴリの新着を取得します。\n保存済みファイルを上書きせず、ZIPの展開や実行はしません。"));
        var save = Button("設定を保存して開始", async (_, _) => await SaveSettings());
        save.Dock = DockStyle.Bottom; save.Height = 44; save.BackColor = Color.FromArgb(223, 238, 255);
        configScroll.Controls.Add(layout); config.Controls.Add(configScroll); config.Controls.Add(save); tabs.TabPages.Add(config);
        BuildCategoryTab();
        var integration = new TabPage("ブラウザ連携") { Padding = new Padding(20) };
        var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        info.Controls.Add(Label("Windows単体: 認証不要のファイルは直接取得。認証が必要なら既定ブラウザへ受け渡します。\nブラウザで取得した後、状況タブの「取得済みファイルを登録」で保存先へ整理できます。\n\n拡張単体: ブラウザのダウンロードフォルダ配下へ保存。\n両方あり: 拡張からファイルデータを受け取り、Library Managerの保存先へ直接保存します。\n\nCookieやパスワードはアプリに渡さず、保存もしません。"));
        info.Controls.Add(Button("拡張のフォルダを開く", (_, _) => { string path = Path.Combine(AppContext.BaseDirectory, "extension"); if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }));
        info.Controls.Add(Button("既定ブラウザでBOOTHを開く", (_, _) => Process.Start(new ProcessStartInfo("https://accounts.booth.pm/") { UseShellExecute = true })));
        info.Controls.Add(Label("Chrome / Edge の拡張管理画面で「パッケージ化されていない拡張機能」を読み込みます。\n導入後は、拡張の画面から連携状態を確認できます。"));
        integration.Controls.Add(info); tabs.TabPages.Add(integration);
        follow.Checked = settings.FollowLibraryFolder;
        folder.Text = follow.Checked ? lib.Folder ?? fixedFolderDraft : fixedFolderDraft;
        follow.CheckedChanged += (_, _) =>
        {
            // Turning follow off pins the currently displayed location instead of jumping elsewhere.
            fixedFolderDraft = folder.Text;
            if (follow.Checked) folder.Text = LibraryInfo.Read().Folder ?? fixedFolderDraft;
            folder.Enabled = !follow.Checked;
        };
        folder.TextChanged += (_, _) => { if (!follow.Checked) fixedFolderDraft = folder.Text; };
        folder.Enabled = !follow.Checked;
        auto.Checked = settings.AutoDownload; adult.Checked = settings.IncludeAdult; existing.Checked = settings.IncludeExisting;
        browserHandoff.Checked = settings.BrowserHandoff;
        existing.Enabled = !state.BaselineCreated; startup.Checked = settings.Startup; search.Text = settings.Search; endpoint.Text = settings.FeedUrl;
    }
    void BuildCategoryTab()
    {
        var page = new TabPage("ダウンロード対象") { Padding = new Padding(20) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(Label("初回の過去分と12時間ごとの新着に共通の設定です。初期値は優先度「高」のみ。\nすべて外すとダウンロードしません。変更後は下の保存ボタンを押してください。"));
        foreach (string priority in new[] { "高", "中", "低" })
        {
            var group = new CheckBox { Text = "優先度：" + priority + "（まとめて選択）", AutoSize = true, ThreeState = true, AutoCheck = false, Margin = new Padding(0, 20, 0, 6) };
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
                var check = new CheckBox { Text = item.Label, AutoSize = true, Checked = settings.DownloadCategories.Contains(item.Id), Margin = new Padding(26, 3, 0, 3) };
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
        panel.Controls.Add(Label("カテゴリを追加した後、過去分も必要なら状況タブの「保存済み一覧から過去分を追加」を使ってください。\n拡張機能版の取得は衣装・髪型のみです。"));
        var save = Button("対象カテゴリを保存して開始", async (_, _) => await SaveSettings());
        save.Dock = DockStyle.Bottom; page.Controls.Add(panel); page.Controls.Add(save); tabs.TabPages.Add(page);
    }
    async Task SaveSettings()
    {
        if (busy) { MessageBox.Show(this, "取得が終わるか、一時停止してから設定を保存してください。"); return; }
        try
        {
            var uri = new Uri(endpoint.Text.Trim());
            if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/catalog.json") throw new IOException("HTTPS のカタログURLを指定してください");
            if (follow.Checked && LibraryInfo.Read().Error is string error) throw new IOException("Library Manager の設定を読めません: " + error);
            if (!Path.IsPathFullyQualified(fixedFolderDraft) || fixedFolderDraft.StartsWith(@"\\")) throw new IOException("保存先はローカルの絶対パスで指定してください");
            settings.FollowLibraryFolder = follow.Checked; settings.DownloadFolder = fixedFolderDraft;
            settings.AutoDownload = auto.Checked; settings.IncludeAdult = adult.Checked; settings.IncludeExisting = existing.Checked;
            settings.Search = search.Text.Trim(); settings.Category = ""; settings.FeedUrl = uri.AbsoluteUri;
            settings.DownloadCategories = categoryChecks.Where(c => c.Value.Checked).Select(c => c.Key).ToArray();
            settings.Startup = startup.Checked; settings.Configured = true;
            settings.BrowserHandoff = browserHandoff.Checked;
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
    async Task Tick(bool resume = false)
    {
        RefreshFolderStatus();
        if (busy || directFiles.Count > 0 || !settings.Configured || !engine.Ready) return;
        if (DateTimeOffset.UtcNow < bridgeActiveUntil) { UpdateView(); return; }
        busy = true;
        cancellation.Dispose(); cancellation = new();
        try
        {
            if (state.Due(DateTimeOffset.UtcNow))
            {
                await feed.Fetch(settings, state, cancellation.Token);
                Log(state.Status);
                if (state.Queue.Count > 0) Notify($"無料商品の待機分 {state.Queue.Count:N0}件");
            }
            UpdateView();
            // A failed batch waits until the next feed cycle or explicit resume.
            if (settings.AutoDownload && !engine.NeedsLogin && (resume || batchAttempt != state.LastAttempt))
            {
                batchAttempt = state.LastAttempt;
                foreach (var p in state.Queue.ToArray())
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!p.Matches(settings)) continue;
                    try
                    {
                        state.Status = "取得中: " + p.Title; UpdateView();
                        bool completed = await engine.DownloadProduct(p, settings, state, cancellation.Token);
                        if (!completed) { state.Status = "既定ブラウザの取得待ち。取得済みファイルを登録するか、拡張を接続してください"; break; }
                        state.Queue.Remove(p); Storage.Save("state.json", state); UpdateView();
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                    }
                    catch (IOException ex) { state.Status = ex.Message; Log(ex.Message); break; }
                }
                if (state.Queue.Count == 0) state.Status = "取得完了。次の新着確認まで待機しています";
            }
        }
        catch (OperationCanceledException) { state.Status = "一時停止しました"; }
        catch (Exception ex) { state.Status = "取得できません: " + ex.Message; Log(state.Status); }
        finally { busy = false; Storage.Save("state.json", state); UpdateView(); }
    }
    DateTimeOffset? batchAttempt;
    // Chrome's alarm runs once a minute. Give an already-running extension time
    // to share its last fetch before a fresh desktop installation performs I/O.
    DateTimeOffset bridgeActiveUntil = DateTimeOffset.UtcNow.AddSeconds(75);
    string? feedLease;
    readonly Dictionary<string, ImportTicket> importTickets = [];
    sealed record ImportTicket(string ProductId, string Key, string Name, string Relative, DateTimeOffset At);
    Task<object> DispatchBridge(JsonElement request)
    {
        var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() => { try { result.SetResult(HandleBridge(request)); } catch (Exception ex) { result.SetResult(new { desktop = true, ok = false, error = ex.Message }); } });
        return result.Task;
    }
    object HandleBridge(JsonElement request)
    {
        string Text(string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        string command = Text("command");
        bridgeActiveUntil = DateTimeOffset.UtcNow.AddSeconds(90);
        if (command == "status")
        {
            if (!busy && DateTimeOffset.TryParse(Text("last_attempt"), out var previous) && previous <= DateTimeOffset.UtcNow && (state.LastAttempt is null || previous > state.LastAttempt))
            { state.LastAttempt = previous; Storage.Save("state.json", state); }
            var library = LibraryInfo.Read();
            return new { desktop = true, ok = true, busy, settings.Configured, settings, state.LastAttempt, next_attempt = state.NextAttempt, state.GeneratedAt, pending_count = state.Queue.Count,
                app_version = Application.ProductVersion, library_folder = library.Folder, library_error = library.Error,
                download_folder = settings.FollowLibraryFolder ? library.Folder ?? settings.DownloadFolder : settings.DownloadFolder,
                saved_files = state.Files.Count, browser_pending = state.BrowserPending.Count, direct_stream = true, active_transfers = directFiles.Count };
        }
        if (busy) return new { desktop = true, ok = false, busy = true, error = "Windows側の処理完了を待っています" };
        if (!settings.Configured) return new { desktop = true, ok = false, error = "Windows側で初期設定を保存してください" };
        if (command == "reserve")
        {
            bool allowed = state.Due(DateTimeOffset.UtcNow);
            if (allowed) { state.LastAttempt = DateTimeOffset.UtcNow; feedLease = Guid.NewGuid().ToString("N"); Storage.Save("state.json", state); }
            return new { desktop = true, ok = true, allowed, lease = allowed ? feedLease : null, state.Etag, state.LastAttempt };
        }
        if (command == "feed")
        {
            if (feedLease is null || Text("lease") != feedLease) throw new IOException("一覧取得の予約が一致しません");
            if (request.TryGetProperty("not_modified", out var nm) && nm.GetBoolean()) { state.LastSuccess = DateTimeOffset.UtcNow; Storage.Save("state.json", state); }
            else
            {
                var data = request.GetProperty("feed").Deserialize<Feed>(Storage.Json) ?? throw new IOException("一覧が空です");
                FeedClient.Apply(settings, state, data, Text("etag"));
            }
            feedLease = null; UpdateView(); return new { desktop = true, ok = true };
        }
        if (command == "sync_cache")
        {
            var cached = request.GetProperty("feed").Deserialize<Feed>(Storage.Json) ?? throw new IOException("一覧が空です");
            cached.Validate();
            if (state.GeneratedAt is null || DateTimeOffset.Parse(cached.GeneratedAt) > DateTimeOffset.Parse(state.GeneratedAt)) FeedClient.Apply(settings, state, cached);
            UpdateView(); return new { desktop = true, ok = true };
        }
        if (command == "catalog")
        {
            if (!File.Exists(Path.Combine(Storage.Root, "catalog.json"))) return new { desktop = true, ok = true, items = Array.Empty<Product>(), total = 0 };
            var catalog = Storage.Load<Feed>("catalog.json");
            int offset = request.TryGetProperty("offset", out var offsetValue) ? Math.Max(0, offsetValue.GetInt32()) : 0;
            return new { desktop = true, ok = true, catalog.GeneratedAt, items = catalog.Items.Skip(offset).Take(100), total = catalog.Items.Length };
        }
        bool highOnly = request.TryGetProperty("high_priority_only", out var highValue) && highValue.ValueKind == JsonValueKind.True;
        if (command == "jobs") return new { desktop = true, ok = true, auto_download = settings.AutoDownload, items = state.Queue.Where(p => p.Matches(settings) && (!highOnly || DownloadCategories.HighPriority(p))).Take(10).ToArray() };
        if (command == "pending") return new { desktop = true, ok = true, product_ids = state.Queue.Where(p => p.Matches(settings)).Select(p => p.Id).Distinct().ToArray() };
        if (command == "sync_pending")
        {
            var ids = request.GetProperty("product_ids").Deserialize<string[]>() ?? [];
            if (ids.Length > 100 || ids.Any(id => !Feed.SafeId(id))) throw new IOException("待機分の形式が不正です");
            FeedClient.MergePending(settings, state, Storage.Load<Feed>("catalog.json"), ids);
            Storage.Save("state.json", state); UpdateView(); return new { desktop = true, ok = true };
        }
        if (command == "history")
        {
            var pending = state.Queue.Select(p => p.Id).ToHashSet();
            foreach (var product in Storage.Load<Feed>("catalog.json").Items.Where(p => p.Matches(settings) && (!highOnly || DownloadCategories.HighPriority(p)))) if (pending.Add(product.Id)) state.Queue.Add(product);
            Storage.Save("state.json", state); UpdateView(); return new { desktop = true, ok = true, count = state.Queue.Count };
        }
        if (command == "prepare")
        {
            string id = Text("product_id"), fileId = Text("file_id"), variant = Text("variant_id"), name = DownloadEngine.SafeFileName(Text("name"));
            if (!Feed.SafeId(id) || !Feed.SafeId(fileId) || !Feed.SafeId(variant)) throw new IOException("ファイルIDが不正です");
            var product = Storage.Load<Feed>("catalog.json").Items.FirstOrDefault(p => p.Id == id && p.Variants.Any(v => v.Id == variant));
            if (product is null || !product.Matches(settings)) throw new IOException("取得対象の商品ではありません");
            string key = id + "/" + fileId;
            bool skip = DownloadEngine.HasSaved(state, key) || LibraryInfo.Read().HasLocalDownload(id, name, DownloadEngine.ResolveRoot(settings));
            if (skip) return new { desktop = true, ok = true, skip = true };
            string ticket = Guid.NewGuid().ToString("N"), relative = $"BOOTH-Free-Collector/b{id}/{fileId}/{name}";
            importTickets[ticket] = new(id, key, name, relative, DateTimeOffset.UtcNow);
            return new { desktop = true, ok = true, skip = false, ticket, relative_filename = relative, direct_stream = true };
        }
        if (command is "begin_file" or "file_chunk" or "finish_file" or "abort_file")
        {
            string ticketId = Text("ticket");
            if (!importTickets.TryGetValue(ticketId, out var ticket) || DateTimeOffset.UtcNow - ticket.At > TimeSpan.FromDays(1)) throw new IOException("保存予約を取り直してください");
            if (command == "abort_file") { directFiles.Abort(ticketId); importTickets.Remove(ticketId); return new { desktop = true, ok = true }; }
            try
            {
                if ((command is "begin_file" or "file_chunk") && !settings.AutoDownload) throw new IOException("自動ダウンロードが一時停止中です");
                if (command == "begin_file")
                {
                    long? expected = request.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null;
                    string path = directFiles.Begin(ticketId, ticket.Key, ticket.ProductId, ticket.Name, DownloadEngine.ResolveRoot(settings), expected);
                    state.Status = "Library Managerの保存先へ直接保存中: " + ticket.Name; UpdateView();
                    return new { desktop = true, ok = true, path };
                }
                if (command == "file_chunk") return new { desktop = true, ok = true, received = directFiles.Append(ticketId, request.GetProperty("offset").GetInt64(), Text("data")) };
                var saved = directFiles.Finish(ticketId, request.GetProperty("size").GetInt64(), state);
                importTickets.Remove(ticketId); state.Status = "直接保存しました: " + ticket.Name; Log(state.Status); UpdateView();
                return new { desktop = true, ok = true, path = saved.Path, sha256 = saved.Sha256, size = saved.Size };
            }
            catch { directFiles.Abort(ticketId); throw; }
        }
        if (command == "complete")
        {
            string ticketId = Text("ticket");
            if (!importTickets.TryGetValue(ticketId, out var ticket) || DateTimeOffset.UtcNow - ticket.At > TimeSpan.FromDays(1)) throw new IOException("保存予約を取り直してください");
            string source = Path.GetFullPath(Text("path"));
            if (!DownloadEngine.MatchesBrowserFile(source, ticket.Relative, settings.BrowserDownloadsFolder, ticket.Name)) throw new IOException("拡張のダウンロードファイルではありません");
            DownloadEngine.ImportFile(settings, state, ticket.Key, ticket.ProductId, ticket.Name, source);
            importTickets.Remove(ticketId); Log("拡張から整理しました: " + ticket.Name); UpdateView();
            return new { desktop = true, ok = true, path = state.Files[ticket.Key].Path };
        }
        if (command == "finish") { state.Queue.RemoveAll(p => p.Id == Text("product_id")); Storage.Save("state.json", state); UpdateView(); return new { desktop = true, ok = true }; }
        throw new IOException("未対応の連携操作です");
    }
    void RegisterBrowserFile()
    {
        if (busy) { MessageBox.Show(this, "処理が終わるか一時停止してから登録してください"); return; }
        string? id = (grid.CurrentRow?.DataBoundItem as Product)?.Id;
        var candidates = state.BrowserPending.Where(p => id is null || p.Value.ProductId == id).ToArray();
        if (candidates.Length == 0) { MessageBox.Show(this, "ブラウザへ受け渡した待機ファイルはありません"); return; }
        var pending = candidates[0];
        using var dialog = new OpenFileDialog { Title = "取得済みファイルを選択: " + pending.Value.Name, FileName = pending.Value.Name, InitialDirectory = settings.BrowserDownloadsFolder };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { DownloadEngine.ImportFile(settings, state, pending.Key, pending.Value.ProductId, pending.Value.Name, dialog.FileName); state.Status = "ファイルを登録しました。再開で次を処理できます"; UpdateView(); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message); }
    }
    void UpdateView()
    {
        status.Text = state.Status;
        schedule.Text = state.LastAttempt is null ? "次回の新着確認: 設定保存後" : $"次回の新着確認: {state.NextAttempt.ToLocalTime():yyyy/MM/dd HH:mm}　前回成功: {state.LastSuccess?.ToLocalTime():MM/dd HH:mm}";
        try { folderLabel.Text = "保存先: " + settings.ResolveFolder(); }
        catch (IOException) { folderLabel.Text = "保存先: Library Managerの設定の読み取り待ち"; }
        RefreshFolderStatus();
        grid.DataSource = null; grid.DataSource = state.Queue.Where(p => p.Matches(settings)).ToList();
    }
    void RefreshFolderStatus()
    {
        var library = LibraryInfo.Read();
        libraryFolderStatus.Text = library.Error is not null ? "Library Managerの設定を読み取れません。自動追従ONの場合は取得を停止します。"
            : library.Folder is null ? "Library Managerは未導入または初期設定前です。固定の保存先で使用できます。"
            : "現在のLibrary Manager設定: " + library.Folder;
        if (follow.Checked && library.Error is null) folder.Text = library.Folder ?? fixedFolderDraft;
    }
    void ShowApp() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Notify(string text) { tray.BalloonTipTitle = "BOOTH Free Collector"; tray.BalloonTipText = text; tray.ShowBalloonTip(5000); }
    void Log(string text)
    {
        string line = $"{DateTime.Now:MM/dd HH:mm:ss}　{text}";
        events.Items.Insert(0, line); while (events.Items.Count > 300) events.Items.RemoveAt(events.Items.Count - 1);
    }
}
