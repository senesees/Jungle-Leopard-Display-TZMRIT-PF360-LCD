using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using JLDisplayManager.Models;
using JLDisplayManager.Services.Ai;

using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager.Views;

/// <summary>
/// All the AI pipeline's configuration and control, in its own window.
///
/// Kept out of SettingsWindow deliberately: that window is about the panel and
/// how pictures reach it, and this is five sections about where pictures come
/// from. Every leg — SwarmUI, the enhancer, one whole generation — has its own
/// Test button, so a failure can be isolated to one hop rather than guessed at.
/// </summary>
public partial class AiWindow : Window
{
    private readonly App _app = App.Current;
    private readonly AiSettings _ai;
    private readonly ObservableCollection<PromptSeed> _prompts = new();

    /// <summary>Cancels whatever test or manual generation is in flight.</summary>
    private CancellationTokenSource? _work;

    private bool _loaded;

    public AiWindow()
    {
        InitializeComponent();

        _ai = _app.Ai;
        PromptList.ItemsSource = _prompts;

        Load();
        _loaded = true;

        _app.Pipeline.PropertyChanged += OnPipelineChanged;
        UpdatePipelineStatus();
    }

    // -----------------------------------------------------------------------
    // Load and save
    // -----------------------------------------------------------------------

    private void Load()
    {
        SwarmUrlBox.Text = _ai.SwarmUrl;
        ModelBox.Text = _ai.Model;
        WidthBox.Text = _ai.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _ai.Height.ToString(CultureInfo.InvariantCulture);
        StepsBox.Text = _ai.Steps.ToString(CultureInfo.InvariantCulture);
        CfgBox.Text = _ai.CfgScale.ToString(CultureInfo.InvariantCulture);
        SamplerBox.Text = _ai.Sampler;
        SchedulerBox.Text = _ai.Scheduler;
        NegativeBox.Text = _ai.NegativePrompt;
        GenTimeoutBox.Text = _ai.GenerateTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        DoNotSaveBox.IsChecked = _ai.DoNotSaveOnServer;
        ExtraParamsBox.Text = _ai.ExtraParamsJson;

        // A stored key is never read back into the box — only whether one is
        // set. Leaving it blank on save keeps it; that is what the hint says.
        SwarmTokenBox.Password = "";

        ProviderBox.SelectedIndex = (int)_ai.Provider;
        SystemPromptBox.Text = _ai.SystemPrompt;
        TemperatureBox.Text = _ai.Temperature.ToString(CultureInfo.InvariantCulture);
        MaxTokensBox.Text = _ai.MaxTokens.ToString(CultureInfo.InvariantCulture);
        LlmTimeoutBox.Text = _ai.LlmTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        RequireEnhancementBox.IsChecked = _ai.RequireEnhancement;

        LoadEndpointFields();

        _prompts.Clear();
        foreach (var prompt in _ai.Prompts) _prompts.Add(prompt);
        if (_prompts.Count == 0) AddStarterPrompts();

        OrderBox.SelectedIndex = (int)_ai.Order;

        DwellBox.Text = _ai.DwellSeconds.ToString(CultureInfo.InvariantCulture);
        GenEveryBox.Text = _ai.GenerateEverySeconds.ToString(CultureInfo.InvariantCulture);
        BufferBox.Text = _ai.BufferSize.ToString(CultureInfo.InvariantCulture);
        StartWithAppBox.IsChecked = _ai.StartWithApp;

        UpdateTimingHint();

        RetentionCountBox.Text = _ai.RetentionCount.ToString(CultureInfo.InvariantCulture);
        PruneByAgeBox.IsChecked = _ai.PruneByAge;
        RetentionDaysBox.Text = _ai.RetentionDays.ToString(CultureInfo.InvariantCulture);

        UpdateProviderUi();
    }

    /// <summary>
    /// A few examples on first run. An empty prompt list means the Start button
    /// can only refuse, and a blank box says nothing about what belongs in it.
    /// </summary>
    private void AddStarterPrompts()
    {
        foreach (string text in new[]
        {
            "a bioluminescent jungle at night",
            "an abandoned space station orbiting a gas giant",
            "a rain-slick neon street in a cyberpunk city",
        })
        {
            _prompts.Add(new PromptSeed { Text = text });
        }
    }

