# 💻 Keyce'sCode

> Éditeur de code modulaire développé en équipe sous .NET / C#, intégrant gestion de fichiers, coloration syntaxique, assistant IA, exécution/débogage et personnalisation.

---

## 🌟 Vision du Projet

**Keyce'sCode** est un environnement de développement léger et extensible conçu de manière collaborative sur GitHub. L'architecture repose sur une séparation nette des responsabilités par modules et contrats d'interfaces afin que chaque membre de l'équipe puisse développer, tester et intégrer ses fonctionnalités en parallèle sans conflits.

---

## 🚀 Fonctionnalités Clés

* **🖥️ Interface & Éditeur (`KeycesCode.App`, `KeycesCode.Editor`)** : fenêtre principale, zone d'édition de code et registre centralisé des commandes.
* **📁 Fichiers & Projets (`KeycesCode.Files`, `KeycesCode.Tests`)** : ouverture/sauvegarde de fichiers, gestion multi-documents (onglets), espace de travail et tests unitaires.
* **🧠 Langages & Assistant IA (`KeycesCode.Languages`, `KeycesCode.Assistant`)** : détection/gestion des langages et aide intelligente intégrée au code.
* **▶️ Exécution & Débogage (`KeycesCode.Execution`)** : détection automatique des outils installés (compilateurs/interpréteurs), exécution de programmes, terminal intégré et suivi de débogage.
* **⚙️ Paramètres, Thèmes & GitHub (`KeycesCode.Settings`, `installer/`, `docs/`)** : préférences utilisateur, thèmes visuels, intégration Git, documentation et installeur.

---

## 🛠️ Stack Technique

* **Langage & Framework** : C# / .NET
* **Architecture** : Solution modulaire orientée interfaces (Injection de dépendances)
* **Gestion de version & Collaboration** : Git & GitHub (branches `feature/mX-...` + Pull Requests)

---

## 👥 L'Équipe & Répartition des Rôles

Membres du projet : **Joyce**, **William**, **Apostel**, **Layelle**, **Travis**

| Membre | Nom | Rôle | Projets possédés | Interfaces à implémenter | Préfixe de branche |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Membre 1** | **Travis** | Interface et éditeur | `KeycesCode.App`, `KeycesCode.Editor` | `IEditorService`, `ICommandRegistry` | `feature/m1-...` |
| **Membre 2** | **Layelle** | Fichiers et projets | `KeycesCode.Files`, `KeycesCode.Fakes`, `KeycesCode.Tests` | `IFileService`, `IDocumentManager`, `IWorkspaceService` | `feature/m2-...` |
| **Membre 3** | **Apostel** | Langages et assistant IA | `KeycesCode.Languages`, `KeycesCode.Assistant` | `ILanguageService`, `IAssistantService` | `feature/m3-...` |
| **Membre 4** | **William** | Exécution et débogage | `KeycesCode.Execution` | `IToolDetectionService`, `IRunService`, `ITerminalService` | `feature/m4-...` |
| **Membre 5** | **Joyce** | Personnalisation, GitHub et distribution | `KeycesCode.Settings`, `installer/`, `docs/` | `ISettingsService`, `IThemeService`, `IGitService` | `feature/m5-...` |

---

## 📂 Structure du Dépôt

```text
Keyce-sCode/
├── docs/                         # Documentation technique et guides utilisateur
├── installer/                    # Scripts et configuration de distribution
├── src/
│   ├── KeycesCode.App/           # Application principale (point d'entrée UI)
│   ├── KeycesCode.Editor/        # Composant d'édition et registre de commandes
│   ├── KeycesCode.Files/         # Gestion des fichiers, documents et espaces de travail
│   ├── KeycesCode.Fakes/         # Implémentations simulées pour les tests
│   ├── KeycesCode.Tests/         # Suite de tests unitaires et d'intégration
│   ├── KeycesCode.Languages/     # Support et analyse des langages
│   ├── KeycesCode.Assistant/     # Services d'assistance IA
│   ├── KeycesCode.Execution/     # Détection d'outils, exécution, terminal et débogage
│   └── KeycesCode.Settings/      # Paramètres, thèmes et intégration Git
├── .gitignore
└── README.md

---

## ?? �tat d'avancement � Module Ex�cution & D�bogage (KeycesCode.Execution)
- Contrats d�finis : `IToolDetectionService`, `IRunService`, `ITerminalService`
- Services impl�ment�s : `ToolDetectionService`, `RunService`, `TerminalService`, `ExecutionModuleFactory`
- Documentation module : [Guide d'int�gration KeycesCode.Execution](docs/execution-module.md)
