using System.Collections.ObjectModel;
using OpenVpnClientManager.Infrastructure;
using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.ViewModels;

public sealed class CustomerViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private int _connectedCount;

    public Customer Model { get; }
    public ObservableCollection<SiteViewModel> Sites { get; } = [];

    public CustomerViewModel(Customer model)
    {
        Model = model;
    }

    public string Name => Model.Name;
    public string? Notes => Model.Notes;
    public int SiteCount => Sites.Count;
    public string SiteCountLabel => Sites.Count == 1 ? "1 site" : $"{Sites.Count} sites";

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public int ConnectedCount { get => _connectedCount; private set => Set(ref _connectedCount, value); }
    public bool HasConnected => ConnectedCount > 0;

    public void RefreshModel()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Notes));
        foreach (var s in Sites)
        {
            s.RefreshModel();
        }
    }

    public void RefreshCounts()
    {
        ConnectedCount = Sites.Count(s => s.State == VpnState.Connected);
        OnPropertyChanged(nameof(HasConnected));
        OnPropertyChanged(nameof(SiteCount));
        OnPropertyChanged(nameof(SiteCountLabel));
    }
}
