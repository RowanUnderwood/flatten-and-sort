using System.IO;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using MediaFlattener;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        int exit = 0;
        try {
            RunEngineTests();
            using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            session.Dispatch(async () => {
                await UiTest(args.FirstOrDefault());
                Console.WriteLine($"PASS: {passed} test groups.");
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        } catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
        finally { DeleteTestRoots(); }
        return exit;
    }
    // Entry point used by HeadlessUnitTestSession; real Skia rendering enables the screenshot.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static async Task UiTest(string? screenshotPath)
    {
        string root = Root();
        FileAt(root, "Summer trip/Beach.JPG", "photo", 3);
        FileAt(root, "Phone backup/Beach.JPG", "second photo", 7);
        FileAt(root, "Phone backup/Sunset.HEIC", "heic", 12);
        FileAt(root, "Camera/Coast.mp4", "clip", 9);
        FixtureAt(root, "Camera/VR coast.mp4", "spherical-v2.mp4");
        FileAt(root, "Documents/Trip notes.txt", "notes", 15);
        var window = new MainWindow();
        window.Show();
        try {
            T Get<T>(string name) where T : Control => window.FindControl<T>(name)!;
            void Click(string name) => Get<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            int Rows() => ((DataGridCollectionView)Get<DataGrid>("PreviewGrid").ItemsSource!).Count;
            Check(Get<CheckBox>("SortVrVideos").IsChecked != true, "VR sorting starts unchecked.");
            Check(Get<CheckBox>("DeleteEmptyFolders").IsChecked != true, "Cleanup starts unchecked.");
            Get<TextBox>("RootPath").Text = root;
            Get<CheckBox>("DateFolders").IsChecked = true;
            Get<TextBox>("FileLimit").Text = "2";
            Click("ScanButton");
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<Button>("ContinueButton").IsEnabled, "UI scan enables Continue.");
            Check(Get<TextBlock>("ImageCount").Text == "3", "Image count shown.");
            Check(Rows() == 6, "Full preview.");
            Check(File.Exists(Path.Combine(root, "Summer trip/Beach.JPG")), "UI preview is read only.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 2;
            Check(Rows() == 2, "Preview filtering.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 0;
            Get<CheckBox>("SortVrVideos").IsChecked = true;
            Check(!Get<Button>("ContinueButton").IsEnabled, "Changing VR option invalidates preview.");
            Check(Get<Border>("VrCard").IsVisible, "VR count card enabled.");
            Get<CheckBox>("DeleteEmptyFolders").IsChecked = true;
            Check(!Get<Button>("ContinueButton").IsEnabled, "Changing cleanup invalidates the preview.");
            Click("ScanButton");
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<TextBlock>("PreviewSummary").Text!.Contains("Cleanup will check 4"), "Cleanup count preview.");
            Check(Get<TextBlock>("VrCount").Text == "1", "VR count shown.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 4;
            Check(Rows() == 1, "VR category filter.");
            Get<ComboBox>("CategoryFilter").SelectedIndex = 0;
            if (screenshotPath is not null) {
                await Task.Delay(200);
                window.CaptureRenderedFrame()!.Save(screenshotPath);
            }
            Click("ContinueButton");
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<TextBlock>("Status").Text == "Your files are organized", "UI completion.");
            Check(!Get<Button>("ContinueButton").IsEnabled, "Cannot run stale preview twice.");
            Check(Scan(root).Items.Count == 0, "UI moved all candidates.");
            Check(!Directory.Exists(Path.Combine(root, "Summer trip")), "UI cleanup removes emptied source.");
            string emptyRoot = Root();
            Directory.CreateDirectory(Path.Combine(emptyRoot, "empty/child"));
            Get<TextBox>("RootPath").Text = emptyRoot;
            Click("ScanButton");
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(Get<Button>("ContinueButton").IsEnabled, "UI allows cleanup without file moves.");
            Click("ContinueButton");
            await Until(() => Get<Button>("ScanButton").IsEnabled);
            Check(!Directory.Exists(Path.Combine(emptyRoot, "empty")) && Directory.Exists(emptyRoot), "UI cleanup-only completion.");
            passed++; Console.WriteLine("PASS Avalonia integration: scan, preview, filter, optional cleanup and cleanup-only completion");
        } finally { window.Close(); }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("UI operation timed out.");
    }
}
