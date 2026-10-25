# BOOTH Free Collector v2.5

BOOTHの無料ファイルを取得・保存するWindows常駐アプリです。付属のブラウザ拡張と連携し、普段のブラウザの認証を使います。BOOTH・pixivの非公式ツールです。

## ダウンロード

[GitHub Releases](https://github.com/YuhiNeru/booth-free-collector/releases/latest) のAssetsから選んでください。

- `BoothFreeCollector-win-x64-v2.5.0.zip`：Windows x64の常駐アプリ。ブラウザ拡張も同梱。.NETの別途導入は不要です。
- `BoothFreeCollector-extension-v2.5.0.zip`：ブラウザ拡張だけで使う場合。
- `SHA256SUMS.txt`：配布ZIPのSHA-256。

GitHubの「Source code」は開発用です。アプリを使う場合は上記のZIPを選びます。

## 初回セットアップ

1. Windows版ZIPを展開し、`BoothFreeCollector.exe` を起動します。
2. 「設定」で保存先を確認します。公式BOOTH Library Managerが設定済みなら、その保存先へ追従できます。未導入なら `%LOCALAPPDATA%\BOOTHLibraryManager` を使うか、自分でフォルダを指定します。
3. 「ダウンロード対象」は衣装・髪型が初期ONです。拡張が取得するのは連携中も衣装・髪型のみです。ほかのカテゴリはWindows単体の取得経路になります。
4. Chromeの `chrome://extensions` を開き、開発者モードをON。「パッケージ化されていない拡張機能を読み込む」で同梱の `extension` フォルダを選びます。
5. 普段のブラウザでBOOTHにログインします。常駐アプリ内にID・パスワードを入力しません。
6. 拡張の「新着の確認を有効にする」「無料ファイルを自動ダウンロードする」「Windowsアプリがあれば自動で連携する」を確認して保存します。

初回の過去商品取得は初期ONです。大量になる場合は、過去分をOFFにするかキーワードで絞ってください。拡張の更新後は拡張管理画面で再読み込みします。Edgeは同じフォルダを登録できますが、Edge自体での実動作検証は未実施です。

## 構成と動く場所

| 場所 | 部品 | 役割 |
| --- | --- | --- |
| 作者のVPS | VPSクローラー（別プロジェクト `booth-free-radar`） | 公開商品の情報を収集。商品ファイルは取得しません |
| Cloudflare | Workers＋KV | 公開情報から作った商品一覧を配信 |
| 利用者PCのブラウザ | 付属拡張 | 本人のブラウザ認証で無料ファイルを取得 |
| 利用者のWindows PC | 常駐アプリ（BOOTH Free Collector） | 重複確認、保存、履歴管理 |
| 利用者のWindows PC | 公式BOOTH Library Manager（任意） | 公式のファイル管理。常駐アプリは保存先などを読み取り専用で参照 |

衣装のZIPなどはBOOTHから利用者PCへ届きます。利用者がVPSを契約したりクローラーを設置したりする必要はありません。

## 保存と停止

- 拡張＋常駐アプリ：拡張が取得したファイルデータを最大192KiBずつNative Messagingで同じPCの常駐アプリへ転送し、選択フォルダの `b<商品ID>/<元ファイル名>` に直接保存します。ブラウザのDownloadsは経由しません。
- 拡張単体：ブラウザのダウンロード先配下の `BOOTH-Free-Collector` へ保存します。
- Windows単体：認証不要なら直接取得。認証やリダイレクトが必要なら設定に応じて既定ブラウザへ一度渡し、取得後に「取得済みファイルを登録」で整理します。受け渡しだけで取得成功にはしません。
- 一覧取得は前回の試行から12時間以上空けます。手動再開・再起動・失敗時にも短縮しません。PCのスリープ中やブラウザの完全終了中は定刻実行を保証しません。
- 常駐アプリの×は通知領域への格納です。完全終了は通知領域アイコンの右クリックから。自動起動は設定で選べます。
- 常駐アプリの「一時停止」、または拡張の自動ダウンロード設定OFFで停止できます。

## 認証・データの扱い

Cookieを読む拡張権限はありません。ブラウザが認証付き通信を行い、Cookie値、パスワード、Authorizationヘッダー、署名付き最終ダウンロードURLを常駐アプリへ渡す処理はありません。PC内の接続は指定拡張IDのNative Messagingと現在のWindowsユーザーに限定した名前付きパイプを使います。ローカルHTTPポートは開きません。

一覧取得のHTTP通信先にはIPアドレスなどの通信情報が見えます。購入履歴やローカル保存履歴を作者のサーバーへ送る処理は設けていません。常駐アプリの設定・履歴は `%LOCALAPPDATA%\BoothFreeCollector` に保存します。

無料リンクと対象バリエーションを照合し、有料購入・カート操作は行いません。ログインやアクセス確認が必要なら停止し、本人の操作を待ちます。同名で内容が異なるファイルは上書きしません。ファイルは1件2GiB、Windows側は空き容量512MiB未満で停止します。アーカイブの展開・取得ファイルの実行は行いません。

公式Library ManagerのDBを書き換えたり、画面へ商品を自動登録したりはしません。同じ保存先に置くことと公式アプリへの登録は別です。必要なら公式のインポート機能を使ってください。

安全性や全商品の取得を保証するものではありません。無料の説明書・サンプルも存在し、商品ごとの利用条件は本人が確認してください。公式告知を個別の公認と解釈していません。

## ソースとビルド

- `BoothFreeCollector/`：Windows常駐アプリ（.NET 10 / Windows Forms）
- `extension/`：付属ブラウザ拡張（Manifest V3）
- `cloud/`：一覧配信Workerと設定例
- `deploy/`：VPS上の一覧書き出し・巡回間隔制御のコード
- `tests/`：既存テスト。`VALIDATION.md` に確認範囲を記載

```powershell
dotnet build BoothFreeCollector/BoothFreeCollector.csproj
dotnet publish BoothFreeCollector/BoothFreeCollector.csproj -c Release -r win-x64 --self-contained true -o dist/BoothFreeCollector
python -m unittest discover -s tests -v
node --test tests/extension.test.mjs tests/worker.test.mjs tests/handoff.test.mjs tests/categories.test.mjs tests/direct.test.mjs
```

配布用には `extension/` とこのREADMEを公開出力に同梱します。常駐アプリの自己テストは `BoothFreeCollector.exe --self-test <専用フォルダ>`。実際の設定・履歴があるフォルダを指定しないでください。

VPSクローラー本体は別プロジェクトです。このリポジトリだけでクローラーを新規設置する構成ではありません。利用者は既定の公開カタログを使えます。作者固有のVPS導入スクリプト、認証情報、実設定・履歴、公式アプリのインストーラーは公開物に含めていません。Cloudflare設定例のIDは自分の環境の値へ変更してください。

[図付きの解説記事](https://zenn.dev/yuhineru/articles/booth-free-collector-design) / [作者HP](https://yuhineru.com/) / [BOOTHの公式告知](https://booth.pm/announcements/863) / [公式Library Manager](https://booth.pm/announcements/893)
