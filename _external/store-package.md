# Microsoft Store向けパッケージ

LiliumのStore配布には、単一プロジェクトのMSIXを使用します。通常の開発ビルドは従来どおり非パッケージ型です。`Tools/Build-StorePackage.ps1`はStore向けビルドを有効にし、.NET 10を自己完結型で含め、Windows App SDKの共有フレームワークを使用します。Store向けビルドの対象はx64のみです。

1. アプリ名の予約は完了済みです。`Package.appxmanifest`にはPartner Centerから提供された正式な値（Name: `Liliales.Lilium`、Publisher: `CN=41F1C16E-24D1-45BD-9222-F8AD2E499279`、PublisherDisplayName: `Liliales`）を設定しています。初回公開のパッケージバージョンは`1.0.0.0`、アプリ本体は`1.0.0`、設定画面の表示は`V1.0`です。更新を提出するときはパッケージバージョンを上げます。
2. .NET 10 SDK、Windows SDKのビルドツール、NuGetに接続できるPCで、`C:\Dev\Lilium`からPowerShell 7を使い、`pwsh -File Tools/Build-StorePackage.ps1`を実行します。スクリプトは.NET 10のMSBuildでRelease版の署名なしMSIXを生成し、[Microsoftの手順](https://learn.microsoft.com/windows/msix/package/packaging-uwp-apps)に従ってStore提出用の`.msixupload`にまとめます。出力先は毎回新しい`AppPackages/Store-バージョン-x64-日時/`です。Storeへの提出にローカルの署名証明書は不要です。
3. アプリ名の予約前にパッケージ生成だけを検証する場合は、`pwsh -File Tools/Build-StorePackage.ps1 -AllowPlaceholderIdentity`を実行します。この出力はビルド検証用であり、Storeには提出できません。署名なしのMSIXをユーザーが直接インストールすることもできません。
4. 提出前に、署名したテスト用パッケージをインストールするか、Storeの限定公開テストを使って、クリーンなWindowsで動作確認します。.NETやWindows App SDKのインストールを求められずに初回起動できること、フォルダーの参照とファイル操作、PDF/ZIPの閲覧、設定・キャッシュ・操作記録の保持を確認してください。MSIXでは`LocalAppData`への新規書き込みがリダイレクトされる場合があるため、非パッケージ版からの移行も確認します。

Liliumはデスクトップのファイル操作を行うため、パッケージマニフェストで`runFullTrust`を宣言しています。Store向けビルドでは、Windows App SDKのビルドターゲットがWindows App SDK共有フレームワークへの依存関係を追加します。

Store用ビルドはデバッグシンボルを生成しません。提出用コンテナーにも任意のシンボルファイルを同梱していないため、Partner Centerでのシンボルに基づくクラッシュ解析は利用できません。ローカルの診断ログ機能はそのまま利用できます。

`Tests/`の自動チェックは本体のコンパイル対象から除外しています。スクリプトはMSIX内にテスト・開発用フォルダー、ソースコード、ビルドスクリプト、PDB、証明書、設定DB、ログが混入していないことと、正式ID・ランタイム同梱・Windows App SDK依存関係を確認します。`_Test`という名前の出力フォルダーやVM検証用ZIPはStore提出物ではありません。提出するファイルは`.msixupload`です。

2026-11-22の作成・検証結果と未実施項目は[検証記録](store-validation-1.0.md)を参照してください。

Store登録前にクリーンな仮想環境へインストールする場合は、[テスト用MSIXの手順](vm-test-package.md)を参照してください。
