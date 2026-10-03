using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace LyricMotion;

/// <summary>
/// Reads Noctis' lyric objects (one per word, one per line) by property name. The plugin
/// kit has no lyrics API, so the plugin looks at the same objects the lyrics page binds
/// to. Going by name instead of compiling against Noctis' internals means a Noctis
/// update that renames something makes the plugin step aside (the accessor comes back
/// null) rather than crash. Getters are compiled once per type: no reflection per frame.
/// </summary>
internal sealed class WordAccess
{
    private static readonly ConcurrentDictionary<Type, WordAccess?> Cache = new();

    public required Func<object, double> Progress { get; init; }
    public Func<object, double>? HeldMs { get; init; }
    public Func<object, bool>? IsEmphasis { get; init; }
    public Func<object, string?>? SweepText { get; init; }
    public Func<object, string?>? Text { get; init; }

    public static WordAccess? For(object word) => Cache.GetOrAdd(word.GetType(), Build);

    private static WordAccess? Build(Type type)
    {
        var progress = Getter.Make<double>(type, "Progress");
        if (progress is null) return null;
        return new WordAccess
        {
            Progress = progress,
            HeldMs = Getter.Make<double>(type, "HeldDurationMs"),
            IsEmphasis = Getter.Make<bool>(type, "IsEmphasis"),
            SweepText = Getter.Make<string?>(type, "SweepText"),
            Text = Getter.Make<string?>(type, "Text"),
        };
    }
}

internal sealed class LineAccess
{
    private static readonly ConcurrentDictionary<Type, LineAccess?> Cache = new();

    public required Func<object, bool> IsActive { get; init; }
    public Func<object, bool>? HasAnyWords { get; init; }
    public Func<object, string?>? Text { get; init; }

    public static LineAccess? For(object line) => Cache.GetOrAdd(line.GetType(), Build);

    private static LineAccess? Build(Type type)
    {
        var active = Getter.Make<bool>(type, "IsActive");
        if (active is null) return null;

        var words = Getter.Make<bool>(type, "HasWords");
        var background = Getter.Make<bool>(type, "HasBackgroundWords");
        var romanized = Getter.Make<bool>(type, "HasTransliterationWords");
        Func<object, bool>? any = null;
        if (words is not null)
            any = line => words(line) || (background?.Invoke(line) ?? false) || (romanized?.Invoke(line) ?? false);

        return new LineAccess { IsActive = active, HasAnyWords = any, Text = Getter.Make<string?>(type, "Text") };
    }
}

internal static class Getter
{
    public static Func<object, T>? Make<T>(Type type, string name)
    {
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0) return null;
            if (!typeof(T).IsAssignableFrom(property.PropertyType)) return null;

            var target = Expression.Parameter(typeof(object), "target");
            Expression body = Expression.Property(Expression.Convert(target, type), property);
            if (body.Type != typeof(T)) body = Expression.Convert(body, typeof(T));
            return Expression.Lambda<Func<object, T>>(body, target).Compile();
        }
        catch
        {
            return null;
        }
    }
}
