namespace KeycesCode.Core.Models;

// Qui parle : les consignes (System), toi (User) ou l'IA (Assistant)
public enum ChatRole { System, User, Assistant }

// Un message de la conversation
public sealed record ChatMessage(ChatRole Role, string Content);
