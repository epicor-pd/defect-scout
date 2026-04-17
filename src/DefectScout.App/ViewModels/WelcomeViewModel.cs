using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// First screen — shows setup instructions and provides buttons to load/import/create config.
/// No process launches; no prerequisite checking required.
/// </summary>
public sealed partial class WelcomeViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<WelcomeViewModel>();
    private readonly IConfigService _configService;
    private readonly DefectScoutConfig? _existingConfig;

    public override string PageTitle => "Welcome";

    /// <summary>Fired when the user opens or imports a config — navigates to the config editor.</summary>
    public event Action<DefectScoutConfig>? OpenWithConfig;

    /// <summary>Fired when the user wants to skip straight to testing (config already saved).</summary>
    public event Action? StartTesting;

    public event Action? NavigateToSetup;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True when the current app-local config has at least one enabled environment.</summary>
    public bool CanRunTests =>
        _existingConfig?.Environments.Any(e => e.Enabled) == true;

    public WelcomeViewModel(IConfigService configService, DefectScoutConfig? existingConfig = null)
    {
        _configService = configService;
        _existingConfig = existingConfig;
    }

    /// <summary>
    /// Load the app-local config and open it in the config editor.
    /// </summary>
    [RelayCommand]
    private async Task OpenConfigAsync()
    {
        _log.Debug("OpenConfig: loading app-local config");
        StatusMessage = string.Empty;
        var config = await _configService.LoadAsync();
        _log.Information("OpenConfig: loaded config with {Count} environments", config.Environments.Count);
        OpenWithConfig?.Invoke(config);
    }

    /// <summary>
    /// Skip to ticket input using the already-loaded config (only available when config has environments).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunTests))]
    private void RunTests()
    {
        _log.Debug("RunTests clicked");
        StartTesting?.Invoke();
    }

    /// <summary>
    /// Let the user pick an external defect-scout-config.json.
    /// Its contents are copied into the app-local config then opened in the editor.
    /// </summary>
    [RelayCommand]
    private async Task ImportConfigAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Defect Scout Config",
            AllowMultiple = false,
            FileTypeFilter = [new("JSON Config") { Patterns = ["*.json"] }],
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        try
        {
            var config = await _configService.ImportFromExternalAsync(path);
            _log.Information("ImportConfig: imported {File}, {Count} environments",
                Path.GetFileName(path), config.Environments.Count);
            StatusMessage = $"Imported from {Path.GetFileName(path)}";
            OpenWithConfig?.Invoke(config);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ImportConfig failed for {Path}", path);
            StatusMessage = $"Could not import config: {ex.Message}";
        }
    }

    /// <summary>Navigate to the setup/config editor to create a new config from scratch.</summary>
    [RelayCommand]
    private void CreateNewConfig()
    {
        _log.Debug("CreateNewConfig clicked");
        NavigateToSetup?.Invoke();
    }
}
