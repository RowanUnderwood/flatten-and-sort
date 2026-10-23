using System.IO;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MediaFlattener;

public partial class MainWindow : Window
{
    private ScanPlan? plan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private string? logFolder;

    public MainWindow() { InitializeComponent(); }
    private void OptionsChanged(object? sender, RoutedEventArgs e)
    {
        if (ContinueButton is null) return;
        bool vr = SortVrVideos.IsChecked == true;
        VrCard.IsVisible = VrFilter.IsVisible = vr;
        CategoryCounts.Columns = vr ? 4 : 3;
        if (!vr && CategoryFilter.SelectedItem == VrFilter) CategoryFilter.SelectedIndex = 0;
        OutputHint.Text = vr
            ? "Files go into image, video, other, and vr-video. VR checks read metadata and can slow scans. Hover over Type for the detection reason."
            : "Files go into image, video, and other. Existing output folders (including vr-video) are excluded from scans.";
        FileLimit.IsEnabled = !busy && DateFolders.IsChecked == true;
        Footer.Text = DeleteEmptyFolders.IsChecked == true
            ? "After moving, empty source folders will be deleted. Root and output folders are kept."
            : "Duplicate names get a suffix. Original empty folders remain.";
        if (busy) return;
        plan = null;
        ContinueButton.IsEnabled = false;
        PreviewGrid.ItemsSource = null;
        SetCounts([]);
        PreviewSummary.Text = "Scan this folder to build a fresh preview.";
        Status.Text = "Ready to scan";
        Detail.Text = "Scanning creates a preview. Files move only when you press Continue.";
        ProgressCount.Text = "";
        ProgressBar.Value = 0;
        Issues.Text = "";
        IssuesTab.Header = "Scan notes";
    }
    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions { Title = "Select the root folder to flatten", AllowMultiple = false };
        if (Directory.Exists(RootPath.Text))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(RootPath.Text);
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) RootPath.Text = path;
    }
    private void SetBusy(bool value)
    {
        busy = value;
        RootPath.IsEnabled = BrowseButton.IsEnabled = ScanButton.IsEnabled = DateFolders.IsEnabled = DeleteEmptyFolders.IsEnabled = SortVrVideos.IsEnabled = !value;
        FileLimit.IsEnabled = !value && DateFolders.IsChecked == true;
        CancelButton.IsEnabled = value;
        ContinueButton.IsEnabled = !value && plan?.HasWork == true;
        ContinueButton.Content = plan?.Options.DeleteEmptyFolders == true
            ? "Continue → Move & clean up" : "Continue → Move files";
    }
    private IProgress<WorkProgress> Reporter()
    {
        // Coalesce worker updates so large directory trees cannot flood the UI dispatcher.
        WorkProgress? latest = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var update = Interlocked.Exchange(ref latest, null);
            if (update is not null) ShowProgress(update);
            if (!busy) timer.Stop();
        };
        timer.Start();
        return new CallbackProgress(p => Interlocked.Exchange(ref latest, p));
    }
    private sealed class CallbackProgress(Action<WorkProgress> report) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => report(value); }
    private void ShowProgress(WorkProgress p)
    {
        if (!busy) return;
        Status.Text = p.Phase switch { "Scanning" => "Scanning your folder…", "Probing" => "Checking video metadata…", "Planning" => "Building your preview…", "Moving" => "Moving files…", "Cleaning" => "Removing empty source folders…", _ => "Preparing preview…" };
        Detail.Text = p.Detail;
        ProgressCount.Text = p.Phase is "Moving" or "Cleaning" ? $"{p.Count:N0} / {p.Total:N0}" : $"{p.Count:N0} files found";
        ProgressBar.IsIndeterminate = p.Phase is not ("Moving" or "Cleaning");
        if (p.Total > 0) ProgressBar.Value = 100.0 * p.Count / p.Total;
    }
    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        int limit = 1000;
        if (DateFolders.IsChecked == true && (!int.TryParse(FileLimit.Text, out limit) || limit < 1 || limit > 1_000_000))
        { await MessageDialog.Show(this, "Enter a file limit between 1 and 1,000,000.", "Check folder limit"); return; }
        if (string.IsNullOrWhiteSpace(RootPath.Text))
        { await MessageDialog.Show(this, "Choose a root folder first.", "Choose a folder"); return; }
        var options = new ScanOptions(DateFolders.IsChecked == true, limit, DeleteEmptyFolders.IsChecked == true, SortVrVideos.IsChecked == true);
        string root = RootPath.Text.Trim();
        plan = null;
        PreviewGrid.ItemsSource = null;
        SetCounts([]);
        LogButton.IsVisible = false;
        Issues.Text = "";
        PreviewSummary.Text = "Scanning… no files have been moved.";
        cancellation = new();
        SetBusy(true);
        ProgressBar.IsIndeterminate = true;
        Status.Text = "Scanning your folder…";
        var progress = Reporter();
        try
        {
            plan = await Task.Run(() => Engine.Scan(root, options, progress, cancellation.Token, AppContext.BaseDirectory));
            SetCounts(plan.Items);
            ApplyFilter();
            int folders = plan.Items.Select(i => Path.GetDirectoryName(i.Destination)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            PreviewSummary.Text = $"{plan.Items.Count:N0} files will move into {folders:N0} destination folders. {plan.Skipped:N0} reserved folders / skipped entries. Review destinations before continuing.";
            if (plan.Options.DeleteEmptyFolders)
                PreviewSummary.Text += $" Cleanup will check {plan.SourceDirectories.Count:N0} source folders and delete only those that are empty.";
            Issues.Text = plan.Warnings.Count == 0 ? "No scan problems found. Existing image, video, other, vr-video, and .flattener-logs folders are excluded. Symlinks are not followed." : string.Join(Environment.NewLine, plan.Warnings);
            IssuesTab.Header = $"Scan notes ({plan.Warnings.Count:N0})";
            Status.Text = plan.HasWork ? "Preview ready — review, then continue" : "No files to move";
            Detail.Text = plan.Warnings.Count > 0 ? $"Scan has {plan.Warnings.Count:N0} notes. See Scan notes for details." : "No files have been moved. Continue starts the moves shown in the preview.";
            ProgressCount.Text = $"{plan.Items.Count:N0} files";
            ProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Scan cancelled";
            Detail.Text = "No files were moved. Scan again to build a complete preview.";
            PreviewSummary.Text = "Scan cancelled. No partial plan will be executed.";
            ProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            Status.Text = "Scan could not finish";
            Detail.Text = ex.Message;
            PreviewSummary.Text = "Resolve the problem and scan again.";
            ProgressBar.IsIndeterminate = false; cancellation.Dispose(); cancellation = null; SetBusy(false);
            await MessageDialog.Show(this, ex.Message, "Scan failed");
            return;
        }
        ProgressBar.IsIndeterminate = false; cancellation.Dispose(); cancellation = null; SetBusy(false);
    }
    private void SetCounts(List<MoveItem> items)
    {
        var labels = new[] { (ImageCount, ImageSize), (VideoCount, VideoSize), (OtherCount, OtherSize), (VrCount, VrSize) };
        for (int i = 0; i < Engine.Categories.Length; i++)
        {
            var files = items.Where(f => f.Category == Engine.Categories[i]).ToList();
            labels[i].Item1.Text = files.Count.ToString("N0");
            labels[i].Item2.Text = Engine.FormatBytes(files.Sum(f => f.Stamp.Length));
        }
    }
    private void FilterChanged(object? sender, SelectionChangedEventArgs e) { if (PreviewGrid is not null) ApplyFilter(); }
    private void ApplyFilter()
    {
        if (plan is null) return;
        var view = new DataGridCollectionView(plan.Items);
        string selected = (CategoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All files";
        if (selected != "All files") view.Filter = item => ((MoveItem)item).Category == selected;
        PreviewGrid.ItemsSource = view;
    }
    private async void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (plan is null || busy) return;
        var currentPlan = plan;
        // The preview and this explicit Continue action are the user's confirmation.
        plan = null;
        cancellation = new();
        SetBusy(true);
        ContinueButton.IsEnabled = false;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        Status.Text = "Moving files…";
        Footer.Text = "Cancel stops after the current file. Completed moves remain in their destination.";
        var progress = Reporter();
        string? failure = null;
        try
        {
            var result = await Task.Run(() => Engine.Execute(currentPlan, progress, cancellation.Token));
            logFolder = Path.GetDirectoryName(result.LogPath);
            LogButton.IsVisible = true;
            Status.Text = result.Cancelled ? "Stopped — completed moves were kept" : result.Errors.Count > 0 ? "Finished with skipped items" : "Your files are organized";
            ProgressCount.Text = $"{result.Moved:N0} moved · {result.Failed:N0} failed";
            int remaining = currentPlan.Items.Count - result.Moved - result.Failed;
            Detail.Text = $"{result.RemovedFolders:N0} empty folders deleted. {remaining:N0} unprocessed. A detailed move journal is saved in {result.LogPath}";
            ProgressBar.Value = currentPlan.Items.Count == 0 ? (result.Cancelled ? 0 : 100) : 100.0 * (result.Moved + result.Failed) / currentPlan.Items.Count;
            Issues.Text = result.Errors.Count == 0 ? "No move errors. The move journal records source and destination for each file." : string.Join(Environment.NewLine, result.Errors);
            IssuesTab.Header = $"Move notes ({result.Errors.Count:N0})";
            PreviewSummary.Text = $"Completed session: {result.RemovedFolders:N0} empty folders deleted, {result.CleanupFailed:N0} cleanup errors; {result.Moved:N0} moved, {result.Failed:N0} failed, {remaining:N0} unprocessed. Table shows the original plan. Scan again for a fresh preview.";
        }
        catch (Exception ex)
        {
            Status.Text = "Moving stopped";
            Detail.Text = "Some files may already have moved. Inspect the journal and scan again.";
            Issues.Text = ex.ToString();
            logFolder = Path.Combine(currentPlan.Root, ".flattener-logs");
            LogButton.IsVisible = Directory.Exists(logFolder);
            failure = ex.Message;
        }
        cancellation.Dispose(); cancellation = null; SetBusy(false);
        Footer.Text = "Existing files were never overwritten. Scan again to preview remaining files.";
        if (failure is not null)
            await MessageDialog.Show(this, failure + "\n\nSome files may already have moved. Inspect .flattener-logs before scanning again.", "Move interrupted");
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Detail.Text = "Stopping after the current filesystem operation…";
    }
    private async void Log_Click(object? sender, RoutedEventArgs e)
    {
        if (logFolder is null) return;
        try
        {
            if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(logFolder)))
                await MessageDialog.Show(this, $"No file manager could open {logFolder}.", "Cannot open log folder");
        }
        catch (Exception ex) { await MessageDialog.Show(this, ex.Message, "Cannot open log folder"); }
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!busy) return;
        e.Cancel = true;
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Detail.Text = "Stopping safely. Close the window once the operation has stopped.";
    }
}
