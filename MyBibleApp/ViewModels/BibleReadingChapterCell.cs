using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class BibleReadingChapterCell : ViewModelBase
{
    private bool _isRead;

    public int Number { get; }

    public BibleReadingChapterCell(int number, bool isRead = false)
    {
        Number = number;
        _isRead = isRead;
    }

    public bool IsRead
    {
        get => _isRead;
        set => this.RaiseAndSetIfChanged(ref _isRead, value);
    }
}
