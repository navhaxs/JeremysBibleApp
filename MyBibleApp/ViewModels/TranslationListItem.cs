using MyBibleApp.Models;
using ReactiveUI;

namespace MyBibleApp.ViewModels;

public sealed class TranslationListItem : ReactiveObject
{
    private bool _isRenaming;
    private string _pendingName;

    public TranslationListItem(InstalledTranslation model)
    {
        Model = model;
        _pendingName = model.DisplayName;
    }

    public InstalledTranslation Model { get; }
    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRenaming, value);
            this.RaisePropertyChanged(nameof(IsNotRenaming));
        }
    }

    public bool IsNotRenaming => !_isRenaming;

    public string PendingName
    {
        get => _pendingName;
        set => this.RaiseAndSetIfChanged(ref _pendingName, value);
    }
}
