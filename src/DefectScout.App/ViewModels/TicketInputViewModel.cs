using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Screen 3 — enter ERPS ticket text or paste raw description; kick off step extraction.
/// </summary>
public sealed partial class TicketInputViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<TicketInputViewModel>();
    private readonly IStepExtractorService _stepExtractor;
    private readonly DefectScoutConfig _config;

    public override string PageTitle => "Ticket Input";

    public event Action? Back;
    public event Action<StructuredTestPlan>? StepsExtracted;

    [ObservableProperty]
    private string _ticketText = string.Empty;

    [ObservableProperty]
    private string? _ticketFilePath;

    [ObservableProperty]
    private bool _isExtracting;

    [ObservableProperty]
    private string _extractionLog = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public int EnabledEnvironmentCount => _config.Environments.Count(e => e.Enabled);

    public string RuntimeLabel => _config.AgentRuntime.IsLocalOllama
        ? $"Local Ollama ({_config.AgentRuntime.StepExtractorModel})"
        : "GitHub Copilot SDK";

    public string ExtractionStatusText => _config.AgentRuntime.IsLocalOllama
        ? "Local agent is extracting steps..."
        : "Copilot is extracting steps...";

    public string ExtractionButtonText => _config.AgentRuntime.IsLocalOllama
        ? "Extract Steps with Local Agent"
        : "Extract Steps with Copilot";

    public TicketInputViewModel(IStepExtractorService stepExtractor, DefectScoutConfig config)
    {
        _stepExtractor = stepExtractor;
        _config = config;
    }

    [RelayCommand]
    private async Task BrowseTicketFileAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Ticket File",
            AllowMultiple = false,
            FileTypeFilter = [
                new("Ticket files") { Patterns = ["*.txt", "*.xml", "*.md", "*.json"] },
                new("All files") { Patterns = ["*"] }
            ],
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        _log.Debug("Ticket file selected: {Path}", path);
        TicketFilePath = path;
    }

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractStepsAsync()
    {
        IsExtracting = true;
        ErrorMessage = null;
        ExtractionLog = string.Empty;

        var progress = new Progress<string>(msg =>
        {
            ExtractionLog += msg;
        });

        _log.Information("ExtractSteps started: hasFile={HasFile}, hasText={HasText}",
            !string.IsNullOrWhiteSpace(TicketFilePath),
            !string.IsNullOrWhiteSpace(TicketText));
        try
        {
            var filePath    = string.IsNullOrWhiteSpace(TicketFilePath) ? null : TicketFilePath;
            var customSteps = TicketText?.Trim() ?? string.Empty;

            var plan = await _stepExtractor.ExtractAsync(customSteps, filePath, progress, _config);
            _log.Information("ExtractSteps succeeded: ticket={Ticket}, steps={Count}",
                plan.Ticket, plan.Steps?.Count ?? 0);
            StepsExtracted?.Invoke(plan);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ExtractSteps failed");
            ErrorMessage = $"Extraction failed: {ex.Message}";
        }
        finally
        {
            IsExtracting = false;
        }
    }

    private bool CanExtract() => !IsExtracting &&
        (!string.IsNullOrWhiteSpace(TicketText) || !string.IsNullOrWhiteSpace(TicketFilePath));

    partial void OnTicketTextChanged(string value) => ExtractStepsCommand.NotifyCanExecuteChanged();
    partial void OnTicketFilePathChanged(string? value) => ExtractStepsCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void GoBack()
    {
        _log.Debug("TicketInputViewModel: GoBack");
        Back?.Invoke();
    }
}
