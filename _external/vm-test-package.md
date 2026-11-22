# クリーンなWindows仮想環境でのMSIXインストール確認

Store登録前の確認には、自己署名証明書で署名したテスト用MSIXを使えます。`Tools/Build-VMTestPackage.ps1`を実行すると、`AppPackages/VM-Test-x64-日時/`に次の3ファイルを作成します。

作成は`C:\Dev\Lilium`でPowerShell 7を使い、`pwsh -File Tools/Build-VMTestPackage.ps1`を実行します。既に生成したStoreパッケージを使う場合は、`-StoreUploadPath`にその`.msixupload`の絶対パスを指定します。Store用の署名なしMSIXは変更せず、コピーに検証用の署名を付けます。

- `Lilium-Test-x64.msix`: テスト用に署名したLilium本体
- `Lilium-Test.cer`: 証明書の公開部分。秘密鍵は含みません
- `Microsoft.WindowsAppRuntime.2.msix`: x64用のWindows App SDK依存パッケージ

ZIPをクリーンなx64 Windows仮想環境の`C:\Lilium-VM-Test`に展開します。まず`C:\Lilium-VM-Test`で**管理者としてPowerShell**を開き、証明書を登録します。

```powershell
Import-Certificate -FilePath .\Lilium-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

次に、アプリを使うユーザーとして`C:\Lilium-VM-Test`で**通常のWindows PowerShell**を開き、依存ランタイムを先にインストールします。`Add-AppxPackage`はコマンドを実行したユーザーのアカウントにアプリを追加します。

```powershell
Add-AppxPackage -Path 'C:\Lilium-VM-Test\Microsoft.WindowsAppRuntime.2.msix' -ErrorAction Stop
```

依存ランタイムの導入に成功した場合のみ、同じユーザー・同じフォルダーの**通常のWindows PowerShell**で本体をインストールします。

```powershell
Add-AppxPackage -Path 'C:\Lilium-VM-Test\Lilium-Test-x64.msix' -ErrorAction Stop
```

2026-11-22の別VM検証では、本体と依存ランタイムの同時導入時にランタイムのインストールフォルダーが見つからないエラーが発生したため、この分離導入手順を案内した。その後、ユーザーからインストール・初回起動・基本閲覧の確認完了報告を受領している。

証明書のインポートは、自己署名のMSIXをWindowsが信頼してインストールできるようにするためです。信頼はその仮想環境全体に適用されるため、テスト専用の仮想環境でのみ実施してください。Liliumのテスト用証明書の秘密鍵ファイルは配布せず、署名後に削除します。証明書の有効期間は発行後1年で、タイムスタンプは付けません。

インストール後、.NETランタイムの追加インストールを求められずに起動すること、フォルダー参照・ファイル操作・PDF/ZIP閲覧・設定保存を確認してください。このテスト用パッケージはStore提出用ではありません。正式なStore用の署名なしパッケージとは別の、インストール検証用ファイルです。

現在は正式なStoreパッケージIDを設定済みです。同じIDのLiliumが既に入っているPCでの上書きは避け、まず検証専用のクリーンなWindowsで確認してください。

Windows App Certification Kitを導入した検証用PCでは、`C:\Lilium-VM-Test`で管理者としてPowerShellを開き、次のコマンドでローカル認証テストを実行できます。管理者アカウントがインストール時のアカウントと異なる場合は、その管理者アカウントでも上記の`Add-AppxPackage`を実行して依存パッケージを導入してから実施します。

```powershell
& 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe' reset
& 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe' test -appxpackagepath 'C:\Lilium-VM-Test\Lilium-Test-x64.msix' -reportoutputpath 'C:\Lilium-VM-Test\WackReport.xml'
```

コマンドの起動だけでは合格とは判断せず、出力されたレポートの結果を確認してください。

このリポジトリのある開発用PCでは、`C:\Dev\Lilium`で管理者としてWindows PowerShellを開き、`powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Tools\Test-StoreCertification.ps1 -TestPackageDirectory <検証用パッケージフォルダーの絶対パス>`を実行すると、証明書の一時登録、署名の検証、依存パッケージと本体のインストール、WACK実行、テスト用本体と追加した証明書の削除をまとめて行えます。この認証スクリプトはWindows標準のPowerShell 5.1で動作し、PowerShell 7は不要です。`-ExecutionPolicy RemoteSigned`は起動したプロセスだけに適用され、PCの恒久的な実行ポリシーは変更しません。既存の同じIDのLiliumがあるアカウントでは、データを守るため処理を中止します。依存フレームワークは削除しません。進捗・ログ・レポートは`AppPackages/Certification-日時/`へ保存します。このスクリプトは開発用PC上でのインストールとローカル認証を確認するもので、クリーンなWindowsでの検証を代替しません。
