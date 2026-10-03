using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Lilium;

public sealed partial class MainWindow
{
    private readonly ReaderView _previewReader = new(true);
    private readonly Grid _previewChrome = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _previewTitle = new()
    {
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _previewDelay = null!;
    private CancellationTokenSource? _previewCancellation;
    private LibraryItem? _previewItem;
    private bool _previewOpen;
    private bool _previewResizing;
    private double _previewPreferredWidth = PreviewRules.DefaultWidth;
    private double _previewDragStartX;
    private double _previewDragStartWidth;
    private string? _previewResumePath;
    private int _previewResumeIndex;
    private string? _previewResumePagePath;
    private string _previewResumeMode = "auto";

    private void InitializePreview()
    {
        _previewChrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewChrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Padding = new Thickness(12, 8, 8, 8), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_previewTitle);
        var collapse = new Button { Content = "×", MinWidth = 32, MinHeight = 32 };
        ToolTipService.SetToolTip(collapse, L10n.Get("Preview_Collapse"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(collapse, L10n.Get("Preview_Collapse"));
        collapse.Click += (_, _) => SetPreviewOpen(false);
        Grid.SetColumn(collapse, 1);
        header.Children.Add(collapse);
        _previewChrome.Children.Add(header);
        Grid.SetRow(_previewReader, 1);
        _previewChrome.Children.Add(_previewReader);
        PreviewHost.Child = _previewChrome;
        _previewReader.CanReceiveInput = () => !_closed && !_fileOperationBusy && _dialogDepth == 0 && !_previewResizing;
        _previewReader.BindingChanged = ApplyBindingFromReader;
        _previewReader.FullscreenRequested = (document, index, binding) =>
        {
            var viewer = new ViewerWindow(document, index, binding, this);
            viewer.ShowForeground();
        };
        _previewDelay = DispatcherQueue.CreateTimer();
        _previewDelay.Interval = TimeSpan.FromMilliseconds(200);
        _previewDelay.IsRepeating = false;
        _previewDelay.Tick += async (_, _) => await LoadPreviewAsync();
        ThumbnailGrid.SelectionChanged += (_, _) => QueuePreview();
        PreviewLayout.SizeChanged += (_, _) => ApplyPreviewWidth();
        BindingBox.SelectionChanged += (_, _) => _previewReader.ApplyBinding(SelectedTag(BindingBox, _savedBinding));
        Closed += (_, _) =>
        {
            CancelPreviewLoad();
            _previewReader.Dispose();
        };
    }

    private void RestorePreviewSettings()
    {
        var raw = ReadSetting("preview_width", PreviewRules.DefaultWidth.ToString(CultureInfo.InvariantCulture));
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) &&
            double.IsFinite(width) && width >= PreviewRules.MinimumWidth && width <= 10000)
            _previewPreferredWidth = width;
        else ShowInvalidSetting("preview_width");
        _previewOpen = ReadChoiceSetting("preview_open", "false", "true", "false") == "true";
        UpdatePreviewLayout();
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => SetPreviewOpen(PreviewToggle.IsChecked == true);

    private void SetPreviewOpen(bool open)
    {
        if (_previewOpen == open) return;
        _previewOpen = open;
        if (!open)
        {
            RememberPreviewPosition();
            CancelPreviewLoad();
            _previewReader.Clear();
            _previewItem = null;
            _previewChrome.Visibility = Visibility.Collapsed;
            ThumbnailGrid.Focus(FocusState.Programmatic);
        }
        UpdatePreviewLayout();
        TrySaveSetting("preview_open", open ? "true" : "false");
        if (open) QueuePreview();
    }

    private void UpdatePreviewLayout()
    {
        PreviewToggle.IsChecked = _previewOpen;
        PreviewHost.Visibility = PreviewSplitter.Visibility = _previewOpen ? Visibility.Visible : Visibility.Collapsed;
        PreviewDividerColumn.Width = new GridLength(_previewOpen ? PreviewRules.DividerWidth : 0);
        ApplyPreviewWidth();
    }

    private void ApplyPreviewWidth() => PreviewColumn.Width = new GridLength(_previewOpen
        ? PreviewRules.FitWidth(_previewPreferredWidth, PreviewLayout.ActualWidth) : 0);

    private bool IsPreviewElement(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, PreviewHost) || ReferenceEquals(node, PreviewSplitter)) return true;
        return false;
    }

    private void CancelPreviewLoad()
    {
        _previewDelay.Stop();
        // The task that owns the CTS disposes it after its asynchronous work finishes.
        _previewCancellation?.Cancel();
        _previewCancellation = null;
    }

    private void RememberPreviewPosition()
    {
        if (_previewItem is null) return;
        _previewResumePath = _previewItem.Path;
        _previewResumeIndex = _previewReader.PageIndex;
        _previewResumeMode = _previewReader.Mode;
        var images = _previewReader.CurrentDocument?.Images;
        _previewResumePagePath = images is not null && _previewResumeIndex < images.Length
            ? images[_previewResumeIndex] : null;
    }

    private void PreparePreviewRefresh()
    {
        if (!_previewOpen) return;
        RememberPreviewPosition();
        CancelPreviewLoad();
        _previewReader.Clear();
        // Collection replacement momentarily clears selection. Preserve the same target's bookmark.
        _previewItem = null;
        _previewChrome.Visibility = Visibility.Collapsed;
    }

    private void QueuePreview()
    {
        if (!_previewOpen || _closed || _initializing) return;
        var item = ThumbnailGrid.SelectedItems.Count == 1 ? ThumbnailGrid.SelectedItems[0] as LibraryItem : null;
        if (ReferenceEquals(item, _previewItem)) return;
        CancelPreviewLoad();
        _previewReader.Clear();
        _previewItem = item;
        _previewChrome.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null)
        {
            _previewResumePath = null;
            if (IsPreviewElement(FocusManager.GetFocusedElement(RootGrid.XamlRoot))) ThumbnailGrid.Focus(FocusState.Programmatic);
            return;
        }
        if (!string.Equals(_previewResumePath, item.Path, StringComparison.Ordinal)) _previewResumePath = null;
        _previewTitle.Text = item.DisplayName;
        ToolTipService.SetToolTip(_previewTitle, item.IsVirtual && ArchiveLocation.TryParse(item.Path, out var location)
            ? location!.DisplayPath : item.Path);
        _previewReader.ShowStatus(L10n.Get("Reader_Loading"));
        _previewDelay.Start();
    }

    private async Task LoadPreviewAsync()
    {
        var item = _previewItem;
        if (item is null || !_previewOpen || _closed) return;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var token = cancellation.Token;
        bool Current() => !token.IsCancellationRequested && !_closed && _previewOpen &&
            ReferenceEquals(item, _previewItem) && ThumbnailGrid.SelectedItems.Count == 1;
        // Capture the displayed sequence and sort before any asynchronous reads.
        string[] images = _items.Where(i => i.IsImage).Select(i => i.Path).ToArray();
        string sort = SelectedTag(FileSortBox, _savedFileSort);
        string binding = SelectedTag(BindingBox, _savedBinding);
        try
        {
            ReaderDocument document;
            int index = 0;
            if (item.IsPdf) document = new ReaderDocument([], item.Path);
            else if (item.IsImage)
            {
                index = Array.IndexOf(images, item.Path);
                document = new ReaderDocument(images);
            }
            else if (item.IsFolder || item.IsArchive)
            {
                string path = item.IsArchive ? ArchiveLocation.Folder(item.Path, "") : item.Path;
                if (ArchiveLocation.TryParse(path, out var archive))
                {
                    var read = await ArchiveCatalog.ReadFolderAsync(archive!, "S", false, token);
                    if (!read.IsSuccess) throw read.Error ?? new IOException(L10n.Get("Preview_OpenError"));
                    images = FileCatalog.FilterAndSort(read.Items.Where(i => i.IsImage), 0, 0, null, "name", sort, token)
                        .Select(i => i.Path).ToArray();
                }
                else
                {
                    // Avoid cover searches and thumbnail construction while listing a preview folder.
                    images = await Task.Run(() =>
                    {
                        var read = FolderListingReader.Read(path, token);
                        if (!read.IsSuccess) throw read.Error ?? new IOException(L10n.Get("Preview_OpenError"));
                        return read.Entries.Where(i => !i.IsFolder &&
                                (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 &&
                                ArchiveCatalog.ImageExtensions.Contains(Path.GetExtension(i.Path)))
                            .OrderByDescending(i => sort == "modified" ? i.Modified : DateTime.MinValue)
                            .ThenBy(i => i.Name, NaturalComparer.Instance).Select(i => i.Path).ToArray();
                    }, token);
                }
                if (!Current()) return;
                if (images.Length == 0) { _previewReader.ShowStatus(L10n.Get("Preview_EmptyFolder")); return; }
                document = new ReaderDocument(images);
            }
            else
            {
                if (Current()) _previewReader.ShowStatus(L10n.Get("Preview_Unsupported"));
                return;
            }
            if (!Current()) return;
            string mode = ReadChoiceSetting("preview_mode", "auto", "auto", "one", "two");
            if (_previewResumePath == item.Path)
            {
                int restored = _previewResumePagePath is null ? -1 : Array.IndexOf(document.Images, _previewResumePagePath);
                index = restored >= 0 ? restored : _previewResumeIndex;
                mode = _previewResumeMode;
            }
            await _previewReader.OpenAsync(document, index, binding, mode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.WriteDiagnosticLog("Preview load failed", ex);
            if (Current()) _previewReader.ShowStatus(L10n.Get("Preview_OpenError") + ": " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PreviewSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewOpen || _dialogDepth > 0 || !e.GetCurrentPoint(PreviewLayout).Properties.IsLeftButtonPressed) return;
        _previewDragStartX = e.GetCurrentPoint(PreviewLayout).Position.X;
        _previewDragStartWidth = PreviewColumn.ActualWidth;
        _previewResizing = PreviewSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewResizing) return;
        _previewPreferredWidth = Math.Max(PreviewRules.MinimumWidth, PreviewRules.FitWidth(
            _previewDragStartWidth + _previewDragStartX - e.GetCurrentPoint(PreviewLayout).Position.X, PreviewLayout.ActualWidth));
        ApplyPreviewWidth();
        e.Handled = true;
    }

    private void PreviewSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewResizing) return;
        PreviewSplitter.ReleasePointerCapture(e.Pointer);
        FinishPreviewResize();
        e.Handled = true;
    }

    private void PreviewSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => FinishPreviewResize();

    private void FinishPreviewResize()
    {
        if (!_previewResizing) return;
        _previewResizing = false;
        TrySaveSetting("preview_width", _previewPreferredWidth.ToString(CultureInfo.InvariantCulture));
    }

    private void PreviewSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right) || InputRouting.Modifiers != 0) return;
        _previewPreferredWidth = Math.Max(PreviewRules.MinimumWidth, PreviewRules.FitWidth(
            PreviewColumn.ActualWidth + (e.Key == VirtualKey.Left ? 16 : -16), PreviewLayout.ActualWidth));
        ApplyPreviewWidth();
        TrySaveSetting("preview_width", _previewPreferredWidth.ToString(CultureInfo.InvariantCulture));
        e.Handled = true;
    }
}
