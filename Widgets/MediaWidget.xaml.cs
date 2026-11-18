using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using Windows.Foundation;
using Windows.Media.Control;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Shows and controls the system's current media session (Spotify, browsers, players) through the
/// Global System Media Transport Controls, including track progress and audio output volume.
/// </summary>
public partial class MediaWidget : DockWidget
{
    private const int AlbumArtDecodePixels = 68;

    private MediaSessionManager? _manager;
    private MediaSession? _session;
    private AudioEndpointMonitor? _audio;
    private int _activationVersion;
    private int _refreshVersion;
    private string? _artKey;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private TimeSpan _position;
    private DateTimeOffset _positionUpdatedAt;
    private bool _playing;
    private bool _canSeek;
    private bool _updatingProgress;
    private bool _draggingProgress;
    private bool _updatingVolume;
    private bool? _lastPlayGlyph;

    private bool? _lastAudioHasDevice;
    private int _lastAudioLevel = -1;
    private bool _lastAudioMuted;
    private string? _lastAudioDeviceName;
    private string? _lastAudioIconKey;
    private AppLanguage _lastAudioLanguage;

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 10;
    public override double FullWidthEstimate => 322;
    public override double CompactWidthEstimate => 94;

    public MediaWidget()
    {
        InitializeComponent();
        HasData = false;
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensityLayout();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensityLayout();

    private void ApplyDensityLayout()
    {
        var compact = IsCompact && !IsHoverExpanded;
        Card.Width = compact ? 88 : 316;
        Card.Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(6, 0, 8, 0);
        InfoPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PreviousButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumePanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var root = new System.Windows.Controls.StackPanel { Width = 286 };

        var header = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var art = new System.Windows.Controls.Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        art.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WidgetTrack");
        if (ArtImage.Source is not null)
        {
            art.Child = new System.Windows.Controls.Image { Source = ArtImage.Source, Stretch = System.Windows.Media.Stretch.UniformToFill };
        }
        else
        {
            var placeholder = new System.Windows.Controls.ContentControl { Content = FindResource("IconMusic"), Width = 18, Height = 18 };
            placeholder.SetResourceReference(StyleProperty, "WidgetIcon");
            art.Child = placeholder;
        }

        var meta = new System.Windows.Controls.StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new System.Windows.Controls.TextBlock
        {
            Text = TitleText.Text,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(StyleProperty, "WidgetText");
        var artist = new System.Windows.Controls.TextBlock
        {
            Text = ArtistText.Text,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        artist.SetResourceReference(StyleProperty, "WidgetCaption");
        meta.Children.Add(title);
        meta.Children.Add(artist);
        System.Windows.Controls.Grid.SetColumn(art, 0);
        System.Windows.Controls.Grid.SetColumn(meta, 1);
        header.Children.Add(art);
        header.Children.Add(meta);
        root.Children.Add(header);

        var controls = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 6)
        };
        var prevBtn = new System.Windows.Controls.Button { Width = 32, Height = 32, Content = new System.Windows.Controls.ContentControl { Content = FindResource("IconPreviousFill"), Width = 12, Height = 12 } };
        prevBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
        if (prevBtn.Content is System.Windows.Controls.ContentControl pc) pc.SetResourceReference(StyleProperty, "WidgetGlyph");
        prevBtn.Click += Previous_Click;

        var playBtn = new System.Windows.Controls.Button { Width = 36, Height = 36, Margin = new Thickness(8, 0, 8, 0), Content = new System.Windows.Controls.ContentControl { Content = FindResource(_playing ? "IconPauseFill" : "IconPlayFill"), Width = 15, Height = 15 } };
        playBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
        if (playBtn.Content is System.Windows.Controls.ContentControl plc) plc.SetResourceReference(StyleProperty, "WidgetGlyph");
        playBtn.Click += PlayPause_Click;

        var nextBtn = new System.Windows.Controls.Button { Width = 32, Height = 32, Content = new System.Windows.Controls.ContentControl { Content = FindResource("IconNextFill"), Width = 12, Height = 12 } };
        nextBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
        if (nextBtn.Content is System.Windows.Controls.ContentControl nc) nc.SetResourceReference(StyleProperty, "WidgetGlyph");
        nextBtn.Click += Next_Click;

        controls.Children.Add(prevBtn);
        controls.Children.Add(playBtn);
        controls.Children.Add(nextBtn);
        root.Children.Add(controls);

        if (_audio?.HasDevice == true)
        {
            var volRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 0) };
            volRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            volRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var muteBtn = new System.Windows.Controls.Button
            {
                Width = 26,
                Height = 26,
                Content = new System.Windows.Controls.ContentControl { Content = VolumeIcon.Content, Width = 14, Height = 14 }
            };
            muteBtn.SetResourceReference(StyleProperty, "WidgetIconButton");
            if (muteBtn.Content is System.Windows.Controls.ContentControl mc) mc.SetResourceReference(StyleProperty, "WidgetIcon");
            muteBtn.Click += MuteButton_Click;

            var slider = new System.Windows.Controls.Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = VolumeSlider.Value,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.SetResourceReference(StyleProperty, "WidgetSlider");
            slider.ValueChanged += (_, args) =>
            {
                if (_updatingVolume || _audio is null) return;
                if (_audio.SetVolume((float)(args.NewValue / 100.0))) RenderVolume();
            };

            System.Windows.Controls.Grid.SetColumn(muteBtn, 0);
            System.Windows.Controls.Grid.SetColumn(slider, 1);
            volRow.Children.Add(muteBtn);
            volRow.Children.Add(slider);
            root.Children.Add(volRow);
        }

