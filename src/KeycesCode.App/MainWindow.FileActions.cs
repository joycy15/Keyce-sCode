using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace KeycesCode.App;

// Explorateur : menu du clic droit, renommer (F2), supprimer (Suppr) vers la Corbeille,
// copier le chemin, afficher dans l'Explorateur Windows.
public partial class MainWindow
{
    private void InitializeExplorerMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Nouveau fichier...", "", NewFile_Click));
        menu.Items.Add(MenuEntry("Nouveau dossier...", "", NewFolder_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Renommer...", "F2", (_, _) => RenameSelected()));
        menu.Items.Add(MenuEntry("Supprimer", "Suppr", (_, _) => DeleteSelected()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Copier le chemin", "", (_, _) => CopySelectedPath()));
        menu.Items.Add(MenuEntry("Afficher dans l'Explorateur Windows", "", (_, _) => RevealSelected()));
        ExplorerTree.ContextMenu = menu;

        // Clic droit : on sélectionne d'abord l'élément sous la souris
        ExplorerTree.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindTreeItem(e.OriginalSource as DependencyObject) is { } item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        };

        // Raccourcis clavier dans l'explorateur
        ExplorerTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        };
    }

    private static MenuItem MenuEntry(string header, string gesture, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += onClick;
        return item;
    }

    private static TreeViewItem? FindTreeItem(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }

    // Élément sélectionné dans l'explorateur (jamais le dossier racine pour renommer / supprimer)
    private bool TryGetSelectedEntry(out TreeViewItem node, out string path)
    {
        node = null!;
        path = "";
        if (ExplorerTree.SelectedItem is not TreeViewItem { Tag: string selected } item) return false;
        if (string.Equals(selected.TrimEnd('\\', '/'), _workspacePath.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Le dossier racine ne peut pas être renommé ni supprimé ici.";
            return false;
        }
        node = item;
        path = selected;
        return File.Exists(path) || Directory.Exists(path);
    }

    // ----- Renommer -----

    private void RenameSelected()
    {
        if (!TryGetSelectedEntry(out var node, out var path)) return;

        bool isDirectory = Directory.Exists(path);
        string oldName = Path.GetFileName(path);
        string? input = ShowNameDialog("Renommer",
            isDirectory ? "Nouveau nom du dossier :" : "Nouveau nom du fichier :", oldName, "Renommer");
        if (string.IsNullOrWhiteSpace(input)) return;

        string newName = input.Trim();
        if (newName == oldName) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Nom invalide", "Un nom de fichier ne peut pas contenir \\ / : * ? \" < > |");
            return;
        }

        string newPath = Path.Combine(Path.GetDirectoryName(path)!, newName);
        bool onlyCaseChanges = string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase);
        if (!onlyCaseChanges && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            ShowError("Nom déjà utilisé", $"« {newName} » existe déjà dans ce dossier.");
            return;
        }

        // Les fichiers ouverts concernés : on les enregistre, puis on les rouvrira sous leur nouveau nom
        var opened = DocumentsInside(path);
        foreach (var document in opened.Where(d => d.IsDirty)) SaveDocument(document);

        try
        {
            if (isDirectory) Directory.Move(path, newPath);
            else File.Move(path, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Impossible de renommer", ex.Message);
            return;
        }

        foreach (var document in opened)
        {
            CloseDocument(document);
            OpenDocument(newPath + document.Path[path.Length..]);
        }

        RefreshParentOf(node);
        StatusText.Text = $"Renommé : {oldName} → {newName}";
    }

    // ----- Supprimer (Corbeille) -----

    private void DeleteSelected()
    {
        if (!TryGetSelectedEntry(out var node, out var path)) return;

        bool isDirectory = Directory.Exists(path);
        string name = Path.GetFileName(path);
        var opened = DocumentsInside(path);
        string unsaved = opened.Any(d => d.IsDirty) ? "\n\nAttention : des modifications non enregistrées seront perdues." : "";

        var answer = MessageBox.Show(this,
            $"Envoyer « {name} »{(isDirectory ? " et tout son contenu" : "")} dans la Corbeille ?{unsaved}",
            "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var document in opened) CloseDocument(document);

        if (!MoveToRecycleBin(path))
        {
            ShowError("Impossible de supprimer",
                $"« {name} » n'a pas pu être supprimé. Il est peut-être ouvert dans un autre programme.");
            return;
        }

        RefreshParentOf(node);
        StatusText.Text = $"Supprimé (dans la Corbeille) : {name}";
    }

    // Windows : SHFileOperation avec FOF_ALLOWUNDO envoie à la Corbeille au lieu d'effacer définitivement
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation operation);

    private bool MoveToRecycleBin(string path)
    {
        const uint FO_DELETE = 0x0003;
        const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

        var operation = new ShFileOperation
        {
            Window = new WindowInteropHelper(this).Handle,
            Function = FO_DELETE,
            From = path + "\0\0",   // la liste des chemins se termine par deux caractères nuls
            Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };

        int result = SHFileOperation(ref operation);
        return result == 0 && !operation.AnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    // ----- Autres actions -----

    private void CopySelectedPath()
    {
        if (ExplorerTree.SelectedItem is TreeViewItem { Tag: string path })
        {
            Clipboard.SetText(path);
            StatusText.Text = $"Chemin copié : {path}";
        }
    }

    private void RevealSelected()
    {
        if (ExplorerTree.SelectedItem is TreeViewItem { Tag: string path })
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // ----- Utilitaires -----

    // Documents ouverts qui sont ce fichier, ou qui se trouvent dans ce dossier
    private List<EditorDocument> DocumentsInside(string path)
    {
        string folder = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return _documents.Values
            .Where(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase) ||
                        d.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Recharge seulement le dossier parent (les autres dossiers ouverts restent ouverts)
    private void RefreshParentOf(TreeViewItem node)
    {
        if (ItemsControl.ItemsControlFromItemContainer(node) is TreeViewItem parent)
        {
            PopulateDirectoryNode(parent);
            parent.IsExpanded = true;
        }
        else
        {
            LoadExplorer();
        }
    }

    private void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
