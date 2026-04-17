using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Screen 4 — user reviews / edits extracted steps before running.
/// </summary>
public sealed partial class StepReviewViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<StepReviewViewModel>();
    private readonly StructuredTestPlan _originalPlan;

    public override string PageTitle => "Review Steps";

    public event Action? Back;
    public event Action<StructuredTestPlan>? Confirmed;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _affectedModule = string.Empty;

    [ObservableProperty]
    private StepItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isEditing;

    // Editing temporaries bound to editor panel
    [ObservableProperty] private string _editAction = string.Empty;
    [ObservableProperty] private string _editTarget = string.Empty;
    [ObservableProperty] private string _editValue = string.Empty;
    [ObservableProperty] private string _editExpected = string.Empty;
    [ObservableProperty] private bool _editIsDiscriminating;

    public ObservableCollection<StepItemViewModel> Steps { get; } = [];

    public static IReadOnlyList<string> ActionTypes =>
        ["navigate", "click", "fill", "select", "verify", "wait", "screenshot", "api-call"];

    public StepReviewViewModel(StructuredTestPlan plan)
    {
        _originalPlan = plan;
        Summary = plan.Summary;
        AffectedModule = plan.AffectedModule;
        foreach (var s in plan.Steps) Steps.Add(new StepItemViewModel(s, this));
    }

    partial void OnSelectedItemChanged(StepItemViewModel? value)
    {
        if (value is null)
        {
            IsEditing = false;
            return;
        }
        EditAction = value.Action;
        EditTarget = value.Target;
        EditValue = value.Value ?? string.Empty;
        EditExpected = value.Expected;
        EditIsDiscriminating = value.IsDiscriminatingStep;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveStepEdit()
    {
        if (SelectedItem is null) return;
        _log.Debug("SaveStepEdit: step {Num} action={Action} target={Target}",
            SelectedItem.StepNumber, EditAction, EditTarget);
        SelectedItem.ApplyEdits(
            EditAction, EditTarget,
            string.IsNullOrWhiteSpace(EditValue) ? null : EditValue,
            EditExpected, EditIsDiscriminating);
        IsEditing = false;
        SelectedItem = null;
    }

    [RelayCommand]
    private void CancelStepEdit()
    {
        IsEditing = false;
        SelectedItem = null;
    }

    [RelayCommand]
    private void MoveStepUp(StepItemViewModel item)
    {
        var idx = Steps.IndexOf(item);
        if (idx > 0) Steps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveStepDown(StepItemViewModel item)
    {
        var idx = Steps.IndexOf(item);
        if (idx < Steps.Count - 1) Steps.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void DeleteStep(StepItemViewModel item) => Steps.Remove(item);

    [RelayCommand]
    private void AddStep()
    {
        var step = new TestStep { Action = "verify", Target = "New step", StepNumber = Steps.Count + 1 };
        var item = new StepItemViewModel(step, this);
        _log.Debug("AddStep: new step {Num}", step.StepNumber);
        Steps.Add(item);
        SelectedItem = item;
    }

    [RelayCommand]
    private void Confirm()
    {
        _originalPlan.Steps = [.. Steps.Select(i => i.Step)];
        _originalPlan.Summary = Summary;
        _originalPlan.AffectedModule = AffectedModule;
        _log.Information("StepReview confirmed: ticket={Ticket}, steps={Count}",
            _originalPlan.Ticket, _originalPlan.Steps.Count);
        Confirmed?.Invoke(_originalPlan);
    }

    [RelayCommand]
    private void EditStep(StepItemViewModel item) => SelectedItem = item;

    [RelayCommand]
    private void GoBack()
    {
        _log.Debug("StepReviewViewModel: GoBack");
        Back?.Invoke();
    }
}

