using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Per-item wrapper used by the StepReviewView DataTemplate.
/// Holds observable copies of the step properties plus direct references to the
/// parent commands, so bindings never need to traverse the visual tree.
/// </summary>
public sealed partial class StepItemViewModel : ObservableObject
{
    internal TestStep Step { get; }

    [ObservableProperty] private int _stepNumber;
    [ObservableProperty] private string _action = string.Empty;
    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private string? _value;
    [ObservableProperty] private string _expected = string.Empty;
    [ObservableProperty] private bool _isDiscriminatingStep;

    public IRelayCommand<StepItemViewModel> EditStepCommand { get; }
    public IRelayCommand<StepItemViewModel> MoveStepUpCommand { get; }
    public IRelayCommand<StepItemViewModel> MoveStepDownCommand { get; }
    public IRelayCommand<StepItemViewModel> DeleteStepCommand { get; }

    public StepItemViewModel(TestStep step, StepReviewViewModel owner)
    {
        Step = step;
        _stepNumber = step.StepNumber;
        _action = step.Action;
        _target = step.Target;
        _value = step.Value;
        _expected = step.Expected;
        _isDiscriminatingStep = step.IsDiscriminatingStep;

        EditStepCommand = owner.EditStepCommand;
        MoveStepUpCommand = owner.MoveStepUpCommand;
        MoveStepDownCommand = owner.MoveStepDownCommand;
        DeleteStepCommand = owner.DeleteStepCommand;
    }

    /// <summary>Applies saved edits to both the observable properties and the backing TestStep.</summary>
    internal void ApplyEdits(string action, string target, string? value, string expected, bool isDiscriminating)
    {
        Action = action;                    Step.Action = action;
        Target = target;                    Step.Target = target;
        Value = value;                      Step.Value = value;
        Expected = expected;               Step.Expected = expected;
        IsDiscriminatingStep = isDiscriminating; Step.IsDiscriminatingStep = isDiscriminating;
    }
}
