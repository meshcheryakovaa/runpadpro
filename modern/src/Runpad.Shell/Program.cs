using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using Runpad.Contracts;
using Runpad.Core;

namespace Runpad.Shell;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\" + Paths.PipeName + "-shell", out var first);
        if (!first) return;
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => { Log.Write("shell", e.Exception.Message); MessageBox.Show(e.Exception.Message, "Runpad Pro"); e.Handled = true; };
        app.Run(new ShellWindow());
    }
}

internal sealed class ShellWindow : Window
{
    private readonly TextBlock status = new() { Foreground = Brushes.LightSteelBlue, Margin = new Thickness(16, 10, 16, 10) };
    private readonly StackPanel modules = new() { Margin = new Thickness(22) };
    private readonly WrapPanel applications = new() { Margin = new Thickness(18) };
    private readonly WebView2 wallpaper = new() { Height = 190, Margin = new Thickness(20, 10, 20, 0) };
    private readonly TextBox search = new() { Margin = new Thickness(22, 18, 22, 8), Padding = new Thickness(10), FontSize = 16 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool refreshing;
    private string? loadedWallpaper;
    public ShellWindow()
    {
        Title = "Runpad Pro — рабочее пространство";
        Width = 1180; Height = 760; MinWidth = 720; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(16, 26, 40));
        Foreground = Brushes.White;
        Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Style TargetType="Button">
            <Setter Property="Background" Value="#203449"/><Setter Property="Foreground" Value="#F1F7FA"/>
            <Setter Property="BorderBrush" Value="#3D586B"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8">
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#31556C"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox"><Setter Property="Background" Value="#16283A"/><Setter Property="Foreground" Value="#EFF5FA"/><Setter Property="CaretBrush" Value="White"/><Setter Property="BorderBrush" Value="#3D586B"/></Style>
          <Style TargetType="TabControl"><Setter Property="Background" Value="#101A28"/><Setter Property="Foreground" Value="#EFF5FA"/><Setter Property="BorderBrush" Value="#304557"/></Style>
          <Style TargetType="TabItem"><Setter Property="Foreground" Value="#EFF5FA"/><Setter Property="Background" Value="#1A2B3E"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border Background="{TemplateBinding Background}" Padding="18,12" CornerRadius="6,6,0,0" Margin="0,0,4,0"><ContentPresenter ContentSource="Header"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#31556C"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#EFF5FA"/></Style>
        </ResourceDictionary>
        """));
        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var header = new DockPanel { Margin = new Thickness(22, 18, 22, 10) };
        header.Children.Add(new TextBlock { Text = "RUNPAD PRO", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var fullscreen = Button("Полный экран", (_, _) => { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; });
        buttons.Children.Add(fullscreen);
        buttons.Children.Add(Button("Папка данных", (_, _) => { Directory.CreateDirectory(Paths.Data); Process.Start(new ProcessStartInfo(Paths.Data) { UseShellExecute = true }); }));
        header.Children.Add(buttons); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl { Margin = new Thickness(12) };
        var desktop = new DockPanel(); DockPanel.SetDock(wallpaper, Dock.Top); desktop.Children.Add(wallpaper);
        DockPanel.SetDock(search, Dock.Top); desktop.Children.Add(search);
        desktop.Children.Add(new ScrollViewer { Content = applications, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        tabs.Items.Add(new TabItem { Header = "Рабочий стол", Content = desktop });
        tabs.Items.Add(new TabItem { Header = "Модули", Content = new ScrollViewer { Content = modules } });
        tabs.Items.Add(new TabItem { Header = "Настройки", Content = BuildSettings() });
        root.Children.Add(tabs); Content = root;
        search.TextChanged += (_, _) => LoadApplications();
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await EnsureAgentAsync(); LoadApplications(); await RefreshAsync(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
    private static Button Button(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4) };
        button.Click += action; return button;
    }
    private void LoadApplications()
    {
        applications.Children.Clear();
        foreach (var app in Settings.Load().Applications.Where(a => (a.Name + " " + a.Category).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
        {
            var tile = Button(app.Name + "\n" + app.Category, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(app.Target, app.Arguments) { UseShellExecute = true }); }
                catch (Exception e) { MessageBox.Show("Не удалось открыть приложение: " + e.Message, "Runpad Pro"); }
            });
            tile.Width = 220; tile.Height = 105; tile.FontSize = 17;
            applications.Children.Add(tile);
        }
    }
    private UIElement BuildSettings()
    {
        var s = Settings.Load();
        var panel = new StackPanel { Margin = new Thickness(26), MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Настройки этого пользователя", FontSize = 24, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Название класса" });
        var classroom = new TextBox { Text = s.Classroom, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 16), MinWidth = 400 };
        panel.Children.Add(classroom);
        panel.Children.Add(new TextBlock { Text = "Частота обновления (мс, от 1000)" });
        var interval = new TextBox { Text = s.RefreshMilliseconds.ToString(), Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 16) };
        panel.Children.Add(interval);
        var capture = new CheckBox { Content = "Передавать миниатюры экрана в центр управления", IsChecked = s.ShareScreens, Margin = new Thickness(0, 8, 0, 16) };
        panel.Children.Add(capture);
        panel.Children.Add(Button("Сохранить", async (_, _) =>
        {
            try
            {
                Settings.Save(Settings.Load() with { Classroom = classroom.Text, RefreshMilliseconds = int.Parse(interval.Text), ShareScreens = capture.IsChecked == true });
                timer.Interval = TimeSpan.FromMilliseconds(Settings.Load().RefreshMilliseconds);
                await RefreshAsync(); MessageBox.Show("Настройки сохранены.", "Runpad Pro");
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Настройки"); }
        }));
        panel.Children.Add(Button("Открыть config.json", (_, _) => { if (!File.Exists(Settings.FileName)) Settings.Save(Settings.Load()); Process.Start(new ProcessStartInfo("notepad.exe", Settings.FileName) { UseShellExecute = true }); }));
        panel.Children.Add(new TextBlock { Text = "Приложения и HTML-фон настраиваются в config.json.\nПодключение к серверу: runpad enroll --server URL\nПодробные команды: runpad help", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
        return panel;
    }
    private static async Task EnsureAgentAsync()
    {
        try { using var ct = new CancellationTokenSource(700); await Ipc.CallAsync(Paths.PipeName, "status", ct: ct.Token); }
        catch
        {
            Process.Start(new ProcessStartInfo(Paths.Component("Agent")) { UseShellExecute = true });
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(300);
                try { using var ct = new CancellationTokenSource(300); await Ipc.CallAsync(Paths.PipeName, "status", ct: ct.Token); return; } catch { }
            }
        }
    }
    private async Task RefreshAsync()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            var response = await Ipc.CallAsync(Paths.PipeName, "status");
            if (!response.Ok) throw new IOException(response.Error);
            status.Text = Environment.MachineName + "  •  " + Settings.Load().Classroom + "  •  " + DateTime.Now.ToString("HH:mm:ss") +
                (response.Result.TryGetProperty("serverError", out var error) && error.ValueKind == JsonValueKind.String ? "  •  Сервер: " + error.GetString() : "");
            modules.Children.Clear();
            foreach (var item in response.Result.GetProperty("modules").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var card = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                card.Children.Add(new TextBlock { Text = item.GetProperty("name").GetString() + "  ·  " + item.GetProperty("state").GetString(), FontSize = 19 });
                var summary = "Версия " + item.GetProperty("version").GetString();
                if (item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (id == "monitoring")
                    {
                        var battery = data.GetProperty("batteryPercent");
                        summary += "  ·  " + (battery.ValueKind == JsonValueKind.Number ? "Батарея " + battery.GetInt32() + "%" : "Стационарный компьютер / батарея недоступна");
                    }
                    else if (id == "yandex-disk") summary += data.GetProperty("configured").GetBoolean() ? "  ·  Диск подключён" : "  ·  Подключите OAuth-токен через CLI";
                    else if (id == "wallpaper") summary += "  ·  HTML-фон рабочего пространства";
                    else if (data.TryGetProperty("message", out var message)) summary += "  ·  " + message.GetString();
                    if (data.TryGetProperty("error", out var dataError) && dataError.ValueKind == JsonValueKind.String) summary += "\n" + dataError.GetString();
                }
                if (item.TryGetProperty("error", out var moduleError) && moduleError.ValueKind == JsonValueKind.String) summary += "\n" + moduleError.GetString();
                card.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 8, 0, 6) });
                var enabled = item.GetProperty("enabled").GetBoolean();
                card.Children.Add(Button(enabled ? "Отключить" : "Включить", async (_, _) =>
                {
                    var settings = Settings.Load();
                    Settings.Save(settings with
                    {
                        DisabledModules = enabled ? settings.DisabledModules.Append(id).Distinct().ToArray() : settings.DisabledModules.Where(x => x != id).ToArray(),
                        EnabledModules = enabled ? settings.EnabledModules.Where(x => x != id).ToArray() : settings.EnabledModules.Append(id).Distinct().ToArray()
                    });
                    await Ipc.CallAsync(Paths.PipeName, "refresh"); await RefreshAsync();
                }));
                modules.Children.Add(card);
            }
            await LoadWallpaperAsync();
            var notifications = await Ipc.CallAsync(Paths.PipeName, "notifications");
            if (notifications.Ok) foreach (var text in notifications.Result.EnumerateArray()) MessageBox.Show(this, text.GetString(), "Сообщение преподавателя");
        }
        catch (Exception e) { status.Text = "Агент недоступен: " + e.Message; }
        finally { refreshing = false; }
    }
    private async Task LoadWallpaperAsync()
    {
        var module = new ModuleCatalog().Scan().FirstOrDefault(m => m.Manifest.Id == "wallpaper" && m.Enabled && m.Error is null);
        var path = Settings.Load().WallpaperFile ?? (module is null ? null : Path.Combine(module.Directory, "wallpaper.html"));
        if (path == loadedWallpaper) return;
        loadedWallpaper = path;
        if (path is null || !File.Exists(path)) { wallpaper.Visibility = Visibility.Collapsed; return; }
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Paths.Data, "webview2"));
            await wallpaper.EnsureCoreWebView2Async(environment);
            wallpaper.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            wallpaper.CoreWebView2.Settings.IsWebMessageEnabled = false;
            wallpaper.CoreWebView2.Settings.AreDevToolsEnabled = false;
            wallpaper.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            wallpaper.Visibility = Visibility.Visible;
            wallpaper.Source = new Uri(Path.GetFullPath(path));
        }
        catch (Exception e) { wallpaper.Visibility = Visibility.Collapsed; Log.Write("shell", "HTML-фон: " + e.Message); }
    }
}
