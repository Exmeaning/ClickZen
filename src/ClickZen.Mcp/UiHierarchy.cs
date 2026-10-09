using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ClickZen.Core.Geometry;

namespace ClickZen.Mcp;

/// <summary>A visible element of the Android UI hierarchy (from <c>uiautomator dump</c>).</summary>
/// <param name="Bounds">Element bounds in device pixels.</param>
public sealed record UiNode(
    int Index,
    int Depth,
    string Class,
    string? Text,
    string? ResourceId,
    string? ContentDesc,
    string? Package,
    RectI Bounds,
    bool Clickable,
    bool LongClickable,
    bool Scrollable,
    bool Checkable,
    bool Checked,
    bool Enabled,
    bool Focused,
    bool Selected,
    bool Password)
{
    public PointI Center => Bounds.Center;
}

/// <summary>
/// Parses <c>uiautomator dump</c> XML into a compact list of the nodes an AI can act on: anything with text,
/// a content description, a resource id or an interaction flag. Layout-only containers are dropped.
/// </summary>
public static partial class UiHierarchy
{
    /// <summary>Parses the dump. <paramref name="scaleX"/>/<paramref name="scaleY"/> convert bounds to the caller's coordinate space.</summary>
    public static IReadOnlyList<UiNode> Parse(string xml, double scaleX = 1, double scaleY = 1, bool includeAll = false)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0)
        {
            start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        }

        if (start < 0)
        {
            throw new McpToolException("The device did not return a UI hierarchy. Output: " + Truncate(xml.Trim(), 300));
        }

        var end = xml.LastIndexOf("</hierarchy>", StringComparison.Ordinal);
        var body = end > start ? xml[start..(end + "</hierarchy>".Length)] : xml[start..];

        var nodes = new List<UiNode>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new StringReader(body), settings);
        var depth = -1;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "node")
                {
                    depth--;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.Name != "node")
                {
                    continue;
                }

                var isEmpty = reader.IsEmptyElement;
                depth++;
                var node = ReadNode(reader, nodes.Count, depth, scaleX, scaleY);
                if (isEmpty)
                {
                    depth--;
                }

                if (node is not null && (includeAll || IsInteresting(node)))
                {
                    nodes.Add(node with { Index = nodes.Count });
                }
            }
        }
        catch (XmlException ex)
        {
            throw new McpToolException("The UI hierarchy could not be parsed: " + ex.Message, ex);
        }

        return nodes;
    }

    /// <summary>Compact one-line-per-node text (cheaper for the model than JSON).</summary>
    public static string Format(IReadOnlyList<UiNode> nodes, int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{nodes.Count} element(s). Format: [index] class \"text\" desc=… id=… flags @center (x,y) bounds [l,t][r,b]. Tap the center to activate an element.\n");
        foreach (var n in nodes.Take(Math.Max(1, maxNodes)))
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{n.Index}] ");
            sb.Append(new string(' ', Math.Min(n.Depth, 12)));
            sb.Append(ShortClass(n.Class));
            if (!string.IsNullOrEmpty(n.Text))
            {
                sb.Append(" \"").Append(Truncate(OneLine(n.Text), 120)).Append('"');
            }

            if (!string.IsNullOrEmpty(n.ContentDesc))
            {
                sb.Append(" desc=\"").Append(Truncate(OneLine(n.ContentDesc), 120)).Append('"');
            }

            if (!string.IsNullOrEmpty(n.ResourceId))
            {
                sb.Append(" id=").Append(ShortId(n.ResourceId));
            }

            var flags = Flags(n);
            if (flags.Length > 0)
            {
                sb.Append(' ').Append(flags);
            }

            var c = n.Center;
            sb.Append(CultureInfo.InvariantCulture, $" @({c.X},{c.Y}) [{n.Bounds.X},{n.Bounds.Y}][{n.Bounds.Right},{n.Bounds.Bottom}]\n");
        }

        if (nodes.Count > maxNodes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"… {nodes.Count - maxNodes} more element(s) omitted; use the filter argument to narrow down.\n");
        }

        return sb.ToString();
    }

    /// <summary>Nodes whose text, description or id contains <paramref name="filter"/> (case-insensitive).</summary>
    public static IReadOnlyList<UiNode> Filter(IReadOnlyList<UiNode> nodes, string? filter)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return nodes;
        }

        var f = filter.Trim();
        return nodes.Where(n =>
                (n.Text?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (n.ContentDesc?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (n.ResourceId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || n.Class.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Parses "[l,t][r,b]".</summary>
    public static bool TryParseBounds(string? text, out RectI rect)
    {
        rect = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var m = BoundsRegex().Match(text);
        if (!m.Success)
        {
            return false;
        }

        int P(int i) => int.Parse(m.Groups[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var (l, t, r, b) = (P(1), P(2), P(3), P(4));
        rect = new RectI(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
        return true;
    }

    private static UiNode? ReadNode(XmlReader r, int index, int depth, double sx, double sy)
    {
        if (!TryParseBounds(r.GetAttribute("bounds"), out var b))
        {
            return null;
        }

        if (sx != 1 || sy != 1)
        {
            var l = (int)Math.Round(b.X * sx);
            var t = (int)Math.Round(b.Y * sy);
            var right = (int)Math.Round(b.Right * sx);
            var bottom = (int)Math.Round(b.Bottom * sy);
            b = new RectI(l, t, Math.Max(0, right - l), Math.Max(0, bottom - t));
        }

        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
        bool Flag(string name) => string.Equals(r.GetAttribute(name), "true", StringComparison.OrdinalIgnoreCase);

        return new UiNode(
            index,
            depth,
            r.GetAttribute("class") ?? "",
            NullIfEmpty(r.GetAttribute("text")),
            NullIfEmpty(r.GetAttribute("resource-id")),
            NullIfEmpty(r.GetAttribute("content-desc")),
            NullIfEmpty(r.GetAttribute("package")),
            b,
            Flag("clickable"),
            Flag("long-clickable"),
            Flag("scrollable"),
            Flag("checkable"),
            Flag("checked"),
            r.GetAttribute("enabled") is not { } en || string.Equals(en, "true", StringComparison.OrdinalIgnoreCase),
            Flag("focused"),
            Flag("selected"),
            Flag("password"));
    }

    private static bool IsInteresting(UiNode n) =>
        !n.Bounds.IsEmpty
        && (n.Text is not null || n.ContentDesc is not null || n.Clickable || n.LongClickable || n.Scrollable || n.Checkable
            || (n.ResourceId is not null && n.Class.Contains("Edit", StringComparison.Ordinal)));

    private static string Flags(UiNode n)
    {
        var parts = new List<string>(6);
        if (n.Clickable)
        {
            parts.Add("clickable");
        }

        if (n.LongClickable)
        {
            parts.Add("long-clickable");
        }

        if (n.Scrollable)
        {
            parts.Add("scrollable");
        }

        if (n.Checkable)
        {
            parts.Add(n.Checked ? "checked" : "unchecked");
        }

        if (n.Selected)
        {
            parts.Add("selected");
        }

        if (n.Focused)
        {
            parts.Add("focused");
        }

        if (n.Password)
        {
            parts.Add("password");
        }

        if (!n.Enabled)
        {
            parts.Add("disabled");
        }

        return parts.Count == 0 ? "" : "{" + string.Join(',', parts) + "}";
    }

    private static string ShortClass(string cls)
    {
        var dot = cls.LastIndexOf('.');
        return dot >= 0 ? cls[(dot + 1)..] : cls.Length == 0 ? "View" : cls;
    }

    private static string ShortId(string id)
    {
        var slash = id.IndexOf(":id/", StringComparison.Ordinal);
        return slash >= 0 ? id[(slash + 4)..] : id;
    }

    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BoundsRegex();
}
