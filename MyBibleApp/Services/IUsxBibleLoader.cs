using MyBibleApp.Models;

namespace MyBibleApp.Services;

public interface IUsxBibleLoader
{
    BibleBook LoadFromAsset(string assetUri);
}

