# Microsoft Store向けパッケージ

LiliumのStore配布には、単一プロジェクトのMSIXを使用します。通常の開発ビルドは従来どおり非パッケージ型です。`Tools/Build-StorePackage.ps1`はStore向けビルドを有効にし、.NET 10を自己完結型で含め、Windows App SDKの共有フレームワークを使用します。Store向けビルドの対象はx64のみです。

1. Partner Centerでアプリ名を予約します。`Package.appxmanifest`にある仮の`Identity Name`と`Publisher`を、Partner Centerで割り当てられたPackage/Identityの値と完全に一致するよう変更します。`PublisherDisplayName`にはStore掲載時の発行元名を設定します。マニフェストの4桁の数値バージョンは、提出のたびに上げてください。
2. Visual Studio 2026、Windows App SDK/MSIXのビルドツール、.NET 10 SDKを備え、NuGetに接続できるPCで、このディレクトリから`pwsh -File Tools/Build-StorePackage.ps1`を実行します。スクリプトはPartner Center提出用の署名なし`.msixupload`を`AppPackages/`に出力します。Storeへの提出にローカルの署名証明書は不要です。
3. アプリ名の予約前にパッケージ生成だけを検証する場合は、`pwsh -File Tools/Build-StorePackage.ps1 -AllowPlaceholderIdentity`を実行します。この出力はビルド検証用であり、Storeには提出できません。署名なしのMSIXをユーザーが直接インストールすることもできません。
4. 提出前に、署名したテスト用パッケージをインストールするか、Storeの限定公開テストを使って、クリーンなWindowsで動作確認します。.NETやWindows App SDKのインストールを求められずに初回起動できること、フォルダーの参照とファイル操作、PDF/ZIPの閲覧、設定・キャッシュ・操作記録の保持を確認してください。MSIXでは`LocalAppData`への新規書き込みがリダイレクトされる場合があるため、非パッケージ版からの移行も確認します。

Liliumはデスクトップのファイル操作を行うため、パッケージマニフェストで`runFullTrust`を宣言しています。Store向けビルドでは、Windows App SDKのビルドターゲットがWindows App SDK共有フレームワークへの依存関係を追加します。

Store登録前にクリーンな仮想環境へインストールする場合は、[テスト用MSIXの手順](vm-test-package.md)を参照してください。
