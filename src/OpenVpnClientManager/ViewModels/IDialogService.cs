using OpenVpnClientManager.Models;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.ViewModels;

public sealed record CustomerEditResult(string Name, string? Notes);

public sealed class SiteEditRequest
{
    public required IReadOnlyList<CustomerViewModel> Customers { get; init; }
    public CustomerViewModel? Customer { get; init; }
    public SiteViewModel? Existing { get; init; }
    public string? InitialConfigPath { get; init; }
}

public sealed class SiteEditResult
{
    public required CustomerViewModel Customer { get; init; }
    public required string Name { get; init; }
    public string? Notes { get; init; }

    /// <summary>Null when the config was not changed (edit only).</summary>
    public ParsedOvpn? Parsed { get; init; }
    public bool StoreExtractedCredentials { get; init; }
}

public sealed class ExportRequest
{
    public required string OutputPath { get; init; }
    public string? Passphrase { get; init; }
    public bool IncludeCredentials { get; init; }
    public required IReadOnlyList<Customer> Customers { get; init; }
}

public sealed class ImportBundleRequest
{
    public required string Path { get; init; }
    public string? Passphrase { get; init; }
    public bool OverwriteExisting { get; init; }
    public bool ImportCredentials { get; init; }
}

public sealed record BulkImportItem(string CustomerName, string SiteName, string SourcePath, ParsedOvpn Parsed);

public interface IDialogService
{
    Task<CredentialResponse?> RequestCredentialsAsync(CredentialRequest request);
    CustomerEditResult? EditCustomer(string title, string? name, string? notes);
    SiteEditResult? EditSite(SiteEditRequest request);
    ExportRequest? AskExport(IReadOnlyList<CustomerViewModel> customers);
    ImportBundleRequest? AskImportBundle(Func<string, string?, BundleContents> preview);
    bool Confirm(string title, string message, string okText, bool danger = false);
    void Info(string title, string message);
    void Error(string title, string message);
    bool ShowSettings(AppSettings settings);
    void ShowLog(SiteViewModel site);
    string? PickOvpnFile();
    IReadOnlyList<BulkImportItem>? AskBulkImport(IEnumerable<SiteViewModel> existingSites);
}
