using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private void OnPublishProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PublishHostInput is null || PublishOrganizationInput is null || PublishOwnerInput is null || PublishPrivateCheckBox is null) return;
        var provider = (PublishProviderSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        var azure = provider == "AzureDevOps";
        PublishHostInput.Visibility = azure || provider == "Bitbucket" ? Visibility.Collapsed : Visibility.Visible;
        PublishOrganizationInput.Visibility = azure ? Visibility.Visible : Visibility.Collapsed;
        PublishVisibilityNotice.Visibility = azure ? Visibility.Visible : Visibility.Collapsed;
        PublishOwnerInput.Header = azure ? "Azure project" : provider == "Bitbucket" ? "Bitbucket workspace" : provider == "GitLab" ? "Namespace (group/subgroup or username)" : "Owner / organization";
        PublishHostInput.Text = provider == "GitLab" ? "gitlab.com" : provider == "Bitbucket" ? "bitbucket.org" : "github.com";
        UpdatePublicationVisibility();
    }

    private void OnPublishModeChanged(object sender, RoutedEventArgs e) => UpdatePublicationVisibility();

    private void UpdatePublicationVisibility()
    {
        if (PublishPrivateCheckBox is null || PublishResumeCheckBox is null) return;
        var azure = (PublishProviderSelector.SelectedItem as ComboBoxItem)?.Tag as string == "AzureDevOps";
        PublishPrivateCheckBox.Visibility = azure ? Visibility.Collapsed : Visibility.Visible;
        PublishPrivateCheckBox.IsEnabled = PublishResumeCheckBox.IsChecked != true;
    }
}