    /// <summary>Fills the address, model and key boxes for the chosen provider.</summary>
    private void LoadEndpointFields()
    {
        var endpoint = _ai.CurrentEndpoint;

        LlmUrlBox.Text = endpoint.BaseUrl;
        LlmModelBox.Text = endpoint.Model;
        LlmKeyBox.Password = "";

        KeyHint.Text = endpoint.HasApiKey
            ? "A key is saved. Leave this blank to keep it, or type a new one to replace it."
            : _ai.Provider == LlmProvider.Anthropic
                ? "Required. Stored encrypted for your Windows account only."
                : "Usually left empty for a local model. Stored encrypted for your Windows account only.";

        // Claude rejects temperature outright from Opus 4.6 onwards, so the box
        // would be a lie on that path rather than merely unused.
        bool anthropic = _ai.Provider == LlmProvider.Anthropic;
        TemperatureBox.IsEnabled = !anthropic;
        TemperatureHint.Text = anthropic
            ? "Claude does not accept a temperature; only the token limit applies."
            : "";
    }

    private void Save()
    {
        _ai.SwarmUrl = SwarmUrlBox.Text;
        _ai.Model = ModelBox.Text.Trim();
        _ai.Width = ParseInt(WidthBox.Text, _ai.Width);
        _ai.Height = ParseInt(HeightBox.Text, _ai.Height);
        _ai.Steps = ParseInt(StepsBox.Text, _ai.Steps);
        _ai.CfgScale = ParseDouble(CfgBox.Text, _ai.CfgScale);
        _ai.Sampler = SamplerBox.Text.Trim();
        _ai.Scheduler = SchedulerBox.Text.Trim();
        _ai.NegativePrompt = NegativeBox.Text.Trim();
        _ai.GenerateTimeoutSeconds = ParseInt(GenTimeoutBox.Text, _ai.GenerateTimeoutSeconds);
        _ai.DoNotSaveOnServer = DoNotSaveBox.IsChecked == true;
        _ai.ExtraParamsJson = ExtraParamsBox.Text.Trim();

        if (SwarmTokenBox.Password.Length > 0) _ai.SwarmToken = SwarmTokenBox.Password;

        _ai.Provider = (LlmProvider)Math.Max(0, ProviderBox.SelectedIndex);
        _ai.SystemPrompt = SystemPromptBox.Text;
        _ai.Temperature = ParseDouble(TemperatureBox.Text, _ai.Temperature);
        _ai.MaxTokens = ParseInt(MaxTokensBox.Text, _ai.MaxTokens);
        _ai.LlmTimeoutSeconds = ParseInt(LlmTimeoutBox.Text, _ai.LlmTimeoutSeconds);
        _ai.RequireEnhancement = RequireEnhancementBox.IsChecked == true;

        var endpoint = _ai.CurrentEndpoint;
        endpoint.BaseUrl = LlmUrlBox.Text;
        endpoint.Model = LlmModelBox.Text;
        if (LlmKeyBox.Password.Length > 0) endpoint.ApiKey = LlmKeyBox.Password;

        _ai.Prompts = _prompts.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
        _ai.Order = (PromptOrder)Math.Max(0, OrderBox.SelectedIndex);

        _ai.DwellSeconds = ParseInt(DwellBox.Text, _ai.DwellSeconds);
        _ai.GenerateEverySeconds = ParseInt(GenEveryBox.Text, _ai.GenerateEverySeconds);
        _ai.BufferSize = ParseInt(BufferBox.Text, _ai.BufferSize);
        _ai.StartWithApp = StartWithAppBox.IsChecked == true;

        _ai.RetentionCount = ParseInt(RetentionCountBox.Text, _ai.RetentionCount);
        _ai.PruneByAge = PruneByAgeBox.IsChecked == true;
        _ai.RetentionDays = ParseInt(RetentionDaysBox.Text, _ai.RetentionDays);

        Storage.SaveAi(_ai);

        // Addresses and keys may have moved; drop the cached clients so the
        // next call builds them from what was just saved.
        _app.Pipeline.SettingsChanged();
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : fallback;

    // -----------------------------------------------------------------------
    // Provider
    // -----------------------------------------------------------------------

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        // Write the boxes back to the provider that is on its way out, so its
        // settings survive the switch, then load the new one's.
        var outgoing = _ai.CurrentEndpoint;
        outgoing.BaseUrl = LlmUrlBox.Text;
        outgoing.Model = LlmModelBox.Text;
        if (LlmKeyBox.Password.Length > 0) outgoing.ApiKey = LlmKeyBox.Password;

        _ai.Provider = (LlmProvider)Math.Max(0, ProviderBox.SelectedIndex);

        LoadEndpointFields();
        UpdateProviderUi();
    }

