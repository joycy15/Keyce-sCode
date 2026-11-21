using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings.Git;

public partial class GitPanel : UserControl
{
    private readonly IGitService _git = new GitService();
    private string? _folder;

    public event EventHandler<string>? BranchChanged;

    public GitPanel()
    {
        InitializeComponent();
        ShowEmpty("Aucun dossier ouvert. Ouvrez un dossier contenant un dépôt Git, ou clonez un dépôt GitHub.");
    }

    public async Task OpenFolderAsync(string folder)
    {
        _folder = folder;
        FolderText.Text = folder;
        await RefreshAsync();
    }

    // ========== Boutons ==========

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ouvrir un dossier" };
        if (dialog.ShowDialog() == true)
            await OpenFolderAsync(dialog.FolderName);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Commit_Click(object sender, RoutedEventArgs e) => await CommitAsync();

    private async void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await CommitAsync();
        }
    }

    private async void Push_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Push en cours…", async () =>
        {
            if (await _git.GetRemoteUrlAsync(_folder!) is null)
                throw new GitException("Ajoute d'abord l'URL du dépôt GitHub.");
            return await _git.PushAsync(_folder!);
        });

    private async void Pull_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Pull en cours…", () => _git.PullAsync(_folder!));

    private async void SaveRemote_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Enregistrement…", async () =>
        {
            await _git.SetRemoteUrlAsync(_folder!, RemoteInput.Text);
            return "URL du dépôt enregistrée.";
        });

    // Cloner : demande où enregistrer, clone, puis ouvre le dépôt cloné
    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CloneUrlInput.Text))
        {
            EmptyInfoText.Text = "Colle d'abord l'URL du dépôt GitHub.";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choisir où enregistrer le dépôt" };
        if (dialog.ShowDialog() != true) return;

        IsEnabled = false;
        EmptyInfoText.Text = "Clonage en cours… (cela peut prendre un moment)";
        try
        {
            var target = await _git.CloneAsync(CloneUrlInput.Text, dialog.FolderName);
            CloneUrlInput.Clear();
            EmptyInfoText.Text = "";
            await OpenFolderAsync(target);
            ShowInfo($"Dépôt cloné dans {target}.");
        }
        catch (Exception ex) when (ex is GitException or ArgumentException)
        {
            EmptyInfoText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    // ========== Logique ==========

    // Exécute une action GitHub : bloque le panneau, affiche « en cours », gère les erreurs
    private async Task RunAsync(string busyMessage, Func<Task<string>> action)
    {
        if (_folder is null)
        {
            ShowInfo("Ouvre d'abord un dossier.");
            return;
        }

        IsEnabled = false;          // désactive tous les boutons du panneau
        ShowInfo(busyMessage);
        try
        {
            var message = await action();
            await RefreshAsync();
            ShowInfo(message);
        }
        catch (Exception ex) when (ex is GitException or ArgumentException)
        {
            ShowInfo(ex.Message);
        }
        finally
        {
            IsEnabled = true;       // réactive le panneau, même en cas d'erreur
        }
    }

    private async Task CommitAsync()
    {
        if (_folder is null) return;

        var files = FilesList.Items.Cast<GitFileItem>()
                                   .Where(f => f.IsChecked)
                                   .Select(f => f.Path)
                                   .ToList();
        try
        {
            await _git.CommitAsync(_folder, MessageInput.Text, files);
            MessageInput.Clear();
            await RefreshAsync();
            ShowInfo($"Commit réussi ({files.Count} fichier(s)). Clique sur Push pour l'envoyer sur GitHub.");
        }
        catch (Exception ex) when (ex is GitException or ArgumentException)
        {
            ShowInfo(ex.Message);
        }
    }

    private async Task RefreshAsync()
    {
        if (_folder is null) return;

        try
        {
            if (!await _git.IsRepositoryAsync(_folder))
            {
                ShowEmpty("Ce dossier n'est pas un dépôt Git.");
                BranchChanged?.Invoke(this, "");
                return;
            }

            var status = await _git.GetStatusAsync(_folder);
            var items = status.Files.Select(f => new GitFileItem(f.Path, f.Status)).ToList();

            EmptyView.Visibility = Visibility.Collapsed;
            RepoView.Visibility = Visibility.Visible;
            FilesList.ItemsSource = items;
            ChangesCount.Text = items.Count.ToString();
            RemoteInput.Text = await _git.GetRemoteUrlAsync(_folder) ?? "";
            ShowInfo(items.Count == 0 ? "Aucune modification." : "");

            BranchChanged?.Invoke(this, status.Branch);
        }
        catch (GitException ex)
        {
            ShowInfo(ex.Message);
        }
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyView.Visibility = Visibility.Visible;
        RepoView.Visibility = Visibility.Collapsed;
    }

    private void ShowInfo(string message) => InfoText.Text = message;
}

// Une ligne de la liste des modifications
public sealed class GitFileItem
{
    public GitFileItem(string path, string status)
    {
        Path = path;
        Status = status;
    }

    public string Path { get; }
    public string Status { get; }
    public bool IsChecked { get; set; } = true;

    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/'));

    public string Folder =>
        System.IO.Path.GetDirectoryName(Path.TrimEnd('/'))?.Replace('\\', '/') ?? "";

    public string Letter => Status == "??" ? "U" : Status[..1];
}
