using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using JLDisplayManager.Models;
using JLDisplayManager.Services;

namespace JLDisplayManager.Views;

public partial class YoutubeDialog : Window
{
    private CancellationTokenSource? _download;

    /// <summary>
    /// What yt-dlp last said it was doing, kept so the percentage can be shown
    /// against it rather than replacing it.
    /// </summary>
    private string _stage = "";

    /// <summary>The downloaded file, when the download finished successfully.</summary>
    public string? DownloadedPath { get; private set; }

    /// <summary>True when yt-dlp could not be found, so a missing binary shows.</summary>
    public bool YtDlpMissing { get; private set; }

    public YoutubeDialog()
    {
        InitializeComponent();
        _ = RefreshYtDlpStatusAsync();
    }

    /// <summary>
    /// Probing runs yt-dlp once for its version, and the bundled copy is a
    /// packed Python program that takes the better part of a second to unpack.
    /// Off the UI thread, so the dialog is on screen and typeable immediately
    /// rather than appearing already frozen.
    /// </summary>
    private async Task RefreshYtDlpStatusAsync()
    {
        HintText.Text = "Looking for yt-dlp…";
        DownloadButton.IsEnabled = false;

        string? tool = await Task.Run(YoutubeService.FindYtDlp);

        if (tool is null)
        {
            YtDlpMissing = true;
            HintText.Text = "yt-dlp was not found. Install it with:\n" +
                "    pip install -U yt-dlp\n" +
                "or put the standalone yt-dlp.exe on your PATH.\n" +
                "It is a free, single-file program.";
        }
        else
        {
            YtDlpMissing = false;
            HintText.Text = $"Using yt-dlp: {tool}";
        }

        DownloadButton.IsEnabled = true;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) return;

        DownloadButton.IsEnabled = false;
        UrlBox.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Value = 0;
        _stage = "Starting…";
        ProgressText.Text = _stage;

        _download = new CancellationTokenSource();
        CancellationToken token = _download.Token;

        try
        {
            // yt-dlp is a child process we wait on, which would otherwise block
            // the message pump for the whole download. Both callbacks arrive on
            // the pipe reader's thread, so they are marshalled back before they
            // touch a control.
            string path = await Task.Run(() => YoutubeService.Download(
                url,
                stage => Dispatcher.InvokeAsync(() => ShowStage(stage)),
                pct => Dispatcher.InvokeAsync(() => ShowProgress(pct)),
                token), token);

            DownloadedPath = path;

            // The real DialogResult, not a shadow of it: this is what
            // ShowDialog returns, and setting it closes the window.
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            // yt-dlp says why far better than we can guess, so its own words go
            // straight to the user.
            Storage.Log($"youtube download failed: {ex.Message}");
            ProgressText.Text = ex.Message;
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            UrlBox.IsEnabled = true;
            CancelButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Collapsed;
            // Nothing is running any more, so a bar that keeps sweeping would
            // say otherwise. The text stays: it is carrying the reason.
            ProgressBar.Visibility = Visibility.Collapsed;
            _download?.Dispose();
            _download = null;
        }
    }

    private void ShowStage(string stage)
    {
        _stage = stage;

        // Merging has no percentage of its own, and leaving the bar full from
        // the download that preceded it would say the work is done when it is
        // not.
        if (stage.StartsWith("Merging", StringComparison.Ordinal) ||
            stage.StartsWith("Converting", StringComparison.Ordinal))
        {
            ProgressBar.IsIndeterminate = true;
        }

        ProgressText.Text = stage;
    }

    private void ShowProgress(double pct)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = pct;

        // A video and its audio are fetched as two streams, so the percentage
        // runs to a hundred twice. The stage beside it is what stops that
        // reading as a stall.
        ProgressText.Text = $"{_stage} {pct:0.0}%";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ProgressBar.IsIndeterminate = true;
        ProgressText.Text = "Cancelling…";
        _download?.Cancel();
    }

    /// <summary>
    /// Closing the window with a download in flight kills yt-dlp with it.
    /// Without this the process would carry on downloading with nothing left to
    /// show for it.
    /// </summary>
    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e) =>
        _download?.Cancel();
}
