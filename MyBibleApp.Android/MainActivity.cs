using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Android;

[Activity(
    Label = "MyBibleApp.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ResizeableActivity = true,
    // SingleTask ensures OnNewIntent is called (not a second instance) when the
    // browser redirects back via the custom URI scheme.
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation |
                           ConfigChanges.ScreenSize |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.Density |
                           ConfigChanges.UiMode)]
// The DataScheme is the reversed client ID of your Android OAuth credential.
// Must match what AndroidGoogleDriveAuthService.GetRedirectUri() produces at runtime.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "com.googleusercontent.apps.346711458495-tmfikb3tqufvut0ueji94ekhh5rqs3e5",
    DataPath = "/")]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Register the URI launcher so AndroidGoogleDriveAuthService can open
        // the Google consent page using an Android ACTION_VIEW Intent.
        AndroidOAuthCallbackBridge.LaunchUri = uri =>
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
            intent.AddFlags(ActivityFlags.NewTask);
            ApplicationContext!.StartActivity(intent);
            return System.Threading.Tasks.Task.CompletedTask;
        };

        // Handle the case where the app was cold-started via the OAuth redirect.
        if (Intent?.Data != null)
            AndroidOAuthCallbackBridge.TryHandleCallback(Intent.Data.ToString()!);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        // Called when the browser redirects back to the app via the custom scheme.
        // Example: com.companyname.mybibleapp:/oauth2redirect?code=4/0A...
        if (intent?.Data != null)
            AndroidOAuthCallbackBridge.TryHandleCallback(intent.Data.ToString()!);
    }
}