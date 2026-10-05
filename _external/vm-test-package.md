# クリーンなWindows仮想環境でのMSIXインストール確認

Store登録前の確認には、自己署名証明書で署名したテスト用MSIXを使えます。`Tools/Build-VMTestPackage.ps1`を実行すると、`AppPackages/VM-Test-x64-日時/`に次の3ファイルを作成します。

- `Lilium-Test-x64.msix`: テスト用に署名したLilium本体
- `Lilium-Test.cer`: 証明書の公開部分。秘密鍵は含みません
- `Microsoft.WindowsAppRuntime.2.msix`: x64用のWindows App SDK依存パッケージ

ZIPをクリーンなx64 Windows仮想環境の`C:\Lilium-VM-Test`に展開します。まず`C:\Lilium-VM-Test`で**管理者としてPowerShell**を開き、証明書を登録します。

```powershell
Import-Certificate -FilePath .\Lilium-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

次に、アプリを使うユーザーとして`C:\Lilium-VM-Test`で**通常のPowerShell**を開き、パッケージをインストールします。`Add-AppxPackage`はコマンドを実行したユーザーのアカウントにアプリを追加します。

```powershell
Add-AppxPackage -Path .\Lilium-Test-x64.msix -DependencyPath .\Microsoft.WindowsAppRuntime.2.msix
```

証明書のインポートは、自己署名のMSIXをWindowsが信頼してインストールできるようにするためです。信頼はその仮想環境全体に適用されるため、テスト専用の仮想環境でのみ実施してください。Liliumのテスト用証明書の秘密鍵ファイルは配布せず、署名後に削除します。証明書の有効期間は発行後1年で、タイムスタンプは付けません。

インストール後、.NETランタイムの追加インストールを求められずに起動すること、フォルダー参照・ファイル操作・PDF/ZIP閲覧・設定保存を確認してください。このテスト用パッケージはStore提出用ではありません。Partner Centerでアプリ名を予約した後、Storeから割り当てられたパッケージIDで別途Store向けパッケージを作成します。
