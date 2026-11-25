using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.Tools;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var monitor = new DesktopPet.MonitorService(Path.Combine(args[0], "isolated-monitor"), Dispatcher.CurrentDispatcher, () => false);
        var advancedType = typeof(ToolsWindow).Assembly.GetType("DesktopPet.MonitorAdvancedWindow")!;
        var window = (Window)Activator.CreateInstance(advancedType, monitor)!;
        window.ShowActivated = false;
        var navigation = (System.Windows.Controls.ListBox)advancedType.GetField("_navigation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        navigation.SelectedIndex = 0;
        var body = (ContentControl)advancedType.GetField("_body", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        var manager = (StackPanel)((Border)body.Content).Child;
        window.Show(); Pump(300);
        var installed = (StackPanel)manager.GetType().GetField("_body", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!;
        Check(installed.Children.OfType<LocalModelPage>().Count() == 0, "installed list does not expand model controls");
        var row = installed.Children.OfType<Grid>().First();
        var toggle = row.Children.OfType<System.Windows.Controls.CheckBox>().Single();
        var originalToggle = toggle.IsChecked;
        toggle.IsChecked = true; Pump(200);
        var thumb = (TranslateTransform)toggle.Template.FindName("thumb", toggle);
        Check(Math.Abs(thumb.X - 14) < 0.1, "plugin sliding switch animated on");
        toggle.IsChecked = false; Pump(200); Check(Math.Abs(thumb.X) < 0.1, "plugin sliding switch animated off"); toggle.IsChecked = originalToggle; Pump(200);
        SaveImage(window, args[0], "installed-plugins.png");
        manager.GetType().GetField("_modelOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(manager, true);
        manager.GetType().GetMethod("Refresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(manager, null);
        var extension = (StackPanel)manager.GetType().GetField("_body", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!;
        try
        {
            Pump(args.Contains("--offline") ? 6500 : 1800);
            var page = extension.Children.OfType<LocalModelPage>().Single();
            var slider = page.Children.OfType<Slider>().Single();
            var combo = page.Children.OfType<System.Windows.Controls.ComboBox>().Single();
            if (args.Contains("--offline"))
            {
                Check(!slider.IsEnabled && page.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Ollama 服务：离线")), "offline page offers service recovery");
                return 0;
            }
            Check(slider.IsEnabled && slider.Maximum == 262144, "live metadata slider range");
            Check(combo.Items.Count == 2, "weight metadata groups installed context variants");
            slider.Focus(); var previous = slider.Value;
            slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider)!, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(slider.Value == previous + 1024, "keyboard right increments 1K");
            var apply = page.Children.OfType<System.Windows.Controls.Button>().First(b => (string)b.Content == "应用上下文");
            apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); WaitForIdle(page);
            var endpoint = "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("PET_MODEL_GATEWAY_PORT") ?? "11435");
            var state = System.Text.Json.Nodes.JsonNode.Parse(new System.Net.Http.HttpClient().GetStringAsync(endpoint + "/control/status").GetAwaiter().GetResult())!;
            Check(state["context"]!.GetValue<int>() == previous + 1024, "native apply button persists context");
            slider.Value = previous;
            apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); WaitForIdle(page);
            state = System.Text.Json.Nodes.JsonNode.Parse(new System.Net.Http.HttpClient().GetStringAsync(endpoint + "/control/status").GetAwaiter().GetResult())!;
            Check(state["context"]!.GetValue<int>() == previous, "native context setting restored");
            SaveImage(window, args[0], "model-page.png");
            if (args.Contains("--setup"))
            {
                InspectDialog(page, "环境设置／首次初始化", "环境设置", dialog =>
                {
                    var controls = (StackPanel)((Border)((ScrollViewer)dialog.Content).Content).Child;
                    var runtimePath = Path.Combine(Environment.GetEnvironmentVariable("PET_MODEL_DATA_ROOT")!, "model-runtime.json");
                    var expectedStartup = !File.Exists(runtimePath) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(runtimePath))!["LoginStartup"]!.GetValue<bool>();
                    Check(controls.Children.OfType<System.Windows.Controls.CheckBox>().Single().IsChecked == expectedStartup, "setup preserves saved startup choice and defaults new installs on");
                    Check(controls.Children.OfType<System.Windows.Controls.ComboBox>().Count() == 2, "discovered endpoint and executable selectors");
                    SaveImage(dialog, args[0], "environment-setup.png");
                });
                InspectDialog(page, "接入 DSH / OpenCode／解除接入", "接入客户端", dialog =>
                {
                    var controls = (StackPanel)((Border)((ScrollViewer)dialog.Content).Content).Child;
                    var confirm = controls.Children.OfType<System.Windows.Controls.Button>().Single(b => ((string)b.Content).StartsWith("确认接入"));
                    Check(!confirm.IsEnabled, "client write requires a fresh preview");
                    Check(!string.IsNullOrWhiteSpace(controls.Children.OfType<System.Windows.Controls.TextBox>().First().Text), "client config path is visible and editable");
                    var defaults = controls.Children.OfType<System.Windows.Controls.CheckBox>().First(); Check(defaults.IsChecked == false, "client default model preserved by default");
                    Check(controls.Children.OfType<System.Windows.Controls.Button>().Count(b => (string)b.Content == "解除此客户端接入") == 2, "managed targets and detach controls render");
                    SaveImage(dialog, args[0], "client-setup.png");
                });
            }
            window.Close(); Pump(50); Check(true, "native page render and lifecycle");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); app.Shutdown(); }
    }
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static void SaveImage(Window window, string root, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(root);
        using var file = File.Create(Path.Combine(root, name)); encoder.Save(file);
    }
    static void InspectDialog(LocalModelPage page, string buttonText, string title, Action<Window> inspect)
    {
        var inspected = false; Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title.Contains(title));
            if (dialog is null) return;
            timer.Stop(); try { inspect(dialog); inspected = true; } catch (Exception ex) { failure = ex; } finally { dialog.Close(); }
        };
        timer.Start();
        page.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == buttonText).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        WaitForIdle(page); timer.Stop();
        if (failure is not null) throw failure;
        Check(inspected, "native " + title + " dialog");
    }
    static void WaitForIdle(LocalModelPage page)
    {
        var busy = typeof(LocalModelPage).GetField("busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((bool)busy.GetValue(page)! && DateTime.UtcNow < deadline) Pump(50);
        if ((bool)busy.GetValue(page)!) throw new Exception("native action timed out");
    }
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
