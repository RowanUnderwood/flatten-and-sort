using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaFlattener;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int exit = 0;
        try {
            RunEngineTests();
            var app = new App(); app.InitializeComponent(); app.MainWindow = new MainWindow(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            app.Dispatcher.InvokeAsync(async () => {
                try { await UiTest(args.FirstOrDefault()); Console.WriteLine($"PASS: {passed} test groups."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
                finally { app.Shutdown(); }
            });
            app.Run();
        } catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
        finally { DeleteTestRoots(); }
        return exit;
    }
    private static async Task UiTest(string? screenshotPath)
    {
        string root = Root();
        FileAt(root, "Summer trip/Beach.JPG", "photo", 3);
        FileAt(root, "Phone backup/Beach.JPG", "second photo", 7);
        FileAt(root, "Phone backup/Sunset.HEIC", "heic", 12);
        FileAt(root, "Camera/Coast.mp4", "clip", 9);
        FixtureAt(root, "Camera/VR coast.mp4", "spherical-v2.mp4");
        FileAt(root, "Documents/Trip notes.txt", "notes", 15);
        var window = (MainWindow)Application.Current.MainWindow;
        window.ShowInTaskbar = false; window.Left = -20000; window.Top = -20000;
        window.Show();
        try {
            T Get<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            Check(Get<CheckBox>("SortVrVideos").IsChecked != true, "VR sorting starts unchecked.");
            Check(Get<CheckBox>("DeleteEmptyFolders").IsChecked != true, "Cleanup starts unchecked.");
            Get<TextBox>("RootPath").Text = root;
            Get<CheckBox>("DateFolders").IsChecked = true;
            Get<TextBox>("FileLimit").Text = "2";
            Get<Button>("ScanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<Button>("ContinueButton").IsEnabled, "UI scan enables Continue.");
            Check(Get<TextBlock>("ImageCount").Text == "3", "Image count shown.");
            Check(Get<DataGrid>("PreviewGrid").Items.Count == 6, "Full preview.");
            Check(File.Exists(Path.Combine(root, "Summer trip/Beach.JPG")), "UI preview is read only.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 2;
            Check(Get<DataGrid>("PreviewGrid").Items.Count == 2, "Preview filtering.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 0;
            Get<CheckBox>("SortVrVideos").IsChecked = true;
            Check(!Get<Button>("ContinueButton").IsEnabled, "Changing VR option invalidates preview.");
            Check(Get<Border>("VrCard").Visibility == Visibility.Visible, "VR count card enabled.");
            Get<CheckBox>("DeleteEmptyFolders").IsChecked = true;
            Check(!Get<Button>("ContinueButton").IsEnabled, "Changing cleanup invalidates the preview.");
            Get<Button>("ScanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<TextBlock>("PreviewSummary").Text.Contains("Cleanup will check 4"), "Cleanup count preview.");
            Check(Get<TextBlock>("VrCount").Text == "1", "VR count shown.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 4;
            Check(Get<DataGrid>("PreviewGrid").Items.Count == 1, "VR category filter.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 0;
            window.UpdateLayout();
            await Task.Delay(200);
            if (screenshotPath is not null) {
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(screenshotPath); encoder.Save(stream);
            }
            Get<Button>("ContinueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<TextBlock>("Status").Text == "Your files are organized", "UI completion.");
            Check(!Get<Button>("ContinueButton").IsEnabled, "Cannot run stale preview twice.");
            Check(Scan(root).Items.Count == 0, "UI moved all candidates.");
            Check(!Directory.Exists(Path.Combine(root, "Summer trip")), "UI cleanup removes emptied source.");
            string emptyRoot = Root();
            Directory.CreateDirectory(Path.Combine(emptyRoot, "empty/child"));
            Get<TextBox>("RootPath").Text = emptyRoot;
            Get<Button>("ScanButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<Button>("ContinueButton").IsEnabled, "UI allows cleanup without file moves.");
            Get<Button>("ContinueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(!Directory.Exists(Path.Combine(emptyRoot, "empty")) && Directory.Exists(emptyRoot), "UI cleanup-only completion.");
            passed++; Console.WriteLine("PASS WPF integration: scan, preview, filter, optional cleanup and cleanup-only completion");
        } finally { window.Close(); }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("UI operation timed out.");
    }
}
