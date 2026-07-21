adb disconnect
adb connect 192.168.0.234:36675
dotnet build MyBibleApp.Android\MyBibleApp.Android.csproj -t:Run -c Debug -f net10.0-android36.0