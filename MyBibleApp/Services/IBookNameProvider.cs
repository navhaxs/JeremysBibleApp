namespace MyBibleApp.Services;

public interface IBookNameProvider
{
    string GetEnglishName(string bookCode);
}
