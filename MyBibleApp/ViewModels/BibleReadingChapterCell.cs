using ReactiveUI;

namespace MyBibleApp.ViewModels;

public class BibleReadingChapterCell : ViewModelBase
{
    private bool _isRead;
    private bool _isCurrentChapter;

    public string BookCode { get; }
    public int Number { get; }

    public BibleReadingChapterCell(string bookCode, int number, bool isRead = false)
    {
        BookCode = bookCode;
        Number = number;
        _isRead = isRead;
    }

    public bool IsRead
    {
        get => _isRead;
        set => this.RaiseAndSetIfChanged(ref _isRead, value);
    }

    public bool IsCurrentChapter
    {
        get => _isCurrentChapter;
        set => this.RaiseAndSetIfChanged(ref _isCurrentChapter, value);
    }
}
