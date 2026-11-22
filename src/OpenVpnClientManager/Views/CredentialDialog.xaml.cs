using System.Windows;
using OpenVpnClientManager.Services;

namespace OpenVpnClientManager.Views;

public partial class CredentialDialog : Window
{
    private readonly CredentialRequest _request;

    public CredentialDialog(CredentialRequest request)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _request = request;

        Eyebrow.Text = request.CustomerName;
        TitleText.Text = request.SiteName;
        FailedBanner.Visibility = request.PreviousAttemptFailed ? Visibility.Visible : Visibility.Collapsed;

        switch (request.Kind)
        {
            case "Private Key":
                SubtitleText.Text = "De privésleutel in deze configuratie is versleuteld. Voer het wachtwoord van de sleutel in.";
                UsernamePanel.Visibility = Visibility.Collapsed;
                PasswordLabel.Text = "Wachtwoord van de privésleutel";
                break;
            case "HTTP Proxy":
                SubtitleText.Text = "De HTTP-proxy vraagt om inloggegevens.";
                break;
            default:
                SubtitleText.Text = "De VPN-server vraagt om inloggegevens.";
                break;
        }

        UsernamePanel.Visibility = request.NeedsUsername ? Visibility.Visible : Visibility.Collapsed;
        UsernameBox.Text = request.PrefilledUsername ?? string.Empty;

        if (request.ChallengeText is not null)
        {
            ChallengePanel.Visibility = Visibility.Visible;
            ChallengeLabel.Text = string.IsNullOrWhiteSpace(request.ChallengeText) ? "Eenmalige code" : request.ChallengeText;
            if (!request.ChallengeEcho)
            {
                ChallengeBox.Visibility = Visibility.Collapsed;
                ChallengeSecretBox.Visibility = Visibility.Visible;
            }
            RememberHint.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            if (request.NeedsUsername && string.IsNullOrEmpty(UsernameBox.Text))
            {
                UsernameBox.Focus();
            }
            else if (request.ChallengeText is not null && request.HasStoredCredentials && !request.PreviousAttemptFailed)
            {
                (request.ChallengeEcho ? ChallengeBox : (UIElement)ChallengeSecretBox).Focus();
            }
            else
            {
                PasswordBox.Focus();
            }
        };
    }

    public CredentialResponse? Result { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string? challenge = _request.ChallengeText is null ? null : (_request.ChallengeEcho ? ChallengeBox.Text : ChallengeSecretBox.Password);
        Result = new CredentialResponse
        {
            Username = _request.NeedsUsername ? UsernameBox.Text.Trim() : null,
            Password = PasswordBox.Password,
            ChallengeResponse = challenge,
            Remember = RememberBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