    private void UpdateProviderUi()
    {
        LlmPanel.Visibility = _ai.Provider == LlmProvider.None
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnResetSystemPrompt(object sender, RoutedEventArgs e) =>
        SystemPromptBox.Text = AiSettings.DefaultSystemPrompt;

    private void OnTimingChanged(object sender, TextChangedEventArgs e) => UpdateTimingHint();

    /// <summary>
    /// Spells out what the two intervals do together. A floor longer than the
    /// dwell is legal and is honoured, but it means the panel holds images past
    /// their turn — which looks like a fault unless it is said out loud.
    /// </summary>
    private void UpdateTimingHint()
    {
        if (TimingHint is null) return;

        int floor = ParseInt(GenEveryBox.Text, 0);
        int dwell = ParseInt(DwellBox.Text, _ai.DwellSeconds);

        TimingHint.Text = floor <= 0
            ? "Off: images are generated as fast as the backend manages, until the buffer is full."
            : floor > dwell
                ? $"Longer than the {dwell}s dwell, so the panel will hold each image until the next " +
                  "one is due. The status line counts down to it."
                : $"At most one every {floor}s, which keeps up with the {dwell}s dwell.";
    }

    // -----------------------------------------------------------------------
    // Prompts
    // -----------------------------------------------------------------------

    private void OnAddPrompt(object sender, RoutedEventArgs e) => AddPrompt(_prompts.Count);

    /// <summary>Adds a blank prompt at a position and puts the caret in it.</summary>
    private void AddPrompt(int index)
    {
        var seed = new PromptSeed();
        _prompts.Insert(Math.Clamp(index, 0, _prompts.Count), seed);
        FocusPrompt(seed);
    }

    private void OnDeletePrompt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PromptSeed seed) return;

        int index = _prompts.IndexOf(seed);
        _prompts.Remove(seed);

