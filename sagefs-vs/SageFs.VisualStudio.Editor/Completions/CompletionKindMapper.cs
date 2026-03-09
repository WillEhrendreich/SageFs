namespace SageFs.VisualStudio.Editor.Completions;

/// <summary>
/// Completion item kind — maps daemon kind strings to a VS-friendly category.
/// Used by <see cref="CompletionKindMapper"/> and tested independently.
/// </summary>
public enum CompletionItemKinds
{
    Text      = 0,
    Method    = 1,
    Module    = 2,
    Keyword   = 3,
    Field     = 4,
    Property  = 5,
    Class     = 6,
    Interface = 7,
    Local     = 8,
    Value     = 9,
}

/// <summary>
/// Pure static mapper from daemon kind strings to <see cref="CompletionItemKinds"/>.
/// </summary>
internal static class CompletionKindMapper
{
    /// <summary>
    /// Maps a daemon-emitted kind string to the corresponding <see cref="CompletionItemKinds"/>.
    /// Returns <see cref="CompletionItemKinds.Text"/> for unknown or null values.
    /// </summary>
    public static CompletionItemKinds ToCompletionItemKind(string? kind) =>
        kind switch
        {
            "method"    => CompletionItemKinds.Method,
            "function"  => CompletionItemKinds.Method,
            "module"    => CompletionItemKinds.Module,
            "namespace" => CompletionItemKinds.Keyword,
            "field"     => CompletionItemKinds.Field,
            "property"  => CompletionItemKinds.Property,
            "keyword"   => CompletionItemKinds.Keyword,
            "class"     => CompletionItemKinds.Class,
            "interface" => CompletionItemKinds.Interface,
            "variable"  => CompletionItemKinds.Local,
            "value"     => CompletionItemKinds.Value,
            _           => CompletionItemKinds.Text,
        };
}
