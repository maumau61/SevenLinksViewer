using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SevenLinksViewer.Models;
using SevenLinksViewer.Services;

namespace SevenLinksViewer;

public partial class MainWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaCrawler _crawler = new();
    private readonly MediaConcatenator _concat = new();
    private readonly FFmpegService _ffmpeg;
    private readonly DispatcherTimer _uiTimer;
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "SevenLinksViewer", "cache");
    private readonly string _toolsDir = Path.Combine(Path.GetTempPath(), "SevenLinksViewer", "tools", "ffmpeg");
    private readonly string _settingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SevenLinksViewer", "settings.txt");
    private Task? _ffmpegBootstrap;

    private string? _lastCameraRoot;
    private EventClip? _currentClip;
    private ObservableCollection<EventClip>? _clips;
    private bool _userSeeking;

    public MainWindow()
    {
        InitializeComponent();

        _ffmpeg = new FFmpegService(_toolsDir);
        _ffmpegBootstrap = _ffmpeg.EnsureAvailableAsync(new Progress<string>(msg =>
            Dispatcher.BeginInvoke(() => StatusText.Text = msg)));

        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.Loaded += (_, _) => VideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.TimeChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTimeUi);
        _mediaPlayer.LengthChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTimeUi);
        _mediaPlayer.EndReached += (_, _) =>
        {
            ThreadPool.QueueUserWorkItem(_ => _mediaPlayer.Stop());
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => UpdateTimeUi();
        _uiTimer.Start();

        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
            try
            {
                if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true);
            }
            catch { /* best effort — VLC may still be releasing handles */ }
        };

        Loaded += (_, _) =>
        {
            var last = LoadLastFolder();
            if (last != null) LoadFolder(last);
        };
    }

    private string? LoadLastFolder()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return null;
            var path = File.ReadAllText(_settingsFile).Trim();
            return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : path;
        }
        catch { return null; }
    }

    private void SaveLastFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, folder);
        }
        catch { /* best effort */ }
    }

    private CollectionViewSource ClipsView => (CollectionViewSource)Resources["ClipsView"];

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Select the camera root folder (containing DCIM/)",
            InitialDirectory = _lastCameraRoot ?? ""
        };
        if (picker.ShowDialog(this) != true) return;
        LoadFolder(picker.FolderName);
    }

    private void LoadFolder(string folder)
    {
        try
        {
            _clips = new ObservableCollection<EventClip>(_crawler.Crawl(folder));
            ClipsView.Source = _clips;
            _lastCameraRoot = folder;
            SaveLastFolder(folder);
            RefreshButton.IsEnabled = true;
            var dayCount = _clips.Select(c => c.DayLabel).Distinct().Count();
            StatusText.Text = _clips.Count == 0
                ? "No clips found in selected folder."
                : $"{dayCount} day(s), {_clips.Count} clip(s) loaded from {folder}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to scan folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteClipButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not EventClip clip) return;

        var msg = $"Permanently delete this event folder?\n\n{clip.FolderPath}\n\n{clip.ChunkFiles.Count} chunk(s) \u2014 cannot be undone.";
        if (MessageBox.Show(this, msg, "Delete clip", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _mediaPlayer.Stop();
        _currentClip = null;
        ExportButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;

        try
        {
            ForceDeleteDirectory(clip.FolderPath);
            _clips?.Remove(clip);
            StatusText.Text = $"Deleted {Path.GetFileName(clip.FolderPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Directory.Delete(recursive:true) throws UnauthorizedAccessException if any file
    // has the read-only attribute set. The 7links camera writes .media files read-only,
    // so we clear attributes on the way down.
    private static void ForceDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir)) ForceDeleteDirectory(sub);
        foreach (var f in Directory.GetFiles(dir))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            File.Delete(f);
        }
        try { File.SetAttributes(dir, FileAttributes.Normal); } catch { }
        Directory.Delete(dir);
    }

    private async void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is not EventClip clip) return;
        await LoadAndPlayAsync(clip);
    }

    private async Task LoadAndPlayAsync(EventClip clip)
    {
        try
        {
            if (_ffmpegBootstrap != null)
            {
                StatusText.Text = "Preparing ffmpeg\u2026";
                await _ffmpegBootstrap;
                _ffmpegBootstrap = null;
            }

            var playable = _concat.BuildPlayableHevc(clip, _cacheDir);
            var fps = clip.EstimatedDuration.TotalSeconds > 0
                ? playable.FrameCount / clip.EstimatedDuration.TotalSeconds
                : 25.0;

            var mp4Path = Path.ChangeExtension(playable.Path, ".mp4");
            if (!File.Exists(mp4Path) || new FileInfo(mp4Path).Length == 0)
            {
                StatusText.Text = "Muxing to MP4\u2026";
                await Task.Run(() => _ffmpeg.RemuxHevcToMp4(playable.Path, mp4Path, fps));
            }

            var media = new Media(_libVlc, mp4Path, FromType.FromPath);
            _mediaPlayer.Play(media);
            _currentClip = clip;
            ExportButton.IsEnabled = true;
            PlayPauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            StatusText.Text = $"Playing: {clip.StartLocal:yyyy-MM-dd HH:mm:ss} \u2014 {clip.ChunkFiles.Count} chunks, ~{clip.EstimatedDuration.TotalSeconds:0}s, {playable.FrameCount} frames @ {fps:0.#} fps";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to play clip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _mediaPlayer.Stop();
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
            StatusText.Text = "Cache cleared.";
            if (_lastCameraRoot != null) LoadFolder(_lastCameraRoot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to clear cache", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _mediaPlayer.Stop();

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e) => _userSeeking = true;

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_mediaPlayer.Length > 0)
        {
            _mediaPlayer.Position = (float)(SeekSlider.Value / SeekSlider.Maximum);
        }
        _userSeeking = false;
    }

    private void UpdateTimeUi()
    {
        var len = _mediaPlayer.Length;
        var time = _mediaPlayer.Time;
        if (len > 0)
        {
            if (!_userSeeking) SeekSlider.Value = (double)time / len * SeekSlider.Maximum;
            TimeText.Text = $"{Format(time)} / {Format(len)}";
        }
        else
        {
            TimeText.Text = "--:-- / --:--";
        }
    }

    private static string Format(long ms)
    {
        if (ms < 0) return "--:--";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip == null) return;
        var clip = _currentClip;
        var dialog = new SaveFileDialog
        {
            Title = "Export clip as MP4",
            FileName = $"{clip.StartLocal:yyyyMMdd_HHmmss}.mp4",
            Filter = "MP4 video (*.mp4)|*.mp4|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = "Exporting MP4\u2026";
            await Task.Run(() =>
            {
                var playable = _concat.BuildPlayableHevc(clip, _cacheDir);
                var fps = clip.EstimatedDuration.TotalSeconds > 0
                    ? playable.FrameCount / clip.EstimatedDuration.TotalSeconds
                    : 25.0;
                var cachedMp4 = Path.ChangeExtension(playable.Path, ".mp4");
                if (!File.Exists(cachedMp4) || new FileInfo(cachedMp4).Length == 0)
                    _ffmpeg.RemuxHevcToMp4(playable.Path, cachedMp4, fps);
                File.Copy(cachedMp4, dialog.FileName, overwrite: true);
            });
            StatusText.Text = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
