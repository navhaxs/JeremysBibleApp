using System;
using System.Collections.ObjectModel;

namespace MyBibleApp.ViewModels;

public class DebugPointerEventViewModel
{
    public string Timestamp { get; set; } = "";
    public string PointerType { get; set; } = "";
    public string Position { get; set; } = "";
    public string Pressure { get; set; } = "";
    public string Tilt { get; set; } = "";
    public string Twist { get; set; } = "";
    public string Properties { get; set; } = "";
}

public class DebugPointerViewModel : ViewModelBase
{
    private readonly ObservableCollection<DebugPointerEventViewModel> _events = [];

    public ObservableCollection<DebugPointerEventViewModel> Events => _events;

    public int EventCount => _events.Count;

    public void AddEvent(string pointerType, string position, string pressure, string tilt, string twist, string properties)
    {
        var evt = new DebugPointerEventViewModel
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            PointerType = pointerType,
            Position = position,
            Pressure = pressure,
            Tilt = tilt,
            Twist = twist,
            Properties = properties
        };

        _events.Insert(0, evt);

        // Keep only last 50 events to avoid memory bloat.
        while (_events.Count > 50)
        {
            _events.RemoveAt(_events.Count - 1);
        }
    }

    public void Clear()
    {
        _events.Clear();
    }
}

