using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace MyBibleApp.Android;

[Activity(
    Label = "MyBibleApp.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ResizeableActivity = true,
    ConfigurationChanges = ConfigChanges.Orientation |
                           ConfigChanges.ScreenSize |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.Density |
                           ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}