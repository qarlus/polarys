using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>Stands in for the hidden notification area: lists running background apps that own a tray icon.</summary>
public sealed class TrayWidget : DockWidget
{
    private readonly Button _button;
    private readonly DockFlyout _flyout = new();

    public event Action<string>? CloseApplicationRequested;
    public event Action<string>? TerminateApplicationRequested;

    public TrayWidget()
    {
        var glyph = new ContentControl { Content = FindResource("IconChevronUp"), Width = 16, Height = 16 };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");
        _button = new Button { Content = glyph, Padding = new Thickness(10, 0, 10, 0) };
        _button.SetResourceReference(StyleProperty, "WidgetCardButton");
        _button.Click += Button_Click;
        Localize();
        Content = _button;
    }

    protected override void OnActivated()
    {
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        Localize();
    }

    protected override void OnDeactivated()
    {
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        _flyout.Close();
    }

    private void UiText_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Localize();


    private void Localize()
    {
        _button.ToolTip = UiText.Get("TrayApps");
        System.Windows.Automation.AutomationProperties.SetName(_button, UiText.Get("TrayApps"));
    }

    private async void Button_Click(object sender, RoutedEventArgs e)
    {
        if (_flyout.IsOpen || _flyout.RecentlyClosed(_button))
        {
            _flyout.Close();
            return;
        }

        var apps = await Task.Run(TrayIcons.RunningApps);
        if (!IsActive) return;
        _flyout.Show(_button, BuildContent(apps));
    }

    internal UIElement BuildContent(IReadOnlyList<TrayApp> apps)
    {
        var root = new StackPanel { Width = 308 };
        root.Children.Add(DockFlyout.Heading(UiText.Get("TrayApps")));
        if (apps.Count == 0)
        {
            root.Children.Add(DockFlyout.Caption(UiText.Get("TrayEmpty")));
        }
        else
        {
            var grid = new UniformGrid { Columns = 4 };
            foreach (var app in apps)
                grid.Children.Add(BuildTile(app));
            root.Children.Add(grid);
        }

        var settings = DockFlyout.FlatButton(new TextBlock { Text = UiText.Get("TraySettings"), FontWeight = FontWeights.SemiBold }, (_, _) =>
        {
            _flyout.Close();
            Report(ShellShortcuts.OpenUri("ms-settings:taskbar"));
        });
        settings.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(settings);
        root.Children.Add(DockFlyout.Caption(UiText.Get("TrayNote")));
        return root;
    }

    private UIElement BuildTile(TrayApp app)
    {
        var image = new Image { Source = app.Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 0, 5) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var tileContent = new StackPanel();
        tileContent.Children.Add(image);
        tileContent.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var open = DockFlyout.FlatButton(tileContent, (_, _) =>
        {
            _flyout.Close();
            Report(TrayIcons.Open(app));
        }, app.Name);
        open.Margin = new Thickness(2);
        var menu = new ContextMenu();
        var closeItem = new MenuItem { Header = UiText.Get("CloseApplication"), Icon = FindResource("IconClose") };
        closeItem.Click += (_, _) =>
        {
            _flyout.Close();
            CloseApplicationRequested?.Invoke(app.ExecutablePath);
        };
        var terminateItem = new MenuItem { Header = UiText.Get("Terminate"), Icon = FindResource("IconTerminate") };
        terminateItem.SetResourceReference(StyleProperty, "DestructiveMenuItem");
        terminateItem.Click += (_, _) =>
        {
            _flyout.Close();
            TerminateApplicationRequested?.Invoke(app.ExecutablePath);
        };
        menu.Items.Add(closeItem);
        menu.Items.Add(terminateItem);
        open.ContextMenu = menu;

        // Tray apps may keep running after WM_CLOSE; force quit uses the dock's existing confirmation flow.
        var quitIcon = new ContentControl
        {
            Content = FindResource("IconTerminate"), Width = 11, Height = 11
        };
        quitIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        quitIcon.SetResourceReference(ForegroundProperty, "TextSecondary");
        var quit = DockFlyout.FlatButton(quitIcon, (_, _) =>
        {
            _flyout.Close();
            TerminateApplicationRequested?.Invoke(app.ExecutablePath);
        }, $"{app.Name}: {UiText.Get("Terminate")}");
        quit.HorizontalAlignment = HorizontalAlignment.Right;
        quit.VerticalAlignment = VerticalAlignment.Top;
        quit.Padding = new Thickness(4);

        var cell = new Grid();
        cell.Children.Add(open);
        cell.Children.Add(quit);
        return cell;
    }
}
