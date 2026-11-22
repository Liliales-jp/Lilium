# Lilium V1.0 Storeパッケージ検証（2026-11-22）

## 現在の判定

Store提出用の正式IDを使ったx64パッケージの作成は完了。開発環境でのパッケージID付き起動と主要操作、署名済みMSIXの通常インストール、Windows App Certification Kit（WACK）の実行とレポート確認を実施済み。別のクリーンなVMでも、ユーザーからインストール・初回起動・V1.0表示・画像／PDF／ZIP閲覧の確認完了報告を受領した。WACKの総合結果はWARNING（22 PASS、任意項目1 FAIL、必須項目1 WARNING）。必須項目のFAILはない。高DPI警告は埋め込みマニフェストと実行時APIでPerMonitorV2を別途確認し、DPI対応不足という判定は再現しなかった。通常インストールしたStore版での設定・キャッシュ・操作記録の保存と既存版からの移行は未確認であり、検証範囲を区別する。

## 正式IDとバージョン

| 項目 | 値 |
|---|---|
| Identity Name | Liliales.Lilium |
| Identity Publisher | CN=41F1C16E-24D1-45BD-9222-F8AD2E499279 |
| PublisherDisplayName | Liliales |
| パッケージバージョン | 1.0.0.0 |
| アプリバージョン | 1.0.0 |
| 設定画面の表示（日英） | Lilium / V1.0 |
| アーキテクチャ | x64 |
| 最低OS（マニフェスト） | 10.0.17763.0 |
| Package family | Liliales.Lilium_0zddy2tc2chrw |

IDはユーザーから提供されたPartner Centerの値に一致する。ユーザーからPartner Centerへのパッケージアップロードと申請完了の報告を受領しており、現在は審査待ち。CodexによるPartner Center画面との直接照合は実施していない。

## 成果物

Store提出用（署名なし）：

`AppPackages/Store-1.0.0.0-x64-20261122-114603/Lilium_1.0.0.0_x64.msixupload`

- ファイルサイズ：65,736,575 bytes
- SHA-256：`C49AD9573192590BF8BED1AB6504F24095F8AE853265A4F55E283BA8B781813D`
- 内包するMSIX：`Lilium_1.0.0.0_x64.msix`（66,245,568 bytes）
- 検証用署名済みパッケージ：`AppPackages/VM-Test-x64-20261122-114653.zip`
- 検証時の展開先・バックアップ：`AppPackages/Validation-1.0/`

提出用と署名済み検証用の全250エントリーを比較し、元からあるペイロードの変更はなし。検証用には署名とCodeIntegrityカタログの2エントリーのみ追加されている。Store提出用ファイル自体は署名していない。

## 実施済み

| 確認 | 結果 |
|---|---|
| .NET SDK | 10.0.401 |
| Store Releaseビルド・MSIX生成 | 成功、最終生成時に警告・エラーなし |
| 通常Debugビルド | 成功、警告0・エラー0 |
| 既存自動チェック | 331件通過（ThumbnailRefresh 98、Preview 218、FolderCoverSearch 15） |
| MSBuild Compile項目 | 59ソース、Tests配下のソース0件 |
| パッケージ内容 | Tests、Tools、_external、ソース、PDB、秘密鍵・証明書、DB、ログの混入なし |
| 提出用コンテナー | 内包MSIXのSHA-256が元の生成物と一致 |
| Windows SDK MakeAppxによる展開 | 成功、検証を無効にするオプションは使用せず |
| パッケージ用PNG | 44×44、150×150、StoreLogo 50×50 |
| .NET同梱 | coreclr.dll、hostpolicy.dll、runtimeconfigのincludedFrameworksを確認 |
| .NET実行時のロード先 | パッケージ展開先のcoreclr.dll／hostpolicy.dll |
| Windows App SDK | 依存フレームワークMicrosoft.WindowsAppRuntime.2、最低2.4.0.0を宣言 |
| パッケージID付き登録 | Add-AppxPackage -Registerで成功、Status=Ok |
| 既存設定の読み込み | 日本語設定と直前のフォルダーを読み込んで起動 |
| 設定画面 | 日本語・英語ともV1.0を表示、Gitハッシュや開発版表記なし |
| 設定保存 | 英語に変更して終了・再起動し、英語表示とフォルダー保持を確認 |
| 画像 | JPGのプレビューと全画面表示 |
| PDF | 8ページのPDFのプレビュー、1→2ページの移動 |
| ZIP | ZIP内JPGのプレビュー |
| ファイルコピー | 検証画像を選択フォルダーへコピーし、SHA-256一致 |
| 名前変更 | コピーした画像をrenamed.jpgへ変更 |
| ごみ箱・取り消し | ごみ箱へ移動し操作記録Complete、Ctrl+Zで復元しUndoneとSHA-256一致 |
| クラッシュ記録 | 検証中の新規追記なし |
| 署名済みMSIXの通常インストール | 管理者による認証スクリプトで成功、WindowsApps配下へ導入、SignatureKind=Developer |
| WACK | 10.0.22621.3233、24項目、総合WARNING。必須13項目は12 PASS・1 WARNING |
| 高DPI追加確認 | 検査対象EXEとSHA-256一致、埋め込みマニフェストと実行時APIの両方でPerMonitorV2を確認 |
| クリーンな別VMでのインストール・初回起動 | ユーザー確認済み。同梱の依存ランタイムと本体を分けて導入する手順を案内後、D-4まで完了の報告 |
| クリーンな別VMでの基本動作 | ユーザー確認済み。追加ランタイム導入の要求なし、V1.0表示、画像・PDF・ZIP閲覧 |