        // Land the caret on the neighbour, so clearing several in a row does
        // not mean reaching for the mouse between each one.
        if (_prompts.Count > 0)
            FocusPrompt(_prompts[Math.Clamp(index, 0, _prompts.Count - 1)]);
    }

    /// <summary>
    /// Enter starts the next prompt rather than doing nothing. These are a list
    /// of one-liners, so typing straight down the list is the common case.
    /// </summary>
    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((sender as FrameworkElement)?.DataContext is not PromptSeed seed) return;

        e.Handled = true;
        AddPrompt(_prompts.IndexOf(seed) + 1);
    }

    /// <summary>
    /// Focuses a row's text box once WPF has built the container for it. The
    /// item is added first and the container appears a layout pass later, so
    /// this has to run after that rather than inline.
    /// </summary>
    private void FocusPrompt(PromptSeed seed)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PromptList.UpdateLayout();

            if (PromptList.ItemContainerGenerator.ContainerFromItem(seed) is not DependencyObject c)
                return;

            if (FindChild<TextBox>(c) is not { } box) return;

            box.Focus();
            box.CaretIndex = box.Text.Length;
            box.BringIntoView();
        }), DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    private async void OnTestSwarm(object sender, RoutedEventArgs e)
    {
        Save();
        SwarmStatus.Text = "connecting…";

        try
        {
            var ct = StartWork();
            var probe = await _app.Pipeline.Swarm.ProbeAsync(ct);

            // Keep what the user picked if the server still offers it; a probe
            // should not silently change the model being generated with.
            string wanted = ModelBox.Text.Trim();
            ModelBox.ItemsSource = probe.Models;
            ModelBox.Text = wanted.Length > 0 ? wanted : probe.Models.FirstOrDefault() ?? "";

            SamplerBox.ItemsSource = probe.Samplers;
            SchedulerBox.ItemsSource = probe.Schedulers;

            SwarmStatus.Text =
                $"SwarmUI {probe.Version} · {probe.Models.Count} model(s)" +
                (probe.BackendCount > 0 ? $" · {probe.BackendCount} backend(s)" : "");

            if (probe.Models.Count == 0)
                SwarmStatus.Text += "  — no checkpoints found; check the server's model folder.";
        }
        catch (OperationCanceledException)
        {
            SwarmStatus.Text = "cancelled";
        }
        catch (Exception ex)
        {
            SwarmStatus.Text = ex.Message;
        }
    }

    private async void OnTestLlm(object sender, RoutedEventArgs e)
    {
        Save();

        string? seed = _prompts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text;
        if (seed is null)
        {
            LlmStatus.Text = "add a prompt first";
            return;
        }

        LlmStatus.Text = "asking the model…";

        try
        {
            var ct = StartWork();

            // Straight at the client, not through PromptEnhancer: its fallback
            // would quietly hand the seed back and report success, which is the
            // opposite of what a test button is for.
            var client = _app.Pipeline.Enhancer.GetClient();
            string raw = await client.EnhanceAsync(_ai.SystemPrompt, seed.Trim(), ct);

            ShowTestOutput($"“{seed.Trim()}”  →  {PromptEnhancer.Clean(raw)}");
            LlmStatus.Text = "the enhancer is working.";

            // Populating this after a success means the list reflects an
            // endpoint that actually answered.
            var models = await client.ListModelsAsync(ct);
            if (models.Count > 0)
            {
                string wanted = LlmModelBox.Text.Trim();
                LlmModelBox.ItemsSource = models;
                LlmModelBox.Text = wanted;
            }
        }
        catch (OperationCanceledException)
        {
            LlmStatus.Text = "cancelled";
        }
        catch (Exception ex)
        {
            LlmStatus.Text = ex.Message;
        }
    }

    private async void OnGenerateNow(object sender, RoutedEventArgs e)
    {
        Save();

        GenerateNowButton.IsEnabled = false;
        ShowTestOutput("generating…");

        try
        {
            var ct = StartWork();
            var item = await _app.Pipeline.GenerateOnceAsync(ct);

            ShowTestOutput(item is null
                ? "Generation failed: " + _app.Pipeline.LastError
                : $"{item.Name}  —  {item.EnhancedPrompt}");
        }
        catch (OperationCanceledException)
        {
            ShowTestOutput("cancelled");
        }
        catch (Exception ex)
        {
            ShowTestOutput(ex.Message);
        }
        finally
        {
            GenerateNowButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// One test at a time: starting a second cancels the first, so a slow probe
    /// cannot land after the settings it used have been edited.
    ///
    /// The source is owned here and disposed by whatever supersedes it — never
    /// by the caller. A caller that disposed its own would leave _work pointing
    /// at a dead object, and the next Cancel would throw.
    /// </summary>
    private CancellationToken StartWork()
    {
        CancelWork();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    /// <summary>Cancels and releases whatever is in flight. Safe to call twice.</summary>
    private void CancelWork()
    {
        var previous = _work;
        _work = null;
        if (previous is null) return;

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone; nothing left to stop.
        }

        previous.Dispose();
    }

    private void ShowTestOutput(string text)
    {
        TestOutput.Text = text;
        TestOutput.Visibility = Visibility.Visible;
    }

    // -----------------------------------------------------------------------
    // Run control
    // -----------------------------------------------------------------------

    private void OnToggleRun(object sender, RoutedEventArgs e)
    {
        Save();

        if (_app.Pipeline.Running) _app.Pipeline.Stop();
        else _app.StartAi();

        UpdatePipelineStatus();
    }

    private void OnPipelineChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(UpdatePipelineStatus);

    private void UpdatePipelineStatus()
    {
        var pipeline = _app.Pipeline;

        RunButton.Content = pipeline.Running ? "Stop" : "Start";

        PipelineStatus.Text = pipeline.Running
            ? pipeline.Current is null
                ? "Running — " + (pipeline.Status.Length > 0 ? pipeline.Status : "starting…")
                : "Showing " + pipeline.Current.Name
            : "Stopped";

        string detail = $"{pipeline.Generated} generated this session · {pipeline.QueueDepth} ready";
        if (pipeline.Failures > 0) detail += $" · {pipeline.Failures} failed";
        if (pipeline.HasError) detail += " · " + pipeline.LastError;
        PipelineDetail.Text = detail;
    }

    // -----------------------------------------------------------------------

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // An in-flight test holds settings this window is about to stop owning.
        // This runs on the close path, where an exception would take the whole
        // tray app down rather than merely failing a test.
        try
        {
            CancelWork();
            _app.Pipeline.PropertyChanged -= OnPipelineChanged;
            Save();
        }
        catch (Exception ex)
        {
            Storage.Log("ai window: closing failed: " + ex);
        }

        base.OnClosing(e);
    }
}
