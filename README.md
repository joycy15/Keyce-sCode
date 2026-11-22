# Keyce's Code

IDE de bureau Windows construit en **C# / WPF sur .NET 8**. L'éditeur de texte est AvalonEdit.

## Démarrer

```powershell
dotnet restore "Keyce'sCode.csproj"
dotnet run --project "Keyce'sCode.csproj"
```

Le package NuGet utilisé est `AvalonEdit` (`6.3.1.120`). Il est restauré par le projet.

### Prérequis

- **.NET 8 SDK** : construire l’IDE, exécuter les projets C# et compiler les fichiers `.cs` isolés.
- **Python** : interpréteur `python` dans le `PATH` et extension Python de VS Code (`ms-python.debugpy-*`) pour le débogage.
- **C/C++** : `gcc`, `g++` et `gdb` dans le `PATH` (ou MSYS2 UCRT64), ainsi que l’extension C/C++ de VS Code (`ms-vscode.cpptools-*`) pour le débogage.
- **JavaScript** : Node.js dans le `PATH` (ou l’installation utilisateur détectée par l’application).
- **TypeScript** : Node.js, `typescript` et `tsx`, installés dans le projet ou globalement.
- **PHP** : PHP 8.4, Xdebug et l’extension PHP Debug de VS Code (`xdebug.php-debug-*`). Le fichier `php_xdebug.dll` doit être placé dans `%LOCALAPPDATA%\Keyce-sCode\php`.
- **HTML/CSS** : association de fichiers configurée dans Windows, généralement un navigateur.

Les adaptateurs de débogage Python, C/C++ et PHP sont recherchés dans le dossier des extensions VS Code de l’utilisateur. Cette intégration utilise les outils installés localement ; elle ne permet pas d’installer ni de charger les extensions du Marketplace directement dans Keyce’s Code.

## Fonctionnalités

- Explorateur de dossiers à chargement progressif : ouvrir un dossier ou un fichier, créer des fichiers et dossiers, actualiser l’arborescence et ouvrir les fichiers en onglets.
- Éditeur avec numéros de ligne liés au défilement, indentation à quatre espaces, points d’arrêt, thèmes sombre et clair, et commandes pour ouvrir/créer/enregistrer des fichiers.
- Coloration basée sur les grammaires XSHD intégrées à AvalonEdit pour Python, C#, JavaScript/TypeScript, HTML, CSS, PHP et C/C++ (`.py`, `.cs`, `.js`, `.mjs`, `.cjs`, `.ts`, `.mts`, `.cts`, `.tsx`, `.html`, `.htm`, `.css`, `.php`, `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, `.hh`, `.hxx`). Le HTML colore également le JavaScript des blocs `<script>` et le CSS des blocs `<style>`.
- Ouverture et sauvegarde asynchrones ; les sauvegardes écrivent un fichier temporaire UTF-8 sans BOM puis le remplacent, et les fichiers de plus d’un million de caractères restent éditables sans coloration pour limiter les ralentissements.
- Dans un fichier `.html`, une ligne ne contenant que `!` est remplacée par un squelette HTML5 en appuyant sur `Tab` ou `Entrée`; le curseur se place dans le corps.
- Sortie, erreurs de compilation et codes de sortie affichés dans la console intégrée. Le bouton **Arrêter** interrompt l’exécution et ses processus enfants; chaque processus est limité à deux minutes.
- Exécution avec **Ctrl+F5** et débogage interactif avec points d’arrêt, pile d’appels, variables et commandes pas-à-pas.

Le débogage est pris en charge pour C# (`.cs`, `.csproj`), Python (`.py`), C/C++ (`.c`, `.cpp`, `.cc`, `.cxx`), JavaScript/TypeScript (`.js`, `.mjs`, `.cjs`, `.ts`, `.mts`, `.cts`, `.tsx`) et PHP (`.php`). Les fichiers C# sont associés à leur projet parent lorsqu’il est trouvé; sinon, un fichier autonome est compilé dans un projet temporaire .NET 8. Ouvrez directement le `.csproj` voulu si le dossier contient plusieurs projets. Les fichiers TypeScript sont vérifiés avant exécution/débogage; les fichiers d’en-tête C/C++ sont colorés mais ne se lancent pas seuls. HTML et CSS s’ouvrent avec l’application Windows associée et ne disposent pas de débogueur intégré.

Raccourcis : `F9` ajoute/retire un point d’arrêt; `F5` démarre ou continue le débogage; `F10` passe à l’instruction suivante; `F11` entre dans l’appel; `Maj+F11` sort de l’appel; `Maj+F5` arrête le débogueur; `Ctrl+F5` exécute sans déboguer.

## Organisation

- `MainWindow.xaml` et `MainWindow.xaml.cs` : composition de l'interface et orchestration des commandes utilisateur.
- `Models/EditorDocument.cs` : état d'un document ouvert.
- `Services/FileService.cs` : lecture UTF-8 avec détection de BOM et sauvegarde atomique asynchrones.
- `Services/SyntaxHighlighter.cs` : contrat `ISyntaxHighlighter`, grammaires intégrées AvalonEdit et prise en charge CSS/JavaScript dans le HTML.
- `Services/ExecutionService.cs` : contrat `ILanguageRunner`, sélection du lanceur et gestion commune des processus, flux, annulation et délai maximal.
- `Services/LanguageRunners.cs` : lanceurs Python, C#, C/C++, PHP, HTML/CSS et JavaScript.
- `Services/DebugSession.cs`, `Services/DebugAdapterClient.cs` et `Services/NodeInspectorClient.cs` : configuration des adaptateurs et échanges DAP/Node Inspector pour le débogage.

## Extension

- Pour ajouter une extension de coloration, associer son suffixe à un groupe dans `LanguageGroups`, puis définir ses mots-clés et ses commentaires dans `LoadDefinition`.
- Pour ajouter un exécuteur, implémenter `ILanguageRunner`, construire le processus via `ProcessStartInfo.ArgumentList`, et enregistrer le lanceur dans `ExecutionService`.
- L’explorateur permet de créer des fichiers et dossiers, mais ne propose pas encore le renommage ni la suppression.
- L’application prend en charge les langages et outils listés ci-dessus; elle ne charge pas les extensions du Marketplace VS Code.

## Vérification de l’éditeur

Sous Windows avec le SDK .NET 8, lancez les tests ciblés de coloration, d’encodage et de sauvegarde :

```powershell
dotnet run --project "Keyce-sCode.Tests\Keyce-sCode.Tests.csproj" -- editor
```
