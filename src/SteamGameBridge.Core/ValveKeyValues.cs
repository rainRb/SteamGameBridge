using System.Text;

namespace SteamGameBridge.Core;

/// <summary>A strict text-span parser. Edits affect only one targeted VDF value; comments and all other text survive.</summary>
public static class ValveKeyValues
{
    internal sealed record Token(string Value, int Start, int End, char Kind);
    internal sealed class Node
    {
        public required string Key { get; init; }
        public string? Value { get; init; }
        public int Start { get; init; }
        public int End { get; init; }
        public int ValueStart { get; init; }
        public int ValueEnd { get; init; }
        public int CloseStart { get; init; }
        public List<Node>? Children { get; init; }
    }
    internal static List<Node> Parse(string text)
    {
        var tokens = Lex(text); int i = 0;
        var nodes = ParseNodes(tokens, ref i, false);
        if (i != tokens.Count) throw new BridgeException("VDF 文件含多余结构。");
        return nodes;
    }
    private static List<Node> ParseNodes(List<Token> tokens, ref int i, bool inObject)
    {
        var nodes = new List<Node>();
        while (i < tokens.Count && tokens[i].Kind != '}')
        {
            var key = tokens[i++];
            if (key.Kind != 's' || i >= tokens.Count) throw new BridgeException("VDF 键值结构无效。");
            var value = tokens[i++];
            if (value.Kind == '{')
            {
                var children = ParseNodes(tokens, ref i, true);
                if (i >= tokens.Count || tokens[i].Kind != '}') throw new BridgeException("VDF 括号不匹配。");
                var end = tokens[i++];
                nodes.Add(new() { Key = key.Value, Start = key.Start, End = end.End, CloseStart = end.Start, Children = children });
            }
            else if (value.Kind == 's') nodes.Add(new() { Key = key.Value, Value = value.Value, Start = key.Start, End = value.End, ValueStart = value.Start, ValueEnd = value.End });
            else throw new BridgeException("VDF 键值结构无效。");
        }
        if (!inObject && i < tokens.Count) throw new BridgeException("VDF 存在意外闭括号。");
        return nodes;
    }
    private static List<Token> Lex(string text)
    {
        var tokens = new List<Token>(); int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '\uFEFF') { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            var start = i;
            if (text[i] is '{' or '}') { tokens.Add(new(text[i].ToString(), i, ++i, text[start])); continue; }
            if (text[i] == '"')
            {
                i++; var b = new StringBuilder(); bool closed = false;
                while (i < text.Length)
                {
                    var c = text[i++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\' && i < text.Length && text[i] is '\\' or '"') c = text[i++];
                    b.Append(c);
                }
                if (!closed) throw new BridgeException("VDF 字符串未闭合。");
                tokens.Add(new(b.ToString(), start, i, 's'));
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}') i++;
                tokens.Add(new(text[start..i], start, i, 's'));
            }
        }
        return tokens;
    }
    internal static Node? Unique(List<Node> nodes, string key)
    {
        var found = nodes.Where(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length > 1) throw new BridgeException($"VDF 中存在重复的 {key} 项，拒绝含糊修改。");
        return found.FirstOrDefault();
    }
    private static string[] LaunchPath(string appId) { SteamAppMetadata.ValidateAppId(appId); return ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId]; }
    public static (bool Exists, string? Value) ReadLaunchOptions(string text, string appId)
    {
        var nodes = Parse(text);
        foreach (var key in LaunchPath(appId))
        {
            var n = Unique(nodes, key);
            if (n is null) return (false, null);
            nodes = n.Children ?? throw new BridgeException("Steam 启动项路径不是对象。");
        }
        var value = Unique(nodes, "LaunchOptions");
        if (value?.Children is not null) throw new BridgeException("LaunchOptions 不是字符串。");
        return (value is not null, value?.Value);
    }
    public static IReadOnlyDictionary<string, string> ReadAllLaunchOptions(string text)
    {
        var nodes = Parse(text);
        foreach (string key in new[] { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps" })
        {
            var node = Unique(nodes, key); if (node is null) return new Dictionary<string, string>();
            nodes = node.Children ?? throw new BridgeException("Steam 启动项路径不是对象。");
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in nodes)
        {
            if (!uint.TryParse(app.Key, out uint id) || id == 0) continue;
            var value = Unique(app.Children ?? throw new BridgeException("游戏配置不是对象。"), "LaunchOptions");
            if (value?.Children is not null) throw new BridgeException("启动项不是字符串。");
            if (value?.Value is { } command && !result.TryAdd(app.Key, command)) throw new BridgeException("AppID 配置重复。");
        }
        return result;
    }

    public static string SetLaunchOptions(string text, string appId, string? value, bool exists = true)
        => SetValue(text, LaunchPath(appId), "LaunchOptions", value, exists);

    internal static string SetValue(string text, string[] objectPath, string leaf, string? value, bool exists = true)
    {
        var nodes = Parse(text); Node? parent = null; var depth = 0;
        for (; depth < objectPath.Length; depth++)
        {
            var node = Unique(nodes, objectPath[depth]);
            if (node is null) break;
            nodes = node.Children ?? throw new BridgeException("Steam 启动项路径不是对象。"); parent = node;
        }
        if (depth == objectPath.Length)
        {
            var old = Unique(nodes, leaf);
            if (old is not null)
            {
                if (old.Children is not null) throw new BridgeException("LaunchOptions 不是字符串。");
                return exists ? text[..old.ValueStart] + Quote(value ?? "") + text[old.ValueEnd..] : text[..old.Start] + text[old.End..];
            }
        }
        if (!exists) return text;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var added = Quote(leaf) + "\t\t" + Quote(value ?? "");
        for (var n = objectPath.Length - 1; n >= depth; n--)
            added = Quote(objectPath[n]) + newline + "{" + newline + added + newline + "}";
        var index = parent?.CloseStart ?? text.Length;
        return text[..index] + newline + added + newline + text[index..];
    }

    internal static string RemoveObject(string text, string[] objectPath, string key)
    {
        var nodes = Parse(text);
        foreach (var part in objectPath)
        { var n = Unique(nodes, part); if (n is null) return text; nodes = n.Children ?? throw new BridgeException("VDF 路径不是对象。"); }
        var node = Unique(nodes, key); return node is null ? text : text[..node.Start] + text[node.End..];
    }
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
