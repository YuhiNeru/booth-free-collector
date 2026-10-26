# v3の検証

```powershell
dotnet publish BoothFreeCollector -c Release -r win-x64 --self-contained true -o dist/test-runtime
node --test tests/worker.test.mjs
python -m unittest discover -s tests -v
```

配布用実行ファイルの `--self-test <新しい専用フォルダ>` はローカル自己検証です。`--session-self-test <新しい専用フォルダ>` は、実WebView2を使った専用セッションと画面の隔離検証です。GUI実行ファイルの終了を待ち、出力先の `result.json` を確認します。

既存のユーザーデータや保存先をテスト出力に指定しないでください。セッション検証は固有のプロファイル、架空Cookie、アプリ内で差し替えた模擬BOOTH応答を使用し、本人の認証・実商品の取得・公式Managerの操作を行いません。公式ツール未確認時にログイン領域を作らないことも確認します。

`test_crawl_gate.py` の2件はLinux専用です。実クローラーの呼び出しを模擬化し、一時フォルダで失敗後の12時間制限と排他を確認します。Windowsではスキップされます。

v2の拡張・Native Messagingを用いたブラウザ検証は、v3の本番経路の検証には使いません。ローカル自己検証に残る旧方式の補助関数のテストは、移行用コードの回帰確認です。実機の本人ログイン・保存先・ファイルの確認結果は、VALIDATION.mdで隔離検証と分けて記録します。
