using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace MediaFlattener;

public partial class MainWindow : Window
{
    private ScanPlan? plan;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private string? logFolder;

    public MainWindow() { InitializeComponent(); }
    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (ContinueButton is null) return;
        bool vr = SortVrVideos.IsChecked == true;
        VrCard.Visibility = VrFilter.Visibility = vr ? Visibility.Visible : Visibility.Collapsed;
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
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the root folder to flatten", Multiselect = false };
        if (Directory.Exists(RootPath.Text)) dialog.InitialDirectory = RootPath.Text;
        if (dialog.ShowDialog(this) == true) RootPath.Text = dialog.FolderName;
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
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        int limit = 1000;
        if (DateFolders.IsChecked == true && (!int.TryParse(FileLimit.Text, out limit) || limit < 1 || limit > 1_000_000))
        { MessageBox.Show(this, "Enter a file limit between 1 and 1,000,000.", "Check folder limit", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (string.IsNullOrWhiteSpace(RootPath.Text))
        { MessageBox.Show(this, "Choose a root folder first.", "Choose a folder"); return; }
        var options = new ScanOptions(DateFolders.IsChecked == true, limit, DeleteEmptyFolders.IsChecked == true, SortVrVideos.IsChecked == true);
        string root = RootPath.Text.Trim();
        plan = null;
        PreviewGrid.ItemsSource = null;
        SetCounts([]);
        LogButton.Visibility = Visibility.Collapsed;
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
            Issues.Text = plan.Warnings.Count == 0 ? "No scan problems found. Existing image, video, other, vr-video, and .flattener-logs folders are excluded. Links and junctions are not followed." : string.Join(Environment.NewLine, plan.Warnings);
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
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ProgressBar.IsIndeterminate = false; cancellation.Dispose(); cancellation = null; SetBusy(false); }
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
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (PreviewGrid is not null) ApplyFilter(); }
    private void ApplyFilter()
    {
        if (plan is null) return;
        var view = new ListCollectionView(plan.Items);
        string selected = (CategoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All files";
        if (selected != "All files") view.Filter = item => ((MoveItem)item).Category == selected;
        PreviewGrid.ItemsSource = view;
    }
    private async void Continue_Click(object sender, RoutedEventArgs e)
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
        try
        {
            var result = await Task.Run(() => Engine.Execute(currentPlan, progress, cancellation.Token));
            logFolder = Path.GetDirectoryName(result.LogPath);
            LogButton.Visibility = Visibility.Visible;
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
            LogButton.Visibility = Directory.Exists(logFolder) ? Visibility.Visible : Visibility.Collapsed;
            MessageBox.Show(this, ex.Message + "\n\nSome files may already have moved. Inspect .flattener-logs before scanning again.", "Move interrupted", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); Footer.Text = "Existing files were never overwritten. Scan again to preview remaining files."; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Detail.Text = "Stopping after the current filesystem operation…";
    }
    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (logFolder is null) return;
        try { Process.Start(new ProcessStartInfo(logFolder) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open log folder"); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true;
        cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Detail.Text = "Stopping safely. Close the window once the operation has stopped.";
    }
}
