using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

internal static class ApplicationInteractionChecks
{
    public static int Run(Application application)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var first = new DockApp("Foreground test", "foreground-test.exe") { WindowCount = 1, IsForeground = true };
        var second = new DockApp("Background test", "background-test.exe") { WindowCount = 1 };
        var firstButton = new Button { Style = (Style)application.FindResource("DockButton"), DataContext = first, Content = "A" };
        var secondButton = new Button { Style = (Style)application.FindResource("DockButton"), DataContext = second, Content = "B" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { firstButton, secondButton } };
        var strip = new StackPanel();
        using var coordinator = new WidgetCoordinator(strip, Dispatcher.CurrentDispatcher, _ => { });
        var tray = strip.Children.OfType<TrayWidget>().Single();
        string? closePath = null;
        string? terminatePath = null;
        coordinator.CloseApplicationRequested += path => closePath = path;
        coordinator.TerminateApplicationRequested += path => terminatePath = path;
        var trayApp = new TrayApp("Background app test", "tray-test.exe", null);
        var content = (StackPanel)tray.BuildContent([trayApp]);
        var row = content.Children.OfType<DockPanel>().Single();
        var terminate = row.Children.OfType<Button>().First();
        var open = row.Children.OfType<Button>().Last();
        var menu = open.ContextMenu ?? throw new InvalidOperationException("Tray app actions must be available.");
        Check(AutomationProperties.GetName(terminate).Contains(trayApp.Name)
            && AutomationProperties.GetName(terminate).Contains(UiText.Get("Terminate")),
            "The visible end-process action must name its application and operation.");
        Check(terminate.Content is ContentControl icon && icon.Style == application.FindResource("WidgetIcon"),
            "The end-process geometry must use the existing icon template.");
        Check(menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(
            new[] { UiText.Get("CloseApplication"), UiText.Get("Terminate") }),
            "Tray apps must expose normal close and confirmed end-process actions.");
        terminate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(terminatePath == trayApp.ExecutablePath && closePath is null,
            "The visible tray action must route only the selected path to process termination.");
        terminatePath = null;
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(closePath == trayApp.ExecutablePath && terminatePath is null,
            "Normal close must stay separate from forced termination.");
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(terminatePath == trayApp.ExecutablePath,
            "The context menu must route to the same termination flow as the visible action.");
        var empty = (StackPanel)tray.BuildContent([]);
        Check(!empty.Children.OfType<DockPanel>().Any()
            && empty.Children.OfType<TextBlock>().Any(text => text.Text == UiText.Get("TrayEmpty")),
            "An empty tray must show its empty state without application actions.");

        var panel = new StackPanel { Children = { buttons, content } };
        panel.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
        panel.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "InterfaceFont");
        var window = new Window
        {
            Title = "OpenTaskbar interaction checks",
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        try
        {
            window.Show();
            window.Activate();
            Check(secondButton.Focus(), "The regression check must give the background button actual keyboard focus.");
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                panel.Background = (Brush)application.FindResource("SurfacePopup");
                Flush();
                var firstSurface = (Border)firstButton.Template.FindName("Surface", firstButton);
                var secondSurface = (Border)secondButton.Template.FindName("Surface", secondButton);
                var firstGlow = (FrameworkElement)firstButton.Template.FindName("HoverGlow", firstButton);
                var secondGlow = (FrameworkElement)secondButton.Template.FindName("HoverGlow", secondButton);
                var firstCapsule = (FrameworkElement)firstButton.Template.FindName("ActiveIndicatorGlow", firstButton);
                var secondCapsule = (FrameworkElement)secondButton.Template.FindName("ActiveIndicatorGlow", secondButton);
                Check(firstGlow.Opacity == 0.55 && firstCapsule.Visibility == Visibility.Visible
                    && IsTransparent(firstSurface.Background),
                    "The foreground application must carry the resting aurora glow without keyboard focus.");
                Check(secondButton.IsKeyboardFocused && IsTransparent(secondSurface.Background) && secondGlow.Opacity == 0,
                    "Keyboard focus on a background app must not create an active glow.");
                Check(secondSurface.BorderBrush == application.FindResource("StrokeFocus"),
                    "Keyboard navigation must retain a visible focus outline.");
                first.IsForeground = false;
                second.IsForeground = true;
                Flush();
                Check(firstGlow.Opacity == 0 && firstCapsule.Visibility == Visibility.Collapsed
                    && secondGlow.Opacity == 0.55 && secondCapsule.Visibility == Visibility.Visible,
                    "Switching applications must move the foreground glow immediately.");
                second.IsForeground = false;
                Flush();
                Check(secondButton.IsKeyboardFocused && IsTransparent(secondSurface.Background) && secondGlow.Opacity == 0,
                    "Minimizing the selected app must clear its glow even while its button retains focus.");
                first.IsForeground = true;
                Flush();
                var directory = Path.Combine(Path.GetTempPath(), "OpenTaskbarInteractionChecks");
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"{theme}.png"));
                encoder.Save(output);
            }

            panel.Children.Remove(content);
            var flyout = new OpenTaskbar.DockFlyout();
            try
            {
                flyout.Show(firstButton, content, animate: false);
                Flush();
                menu.PlacementTarget = open;
                menu.IsOpen = true;
                Flush();
                Check(menu.IsOpen && menu.IsVisible,
                    "The tray context menu must remain available inside the real dock flyout.");
            }
            finally
            {
                menu.IsOpen = false;
                flyout.Close();
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(AppTheme.Dark);
        }

        return checks;

        void Flush()
        {
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            panel.UpdateLayout();
        }
    }

    private static bool IsTransparent(Brush brush) => brush is SolidColorBrush solid && solid.Color.A == 0;
}
