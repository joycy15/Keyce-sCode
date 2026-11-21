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

    // Prévient la fenêtre quand la branche change (pour la barre de statut)
    public event EventHandler<string>? BranchChanged;

    public GitPanel()
    {
        InitializeComponent();
        ShowEmpty("Le dossier ouvert ne contient pas de dépôt Git, ou aucun dossier n'est ouvert.");
    }

    // Utilisable depuis l'extérieur (page d'accueil, explorateur…)
    public async Task OpenFolderAsync(string folder)
    {
        _folder = folder;
        FolderText.Text = folder;
        await RefreshAsync();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ouvrir un dossier" };
        if (dialog.ShowDialog() == true)
            await OpenFolderAsync(dialog.FolderName);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Commit_Click(object sender, RoutedEventArgs e) => await CommitAsync();

    // Ctrl+Entrée dans le message = commit
    private async void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await CommitAsync();
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
            ShowInfo($"Commit réussi ({files.Count} fichier(s)).");
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

    // "src/App/MainWindow.xaml" → "MainWindow.xaml"
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/'));

    // "src/App/MainWindow.xaml" → "src/App"
    public string Folder =>
        System.IO.Path.GetDirectoryName(Path.TrimEnd('/'))?.Replace('\\', '/') ?? "";

    // Lettre affichée comme dans VS Code : "??" (non suivi) devient "U"
    public string Letter => Status == "??" ? "U" : Status[..1];
}