        return root;
    }

    protected override async void OnActivated()
    {
        var activationVersion = ++_activationVersion;
        UiText.Current.PropertyChanged += UiText_PropertyChanged;

        try
        {
            _audio = new AudioEndpointMonitor();
            _audio.VolumeChanged += Audio_VolumeChanged;
            _audio.DefaultDeviceChanged += Audio_DefaultDeviceChanged;
        }
        catch (COMException ex)
        {
            AppLog.Warning($"Media widget audio unavailable: {ex.Message}");
            _audio = null;
        }

        InvalidateAudioCache();
        RenderVolume();

        try
        {
            var manager = await MediaSessionManager.RequestAsync();
            if (!IsActive || activationVersion != _activationVersion) return;
            _manager = manager;
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            BindSession(_manager.GetCurrentSession());
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            if (activationVersion == _activationVersion)
            {
                AppLog.Warning($"Media session manager unavailable: {ex.Message}");
                HasData = false;
            }
        }
    }

    protected override void OnDeactivated()
    {
        _activationVersion++;
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        BindSession(null);
        ClearAlbumArt();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            _manager = null;
        }

        if (_audio is not null)
        {
            _audio.VolumeChanged -= Audio_VolumeChanged;
            _audio.DefaultDeviceChanged -= Audio_DefaultDeviceChanged;
            _audio.Dispose();
            _audio = null;
        }
    }

    protected override void OnTick(DateTime now)
    {
        if (_session is not null && _playing && !_draggingProgress)
        {
            UpdateProgressUI();
        }
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RenderVolume();

    private void InvalidateAudioCache()
    {
        _lastAudioHasDevice = null;
        _lastAudioLevel = -1;
        _lastAudioIconKey = null;
        _lastAudioDeviceName = null;
    }

    private void ClearAlbumArt()
    {
        _artKey = null;
        ArtImage.Source = null;
        ArtPlaceholder.Visibility = Visibility.Visible;
    }

    private void Audio_VolumeChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (IsActive) RenderVolume();
    });

    private void Audio_DefaultDeviceChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!IsActive || _audio is null) return;
        _audio.BindDefaultDevice();
        RenderVolume();
    });

    private void RenderVolume()
    {
        var hasDevice = _audio?.HasDevice == true;
        var lang = UiText.Current.Language;
        if (!hasDevice)
        {
            if (_lastAudioHasDevice == false && _lastAudioLanguage == lang) return;
            _lastAudioHasDevice = false;
            _lastAudioLanguage = lang;
            _lastAudioLevel = -1;
            MuteButton.IsEnabled = false;
            VolumeSlider.IsEnabled = false;
            SetVolumeIcon("IconVolumeMute");
            return;
        }

        var level = (int)Math.Round(_audio!.Volume * 100);
        var muted = _audio.Muted;
        var deviceName = _audio.DeviceName;
        if (_lastAudioHasDevice == true && _lastAudioLevel == level && _lastAudioMuted == muted
            && _lastAudioDeviceName == deviceName && _lastAudioLanguage == lang)
        {
            return;
        }

        _lastAudioHasDevice = true;
        _lastAudioLevel = level;
        _lastAudioMuted = muted;
        _lastAudioDeviceName = deviceName;
        _lastAudioLanguage = lang;

        MuteButton.IsEnabled = true;
        VolumeSlider.IsEnabled = true;
        SetVolumeIcon(muted || level == 0 ? "IconVolumeMute" : level < 50 ? "IconVolumeLow" : "IconVolume");
        VolumeSlider.ToolTip = muted ? UiText.Get("MuteToggle") : $"{deviceName ?? UiText.Get("Volume")}: {UiText.Format("Percent", level)}";
        _updatingVolume = true;
        VolumeSlider.Value = level;
        _updatingVolume = false;
    }

    private void SetVolumeIcon(string key)
    {
        if (_lastAudioIconKey == key) return;
        _lastAudioIconKey = key;
        VolumeIcon.Content = FindResource(key);
    }

    private void Manager_CurrentSessionChanged(MediaSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && _manager is not null) BindSession(_manager.GetCurrentSession());
        });

    private void BindSession(MediaSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }

        _session = session;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }

        _artKey = null;
        Refresh(includeProperties: true);
    }

    private void Session_MediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && ReferenceEquals(sender, _session)) Refresh(includeProperties: true);
        });

    private void Session_PlaybackInfoChanged(MediaSession sender, PlaybackInfoChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && ReferenceEquals(sender, _session)) Refresh(includeProperties: false);
        });

    private void Session_TimelinePropertiesChanged(MediaSession sender, TimelinePropertiesChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && ReferenceEquals(sender, _session)) RefreshTimeline();
        });

    private void RefreshTimeline()
    {
        if (_session is null) return;
        try
        {
            var timeline = _session.GetTimelineProperties();
            _startTime = timeline.StartTime;
            _endTime = timeline.EndTime;
            _position = timeline.Position;
            _positionUpdatedAt = timeline.LastUpdatedTime;
            if (!_draggingProgress) UpdateProgressUI();
        }
        // Dead sessions surface as NullReferenceException/ObjectDisposedException through the WinRT projection.
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NullReferenceException or ObjectDisposedException)
        {
            AppLog.Warning($"Media timeline refresh failed: {ex.Message}");
        }
    }

    private void UpdateProgressUI()
    {
        var duration = _endTime - _startTime;
        if (duration.TotalSeconds <= 0.5)
        {
            _updatingProgress = true;
            ProgressSlider.Maximum = 100;
            ProgressSlider.Value = 0;
            ProgressSlider.IsEnabled = false;
            ProgressSlider.ToolTip = null;
            _updatingProgress = false;
            return;
        }

        var elapsed = (_position - _startTime).TotalSeconds;
        if (_playing && _positionUpdatedAt != default)
        {
            var delta = (DateTimeOffset.UtcNow - _positionUpdatedAt).TotalSeconds;
            if (delta > 0 && delta < 3600) elapsed += delta;
        }

        elapsed = Math.Clamp(elapsed, 0, duration.TotalSeconds);
        _updatingProgress = true;
        ProgressSlider.Maximum = duration.TotalSeconds;
        ProgressSlider.Value = elapsed;
        ProgressSlider.IsEnabled = _canSeek;
        ProgressSlider.ToolTip = $"{FormatTime(TimeSpan.FromSeconds(elapsed))} / {FormatTime(duration)}";
        _updatingProgress = false;
    }

    private static string FormatTime(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");

    private async void Refresh(bool includeProperties)
    {
        // Only property fetches are versioned; a playback-only update must not cancel an in-flight title fetch.
        var version = includeProperties ? ++_refreshVersion : _refreshVersion;
        var activationVersion = _activationVersion;
        var session = _session;
        if (session is null)
        {
            HasData = false;
            ClearAlbumArt();
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            _playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _canSeek = controls.IsPlaybackPositionEnabled;
            if (_lastPlayGlyph != _playing)
            {
                _lastPlayGlyph = _playing;
                PlayPauseGlyph.Content = FindResource(_playing ? "IconPauseFill" : "IconPlayFill");
            }
            PlayPauseButton.IsEnabled = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled;
            PreviousButton.IsEnabled = controls.IsPreviousEnabled;
            NextButton.IsEnabled = controls.IsNextEnabled;
            RefreshTimeline();
            if (!includeProperties) return;

            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion || activationVersion != _activationVersion || !IsActive) return;
            var title = properties?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                HasData = false;
                ClearAlbumArt();
                return;
            }

            TitleText.Text = title;
            ArtistText.Text = string.IsNullOrWhiteSpace(properties!.Artist) ? properties.AlbumTitle : properties.Artist;
            Card.ToolTip = string.IsNullOrWhiteSpace(ArtistText.Text) ? title : $"{title}\n{ArtistText.Text}";
            HasData = true;
            await LoadArtAsync(properties, version, activationVersion);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NullReferenceException or ObjectDisposedException)
        {
            // Sessions vanish while their owning app exits; the next CurrentSessionChanged rebinds.
            AppLog.Warning($"Media session refresh failed: {ex.Message}");
            if (version == _refreshVersion && activationVersion == _activationVersion)
            {
                HasData = false;
                ClearAlbumArt();
            }
        }
    }

    private async Task LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties, int version, int activationVersion)
    {
        var key = $"{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
        if (key == _artKey) return;
        // Players often publish artwork in a later event with the same title, so the key is recorded only on success.
        if (properties.Thumbnail is null)
        {
            ArtImage.Source = null;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using var stream = await properties.Thumbnail.OpenReadAsync();
            if (version != _refreshVersion || activationVersion != _activationVersion || !IsActive) return;

            var image = await Task.Run(() =>
            {
                using var managed = stream.AsStreamForRead();
                using var copy = new MemoryStream();
                managed.CopyTo(copy);
                copy.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = AlbumArtDecodePixels;
                bitmap.StreamSource = copy;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            });

            if (version != _refreshVersion || activationVersion != _activationVersion || !IsActive) return;
            ArtImage.Source = image;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
            _artKey = key;
        }
        catch (Exception ex) when (ex is COMException or IOException or NotSupportedException or FileFormatException)
        {
            AppLog.Warning($"Media artwork unavailable: {ex.Message}");
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TrySkipPreviousAsync());
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TryTogglePlayPauseAsync());
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TrySkipNextAsync());

    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingProgress = true;
    }

    private async void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingProgress) return;
        _draggingProgress = false;
        await SeekToSliderValueAsync();
    }

    private void ProgressSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _draggingProgress = false;
    }

    private async void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingProgress || _draggingProgress) return;
        var mouseSeek = ProgressSlider.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed;
        var keyboardSeek = ProgressSlider.IsKeyboardFocusWithin;
        if (!mouseSeek && !keyboardSeek) return;
        await SeekToSliderValueAsync();
    }

    private async Task SeekToSliderValueAsync()
    {
        if (_session is null || !_canSeek) return;
        var target = _startTime + TimeSpan.FromSeconds(ProgressSlider.Value);
        _position = target;
        _positionUpdatedAt = DateTimeOffset.UtcNow;
        await RunAsync(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || _audio is null) return;
        if (_audio.SetVolume((float)(e.NewValue / 100.0))) RenderVolume();
        else Report(UiText.Get("ErrorVolumeChange"));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        if (_audio.ToggleMute()) RenderVolume();
        else Report(UiText.Get("ErrorVolumeChange"));
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_audio?.HasDevice != true) return;
        if (_audio.SetVolume(Math.Clamp(_audio.Volume + Math.Sign(e.Delta) * 0.02f, 0f, 1f))) RenderVolume();
        else Report(UiText.Get("ErrorVolumeChange"));
        e.Handled = true;
    }

    private async Task RunAsync(Func<MediaSession, IAsyncOperation<bool>> command)
    {
        if (_session is null) return;
        try
        {
            if (!await command(_session)) Report(UiText.Get("ErrorMediaControl"));
        }
        catch (COMException ex)
        {
            AppLog.Warning($"Media command failed: {ex.Message}");
            Report(UiText.Get("ErrorMediaControl"));
        }
    }
}
