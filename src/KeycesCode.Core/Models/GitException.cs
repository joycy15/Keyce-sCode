namespace KeycesCode.Core.Models;

// Erreur levée quand une commande git échoue, avec un message lisible
public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
}