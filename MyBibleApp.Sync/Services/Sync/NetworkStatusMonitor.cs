using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Delegate for network status changed events
/// </summary>
public delegate void NetworkStatusChangedEventHandler(bool isConnected);

/// <summary>
/// Interface for monitoring network connectivity
/// </summary>
public interface INetworkStatusMonitor
{
    /// <summary>
    /// Gets whether the device is currently connected to a network
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Event raised when network connectivity changes
    /// </summary>
    event NetworkStatusChangedEventHandler? ConnectivityChanged;

    /// <summary>
    /// Starts monitoring network status
    /// </summary>
    void StartMonitoring();

    /// <summary>
    /// Stops monitoring network status
    /// </summary>
    void StopMonitoring();
}

/// <summary>
/// Cross-platform implementation of network status monitoring
/// </summary>
public class NetworkStatusMonitor : INetworkStatusMonitor
{
    private bool _isConnected;
    private bool _isMonitoring;

    public bool IsConnected => _isConnected;

    public event NetworkStatusChangedEventHandler? ConnectivityChanged;

    public NetworkStatusMonitor()
    {
        _isConnected = CheckConnectivity();
    }

    public void StartMonitoring()
    {
        if (_isMonitoring)
            return;

        _isMonitoring = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring)
            return;

        _isMonitoring = false;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        var wasConnected = _isConnected;
        _isConnected = e.IsAvailable;

        if (wasConnected != _isConnected)
        {
            ConnectivityChanged?.Invoke(_isConnected);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var wasConnected = _isConnected;
        _isConnected = CheckConnectivity();

        if (wasConnected != _isConnected)
        {
            ConnectivityChanged?.Invoke(_isConnected);
        }
    }

    private static bool CheckConnectivity()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                if (ni.OperationalStatus == OperationalStatus.Up)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

