using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lilium;

public sealed class QuickAccessItem
{
    public QuickAccessItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;
    }

    public string Path { get; }
    public string Name { get; }
}

public sealed record FolderNode(string Name, string Path, bool CanExpand, bool IsPlaceholder = false);

public sealed class LibraryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsFolder { get; init; }
    public required DateTime Modified { get; init; }
    public required int Rating { get; init; }
    public required string ThumbnailSourcePath { get; init; }
    public required int CardWidth { get; init; }
    public required int CardHeight { get; init; }
    public bool IsArchive => !IsFolder && !ArchiveLocation.IsVirtual(Path) && ArchiveLocation.IsArchiveFile(Path);
    public bool IsVirtual => ArchiveLocation.IsVirtual(Path);
    public bool IsPdf => !IsFolder && !IsVirtual && PdfSession.IsPdfFile(Path);
    public bool IsImage => !IsFolder && !IsArchive && !IsPdf && !string.IsNullOrEmpty(ThumbnailSourcePath);
    public Visibility FileIconVisibility => !IsFolder && !IsImage && !IsArchive && (!IsPdf || Thumbnail is null) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RatingVisibility => Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RatingLabel => $"★ {Rating}";
    public string TypeLabel => IsFolder ? "📁" : IsPdf ? "PDF" : IsArchive ? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() : "";
    public Visibility TypeLabelVisibility => Thumbnail is not null && TypeLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(ImageVisibility)); OnPropertyChanged(nameof(FolderIconVisibility)); OnPropertyChanged(nameof(FileIconVisibility)); OnPropertyChanged(nameof(TypeLabelVisibility)); } }
    public Visibility ImageVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FolderIconVisibility => (IsFolder || IsArchive) && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record LibraryFolderReadResult(
    List<LibraryItem> Items,
    FolderReadStatus Status,
    Exception? Error = null,
    string? FailedPath = null)
{
    public bool IsSuccess => Status == FolderReadStatus.Success;
}
