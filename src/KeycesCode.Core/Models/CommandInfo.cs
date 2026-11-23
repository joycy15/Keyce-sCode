namespace KeycesCode.Core.Models;

// Une commande de l'IDE : identifiant unique, titre affiché, raccourci par défaut
public sealed record CommandInfo(string Id, string Title, string DefaultShortcut);
