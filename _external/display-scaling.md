# Windows の表示スケール

## 実装

- 実行ファイルの `app.manifest` に `PerMonitorV2` を宣言する。WinUI の文字・コントロールを、ウィンドウのあるモニターの DPI で描画する。
- XAML のレイアウト値は DIP のまま扱う。文字サイズやコントロール幅へ独自に倍率を掛けない。
- 設定・言語選択・起動エラーのウィンドウは `WindowDpi.ResizeInDips` で初期サイズを物理ピクセルへ変換し、タスクバーを除く作業領域に収める。保存済みの一覧ウィンドウ位置・サイズと、全画面のモニター領域は物理ピクセルとして扱う。
- `ReaderView` は読み込み時の `XamlRoot.RasterizationScale` で表示領域を物理ピクセルへ変換する。`XamlRoot.Changed` で倍率の変更を監視し、DIP のサイズが変わらない場合も現在のページを180msのタイマーで再読み込みする。プレビューの縮小デコードと PDF の描画解像度を更新する。
- 倍率変更とサイズ変更には同じタイマーを使う。読み込み中は完了を待って読み直し、終了・アンロード時は倍率のイベント購読を解除する。
- 一覧のサムネイル画像は従来の最大300×400ピクセル、JPEG品質70の共有キャッシュを使う。高倍率で拡大されるサムネイル画像の解像度は今回変更していない。ファイル名などの文字は WinUI が DPI に合わせて描画する。

## 検証

- x64 Debug ビルド成功（警告0・エラー0）。
- 既存の `Tests/Preview` の218項目が成功。
- ビルドした実行ファイルの埋め込みマニフェストに `PerMonitorV2` が含まれることを確認。
- 実際に起動したプロセスと WinUI のウィンドウが `PerMonitorV2` であることを Windows API で確認。ウィンドウ DPI は144（150%）。

表示倍率の変更・異なる倍率のモニター間移動の見た目は未確認。実機では次を確認する。

1. 100%・125%・150%・200%で起動し、メニュー、ファイル名、設定、ダイアログの文字が鮮明であること。
2. 起動中に表示スケールを変更し、文字・UI が新しい倍率に追従すること。
3. 倍率の異なるモニター間で一覧・設定ウィンドウを移動し、文字がぼやけず、操作位置と表示位置が一致すること。
4. PDF と画像のプレビューを表示したまま倍率を変え、現在のページと表示モードを保って画像が読み直されること。
5. 高倍率の小さい作業領域で設定・言語選択ウィンドウを開き、ウィンドウが画面内に収まること。
6. プレビューの開閉・別項目選択・ウィンドウ終了を倍率変更と組み合わせ、古いページの表示や終了後の読み込みが起きないこと。

## 参照

- [DPI awareness の宣言](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)
- [XamlRoot.Changed](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.xamlroot.changed)
- [XamlRoot.RasterizationScale](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.xamlroot.rasterizationscale)
