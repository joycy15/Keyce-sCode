namespace KeycesCode.Core.Models;

// Un fichier modifié : son chemin et son état ("M", "A", "D", "??"…)
public sealed record GitFileChange(string Path, string Status);

// L'état d'un dépôt : la branche et la liste des fichiers modifiés
public sealed record GitStatus(string Branch, IReadOnlyList<GitFileChange> Files);