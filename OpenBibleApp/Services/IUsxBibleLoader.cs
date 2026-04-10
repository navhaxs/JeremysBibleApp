using OpenBibleApp.Models;

namespace OpenBibleApp.Services;

public interface IUsxBibleLoader
{
    BibleBook LoadFromAsset(string assetUri);
}

