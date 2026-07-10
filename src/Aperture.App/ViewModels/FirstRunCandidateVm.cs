using Aperture.App.Mvvm;

namespace Aperture.App.ViewModels;

/// <summary>A detected photo folder offered on the first-run screen.</summary>
public sealed class FirstRunCandidateVm(string path, string alias) : ObservableObject
{
    private bool _isChosen = true;

    public string Path { get; } = path;
    public string Alias { get; } = alias;

    public bool IsChosen
    {
        get => _isChosen;
        set => SetProperty(ref _isChosen, value);
    }
}
