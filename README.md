# Keyce's Code

IDE de bureau Windows construit en **C# / WPF sur .NET 8**. L'éditeur de texte est AvalonEdit.

## Démarrer

```powershell
dotnet restore "Keyce'sCode.csproj"
dotnet run --project "Keyce'sCode.csproj"
```

Le package NuGet utilisé est `AvalonEdit` (`6.3.1.120`). Il est restauré par le projet.

### Prérequis des lanceurs

- **.NET 8 SDK** : construire l'IDE et exécuter les fichiers `.cs` isolés.
- **Python** : commande `python` ou, en repli, `python3` dans le `PATH`.
- **C#** : commande `dotnet` dans le `PATH`.
- **C/C++** : `gcc` pour `.c`, `g++` pour `.cpp`, dans le `PATH`.
- **PHP** : commande `php` dans le `PATH`.
- **JavaScript** : commande `node` dans le `PATH`.
- **HTML/CSS** : association de fichiers configurée dans Windows (généralement un navigateur).

## Fonctionnalités

- Explorateur de dossiers à chargement progressif, onglets de fichiers et éditeur avec numéros de ligne.
- Thèmes sombre et clair, enregistrer/ouvrir/créer et raccourcis `Ctrl+N`, `Ctrl+O`, `Ctrl+S`, `F5`.
- Coloration dynamique pour `.py`, `.cs`, `.js`, `.html`, `.css`, `.php`, `.c`, `.cpp`, `.h` et `.hpp`.
- Dans un fichier `.html`, une ligne ne contenant que `!` est remplacée par un squelette HTML5 en appuyant sur `Tab` ou `Entrée`; le curseur se place dans le corps.
- Sortie et erreurs de processus affichées au fil de l'exécution dans la console intégrée. Le bouton **Arrêter** annule le processus et ses processus enfants; chaque processus est limité à deux minutes.

Les fichiers `.cs` sont compilés/exécutés à partir d'un projet temporaire .NET 8 contenant le fichier seul. Les fichiers d'en-tête `.h` et `.hpp` ne peuvent pas être lancés seuls. Les fichiers HTML et CSS sont ouverts par Windows; JavaScript est exécuté avec Node.js.

## Organisation

- `MainWindow.xaml` et `MainWindow.xaml.cs` : composition de l'interface et orchestration des commandes utilisateur.
- `Models/EditorDocument.cs` : état d'un document ouvert.
- `Services/FileService.cs` : lecture et écriture UTF-8.
- `Services/SyntaxHighlighter.cs` : contrat `ISyntaxHighlighter`, définitions AvalonEdit par langage et thème.
- `Services/ExecutionService.cs` : contrat `ILanguageRunner`, sélection du lanceur et gestion commune des processus, flux, annulation et délai maximal.
- `Services/LanguageRunners.cs` : lanceurs Python, C#, C/C++, PHP, HTML/CSS et JavaScript.

## Extension

- Pour ajouter une extension de coloration, associer son suffixe à un groupe dans `LanguageGroups`, puis définir ses mots-clés et ses commentaires dans `LoadDefinition`.
- Pour ajouter un exécuteur, implémenter `ILanguageRunner`, construire le processus via `ProcessStartInfo.ArgumentList`, et enregistrer le lanceur dans `ExecutionService`.
- Les fichiers `.cs` sont actuellement traités comme des programmes autonomes, pas comme des projets/solutions. Pour exécuter une solution complète, ajouter un lanceur qui identifie le `.csproj` ou `.sln` correspondant et appelle `dotnet run --project`.
