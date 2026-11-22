# Module `KeycesCode.Execution` — Guide d'intégration (Membre 4)

## Responsabilité
Le projet `KeycesCode.Execution` gère :
1. **La détection d'outils** (`IToolDetectionService` / `ToolDetectionService`) : vérification de la présence des compilateurs/runtimes (`dotnet`, `python`, `node`, `gcc`, `g++`, `javac`, `git`) et association extension de fichier -> outil.
2. **L'exécution et le débogage** (`IRunService` / `RunService`) : lancement de fichiers ou projets, diffusion des flux `StandardOutput` / `StandardError` / `SystemMessage` via `OutputReceived`, notification de fin via `ProcessExited`, arrêt propre via `StopCurrentProcessAsync()`.
3. **Le terminal interactif** (`ITerminalService` / `TerminalService`) : ouverture d'une session shell persistante, envoi de commandes (`SendInputAsync`) et changement de répertoire (`ChangeDirectoryAsync`).

## Point d'entrée rapide
```csharp
var detector = ExecutionModuleFactory.CreateToolDetectionService();
var runner = ExecutionModuleFactory.CreateRunService(detector);
var terminal = ExecutionModuleFactory.CreateTerminalService();
### 4. Commit D — Ajouter un script local de vérification rapide `installer/check-env.ps1`
Permet à n'importe quel membre sous Windows de vérifier en une commande que `git` et `dotnet` sont bien présents sur sa machine avant de compiler ou pousser.

```powershell
@'
Write-Host "=== Vérification environnement Keyce'sCode ===" -ForegroundColor Cyan

foreach ($cmd in @("git", "dotnet", "python", "node")) {
    $found = Get-Command $cmd -ErrorAction SilentlyContinue
    if ($found) {
        Write-Host "[OK] $cmd -> $($found.Source)" -ForegroundColor Green
    } else {
        Write-Host "[ABSENT] $cmd non trouvé dans le PATH" -ForegroundColor Yellow
    }
}