実画面テスト終了後に、元の設定DBと操作記録を事前バックアップから復元し、設定DBのSHA-256一致を確認。開発用の展開登録は解除済み。テスト中のDB・操作記録は`AppPackages/Validation-1.0/AfterTests/`へ保全している。証明書の信頼設定は、この開発用登録の検証では変更していない。

Codexによる実画面の検証はWindows 11（OS build 22621）の開発用VMで実施。実画面の操作は既存の開発者モードを利用して提出用MSIXを展開・登録した状態で確認した。これとは別に、管理者による認証スクリプトで署名済みMSIXをWindowsApps配下へ通常インストールし、WACKを実行した。ここまでは.NETとWindows App SDKがインストール済みの開発用VMであり、実画面検証時にロードされたWindows App SDKフレームワークは2.7.0.0だった。その後、ユーザーが別のクリーンなVMへ同梱の依存ランタイム2.4.0.0と署名済み本体を導入し、初回起動と基本閲覧を確認した。別VMの詳細なOSビルドと実行時モジュール一覧は取得していない。

WACKレポートは`AppPackages/Certification-20261122-120836/WackReport.xml`。結果の解釈と証拠は[認証結果の確認記録](store-certification-1.0.md)に記載した。認証後のテストアプリ登録解除と、一時追加した証明書の削除も確認済み。

## 未完了

1. 実インストール先での設定・キャッシュ・操作記録の保存と、非パッケージ版からの移行。開発用展開登録での設定読み込みを、通常インストールの移行検証の代わりにしない。ユーザーのD-4完了報告は初回起動・V1.0表示・基本閲覧についての確認として扱う。
2. Windows 10での動作、およびキーボード・スクリーンリーダーを使った通しのアクセシビリティ検証。

残りの実行手順は[VM検証手順](vm-test-package.md)を参照。

クリーンな別VMでの最初の同時インストールでは、`Add-AppxPackage`が`0x80073CF0`／`0x80070003`（AppxManifest.xmlの参照先が見つからない）で失敗した。ActivityIdは`0799a2fe-7ab9-0008-dd31-9a07b97add01`。以下はその時点の診断記録であり、最終結果は分離導入手順の案内後にユーザーが確認したインストール・D-4までの基本動作成功である。

ユーザー提供の`AppPackages/ps.txt`で、本体MSIX（66,257,054 bytes、SHA-256 `B80BE84207A6CF0567F9908B80DCBD56251B0E9E892FA6272A795A11C6321F79`）と依存MSIX（46,787,781 bytes、SHA-256 `A3CE5B76713133DFD3B378E81C43A89954C664FCD70FD0C070E409ED3DE03EBF`）が送付元と完全一致することを確認。ログ上も両パッケージの署名検証とstreaming reader生成は成功している。失敗箇所は依存パッケージ`Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe`についての`Package folder doesn't exist`（イベント486、0x80070490）であり、入力ファイル欠落や転送時破損ではない。既存登録・ステージング情報と実体の不整合が疑われるが、発生原因は特定していない。依存ランタイムを先に単独導入し、成功後に本体を導入する手順を案内したところ、ユーザーからD-4まで動作確認できたとの報告を受領した。Lilium本体やStore提出用パッケージの変更は不要だった。

## ビルド方法の変更

既存スクリプトが選択したVS2022のMSBuildは.NET 8までしか扱えず、NETSDK1045で失敗した。.NET 10のMSBuildではMSIX本体は作成できるが、標準のStoreコンテナー・シンボル生成タスクは不足コンポーネントにより失敗した。そのため、.NET 10 MSBuildで署名なしMSIXを作り、[Microsoftが公開している方法](https://learn.microsoft.com/windows/msix/package/packaging-uwp-apps)でZIP形式の提出用コンテナーを作成する構成に変更した。

Store用ビルドではPDBと任意の.appxsymを生成しない。Partner Centerでのシンボルに基づくクラッシュ解析は利用できない。アプリのローカル診断ログは維持している。

テストプロジェクトと検証用パッケージ作成スクリプトは開発・保守のためリポジトリに残し、配布するMSIXから除外している。
