# Lilium V1.0 WACK結果の確認（2026-11-22）

## 結果

| 項目 | 結果 |
|---|---|
| レポート | AppPackages/Certification-20261122-120836/WackReport.xml |
| WACK | 10.0.22621.3233、x64 |
| 対象 | Liliales.Lilium_1.0.0.0_x64__0zddy2tc2chrw |
| OS | Windows 11 Enterprise Evaluation、build 22621 |
| インストール | 署名済みMSIXをWindowsApps配下へ通常インストール、SignatureKind=Developer |
| 総合結果 | WARNING |
| 全24項目 | PASS 22、FAIL 1、WARNING 1 |
| 必須13項目 | PASS 12、WARNING 1、FAIL 0 |
| 任意11項目 | PASS 10、FAIL 1 |
| PARTIAL_RUN | FALSE |

スクリプト終了コード0は検査の完了を意味し、全項目PASSという意味ではない。レポート本文の結果に基づいて上表を集計した。テスト終了後のアプリ登録解除と一時証明書の削除を確認した。

## 任意 TEST88：ブロック済みの実行可能ファイル（FAIL）

レポートはOPTIONAL=TRUEと明記している。33件のメッセージは、プロセス起動APIへの参照と、DLL内のcmd、reg、MSBuildなどの文字列検出である。検出したこと自体は、それらのコマンドが実行されたことを示さない。

Microsoftの[Desktop Bridge検査の説明](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-desktop-bridge-app-tests)では、任意検査はStore登録の合否には使われない情報提供用とされ、この検査はWindows Sモード向けに説明されている。Microsoftの[誤検出に関する解説](https://techcommunity.microsoft.com/blog/ModernWorkAppConsult/how-to-validate-if-your-application-is-compliant-with-the-windows-store-polices-/316783)でも、文字列検出による誤検出があるため、内容を確認するよう案内している。

Lilium本体のProcess.Startは、ヘルプをnotepad.exeで開く処理と、選択ファイルをexplorer.exeで表示する処理である。これは実際の外部プロセス起動を伴う機能であり、すべてを誤検出とは扱わない。今回これらの製品機能は削除していない。通常のデスクトップWindowsでの検証とSモードでの対応は区別し、Sモード対応は未検証とする。

## 必須 TEST92：DPIAwarenessValidation（WARNING）

メッセージは「Lilium.exeを処理できませんでした」と「DPI対応ではありません」の2件。DPI設定がないことを断定できる結果ではないため、次を追加確認した。

- WACKのファイル情報にあるLilium.exeのSHA-256は`DED46B4AF1F41D9F885756E4CFFD563FC70A2ACC75872C2ECC9D84BFC5620DC8`。
- 提出パッケージから展開したLilium.exeも同じSHA-256。
- Windows SDKのmt.exeでそのEXEのRT_MANIFEST（#1）を抽出し、`dpiAwareness=PerMonitorV2`を確認。
- 同一ペイロードのパッケージを一時的に展開登録して起動し、`GetDpiAwarenessContextForProcess`と`AreDpiAwarenessContextsEqual`でプロセスの既定DPIコンテキストが`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`に一致することを確認。APIの取得は成功、PerMonitorV2=true、SystemAware=false。

証拠は`AppPackages/Certification-20261122-120836/dpi-runtime-evidence.json`、抽出マニフェストは`AppPackages/Validation-1.0/Lilium.embedded.manifest`。確認のためのソース変更やパッケージ再生成は行っていない。

以上から、DPI対応不足というWACKの判定は独立した確認では再現していない。検査ツール側の解析上の問題と考えられるが、WACKがEXEを処理できなかった内部の原因までは特定していない。インストール先へのアクセス権やツールの古さを原因と断定しない。プロセス既定値の確認は、すべてのウィンドウや倍率での実画面検証を代替しない。

## 付随する解析上の制約

「デバッグの構成」はPASSだが、FailedToLoadの記録が192件ある。.NET 10のSystem.Private.CoreLibや.NET 6を参照するWindows App SDKのProjection DLLについて、WACK内部のアセンブリ読み込みが失敗した記録である。アプリ実行時の読み込み失敗ログとは区別する。今回のWACKがすべての.NET DLLを完全に解析できたとは主張しない。

## 残る確認

開発用VMでの通常インストールとWACKに加え、別のクリーンなVMでも、同梱依存ランタイムと署名済み本体の導入・初回起動・V1.0表示・画像／PDF／ZIP閲覧をユーザーが確認済み。最初の依存パッケージ同時導入エラーと、分離導入手順の案内後の成功報告は[検証記録](store-validation-1.0.md)に記載した。

通常インストール先での設定・キャッシュ・操作記録の保存と既存版からの移行、Windows 10とスクリーンリーダーの通し検証は未実施。Partner Centerへのパッケージアップロードと申請はユーザーが完了しており、現在は審査待ち。
