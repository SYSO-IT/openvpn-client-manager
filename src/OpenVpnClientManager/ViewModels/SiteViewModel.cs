using System.ComponentModel;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.ViewModels;

public sealed class SiteViewModel : ObservableObject
{
    private int _searchScore;
    private bool _hasStoredCredentials;
    private bool _isSelected;
    private string _duration = string.Empty;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public Site Model { get; }
    public CustomerViewModel Customer { get; }
    public VpnConnection Connection { get; }

    public SiteViewModel(CustomerViewModel customer, Site model, VpnConnection connection)
    {
        Customer = customer;
        Model = model;
        Connection = connection;
        Connection.PropertyChanged += OnConnectionChanged;
        RefreshCredentialState();
    }

    public string Name => Model.Name;
    public string? Notes => Model.Notes;
    public string Remote => Model.Remote ?? string.Empty;
    public string ProtocolLabel => string.IsNullOrEmpty(Model.Protocol) ? string.Empty : Model.Protocol!.ToUpperInvariant();
    public string CustomerName => Customer.Name;
    public bool RequiresUserPass => Model.RequiresUserPass;
    public string LastConnectedLabel => Model.LastConnectedUtc is { } t ? t.ToLocalTime().ToString("d MMM yyyy HH:mm") : "nooit";
    public string ImportedLabel => Model.ImportedUtc is { } t ? t.ToLocalTime().ToString("d MMM yyyy") : "-";

    public VpnState State => Connection.State;
    public string StatusText => Connection.StatusText;
    public bool IsActive => Connection.IsActive;
    public bool IsConnected => Connection.State == VpnState.Connected;
    public bool CanConnect => Connection.CanConnect;
    public string? LastError => Connection.LastError;
    public string? LocalIp => Connection.LocalIp;
    public string? RemoteIp => Connection.RemoteIp;
    public long BytesIn => Connection.BytesIn;
    public long BytesOut => Connection.BytesOut;

    public string Duration { get => _duration; private set => Set(ref _duration, value); }

    public bool HasStoredCredentials { get => _hasStoredCredentials; private set => Set(ref _hasStoredCredentials, value); }

    public int SearchScore { get => _searchScore; set => Set(ref _searchScore, value); }

    /// <summary>Text the fuzzy search runs against.</summary>
    public string SearchText => $"{Customer.Name} {Model.Name} {Model.Remote}";

    public void RefreshCredentialState() => HasStoredCredentials = CredentialStore.Exists(Model.Id);

    public void RefreshModel()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Remote));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(CustomerName));
        OnPropertyChanged(nameof(RequiresUserPass));
        OnPropertyChanged(nameof(LastConnectedLabel));
        OnPropertyChanged(nameof(ImportedLabel));
        OnPropertyChanged(nameof(SearchText));
        RefreshCredentialState();
    }

    public void Tick()
    {
        if (Connection.ConnectedSinceUtc is { } since)
        {
            var d = DateTime.UtcNow - since;
            Duration = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
        }
        else if (Duration.Length > 0)
        {
            Duration = string.Empty;
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VpnConnection.State):
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(LastConnectedLabel));
                Customer.RefreshCounts();
                RefreshCredentialState();
                break;
            case nameof(VpnConnection.StatusText):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(VpnConnection.LastError):
                OnPropertyChanged(nameof(LastError));
                break;
            case nameof(VpnConnection.LocalIp):
                OnPropertyChanged(nameof(LocalIp));
                break;
            case nameof(VpnConnection.RemoteIp):
                OnPropertyChanged(nameof(RemoteIp));
                break;
            case nameof(VpnConnection.BytesIn):
                OnPropertyChanged(nameof(BytesIn));
                break;
            case nameof(VpnConnection.BytesOut):
                OnPropertyChanged(nameof(BytesOut));
                break;
        }
    }
}
