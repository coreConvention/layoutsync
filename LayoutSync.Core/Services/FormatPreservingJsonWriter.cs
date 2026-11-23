using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LayoutSync.Services;

/// <summary>
/// Rewrites a JSON file to hold a new document while keeping the bytes of every part the
/// change did not touch (issue #70). Re-serializing the whole document expands every block an
/// author wrote compactly, such as a <c>themes.set</c> array of one-line objects, so the diff of
/// a one-line change showed the whole file being reformatted.
/// </summary>
/// <remarks>
/// <para>
/// The original bytes are parsed into a tree of byte ranges and walked together with the old
/// and new <see cref="JsonNode"/> trees. A subtree whose value did not change is copied as it
/// was. A changed object keeps each kept member's name and separator bytes and recurses into its
/// value; a changed array aligns old and new elements (longest common subsequence on equal
/// values, then pairs the leftovers in order, so an edited element is recursed into rather than
/// replaced). Only values with no counterpart in the original are serialized, in the style of
/// their surroundings: on one line inside a one-line container or beside one-line siblings of
/// the same kind, otherwise expanded at the indentation of the lines around them.
/// </para>
/// <para>
/// The result is parsed again and must equal the new document; otherwise
/// <see cref="Rewrite"/> returns <c>null</c> and the caller writes nothing. A leading UTF-8 BOM,
/// the line ending (CRLF or LF) and whatever follows the root value are kept.
/// </para>
/// </remarks>
internal static class FormatPreservingJsonWriter
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Scalars and property names, written literally as every LayoutSync write is (#38).</summary>
    private static readonly JsonSerializerOptions ScalarOptions = new() { Encoder = LiteralJsonEncoder.Instance };

    /// <summary>
    /// Returns <paramref name="original"/> changed to hold <paramref name="updated"/>, touching
    /// only the bytes of values that differ. Returns <c>null</c> when the original is not a
    /// single JSON value, or the result would not parse to exactly <paramref name="updated"/>.
    /// </summary>
    public static byte[]? Rewrite(byte[] original, JsonNode updated)
    {
        int offset = original.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

        Node root;
        JsonNode? oldRoot;
        try
        {
            Utf8JsonReader reader = new(original.AsSpan(offset));
            if (!reader.Read()) return null;
            root = Parse(ref reader, offset);
            // Past the root value: anything but trailing whitespace makes Read() throw.
            if (reader.Read()) return null;
            oldRoot = JsonNode.Parse(original.AsSpan(offset));
        }
        catch (JsonException)
        {
            return null;
        }

        byte[] result;
        try
        {
            Emitter emitter = new(original, root);
            result = [.. original[..root.Start], .. emitter.Emit(root, oldRoot, updated, parentInline: false), .. original[root.End..]];
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(result.AsSpan(offset)), updated) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ───── the original's byte ranges ─────

    /// <summary>A value in the original, as the absolute byte range <c>[Start, End)</c>.</summary>
    private abstract record Node(int Start, int End);

    private sealed record ScalarNode(int Start, int End) : Node(Start, End);

    /// <param name="NameEnd">The byte after the closing quote of the name.</param>
    private sealed record Member(string Name, int NameStart, int NameEnd, Node Value);

    private sealed record ObjectNode(int Start, int End, List<Member> Members) : Node(Start, End);

    private sealed record ArrayNode(int Start, int End, List<Node> Items) : Node(Start, End);

    /// <summary>
    /// Reads the value the reader is positioned on. Offsets are shifted by
    /// <paramref name="offset"/> (the BOM) so they index the original buffer. Duplicate
    /// property names are refused: a byte range could not say which one the document kept.
    /// </summary>
    private static Node Parse(ref Utf8JsonReader reader, int offset)
    {
        int start = offset + (int)reader.TokenStartIndex;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                List<Member> members = [];
                HashSet<string> names = [];
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!;
                    if (!names.Add(name)) throw new JsonException($"Duplicate property '{name}'.");
                    int nameStart = offset + (int)reader.TokenStartIndex;
                    // ValueSpan is the raw (still escaped) text between the quotes.
                    int nameEnd = nameStart + reader.ValueSpan.Length + 2;
                    reader.Read();
                    members.Add(new Member(name, nameStart, nameEnd, Parse(ref reader, offset)));
                }
                return new ObjectNode(start, offset + (int)reader.TokenStartIndex + 1, members);

            case JsonTokenType.StartArray:
                List<Node> items = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    items.Add(Parse(ref reader, offset));
                return new ArrayNode(start, offset + (int)reader.TokenStartIndex + 1, items);

            default:
                return new ScalarNode(start, offset + (int)reader.BytesConsumed);
        }
    }

    // ───── writing ─────

    /// <summary>How a one-line container is written: <c>{ "a": 1, "b": 2 }</c> or <c>{"a":1,"b":2}</c>.</summary>
    private sealed record InlineStyle(bool Pad, byte[] Colon, byte[] Comma);

    /// <summary>A value of the new container: an original item (by index) or a new one.</summary>
    private sealed record OutItem(int Original, byte[] Bytes);

    private sealed class Emitter
    {
        private readonly byte[] _source;
        private readonly byte[] _newLine;
        private readonly string _indentUnit;
        private readonly byte[] _colon;
        private readonly InlineStyle _objectStyle;
        private readonly InlineStyle _arrayStyle;

        /// <summary>Detects the document's conventions, with the old writer's as defaults.</summary>
        public Emitter(byte[] source, Node root)
        {
            _source = source;
            _newLine = source.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n"u8.ToArray() : "\n"u8.ToArray();

            ObjectNode? firstExpanded = null;
            ObjectNode? firstInlineObject = null;
            ArrayNode? firstInlineArray = null;
            foreach (Node node in DocumentOrder(root))
            {
                switch (node)
                {
                    case ObjectNode o when o.Members.Count > 0 && IsSingleLine(o):
                        firstInlineObject ??= o;
                        break;
                    case ObjectNode o when o.Members.Count > 0:
                        firstExpanded ??= o;
                        break;
                    case ArrayNode a when a.Items.Count > 0 && IsSingleLine(a):
                        firstInlineArray ??= a;
                        break;
                }
            }

            string rootMemberIndent = root is ObjectNode { Members.Count: > 0 } r && !IsSingleLine(r)
                ? LineIndent(r.Members[0].NameStart)
                : string.Empty;
            _indentUnit = rootMemberIndent.Length > 0 ? rootMemberIndent : "  ";
            _colon = firstExpanded is null ? ": "u8.ToArray() : ColonOf(firstExpanded);
            _objectStyle = firstInlineObject is null
                ? new InlineStyle(true, ": "u8.ToArray(), ", "u8.ToArray())
                : StyleOf(firstInlineObject);
            _arrayStyle = firstInlineArray is null
                ? new InlineStyle(false, [], ", "u8.ToArray())
                : StyleOf(firstInlineArray);
        }

        /// <summary>
        /// The bytes that replace <paramref name="node"/>'s range so it holds
        /// <paramref name="updated"/>. <paramref name="parentInline"/> says whether the
        /// container around it is written on one line.
        /// </summary>
        public byte[] Emit(Node node, JsonNode? old, JsonNode? updated, bool parentInline)
        {
            if (JsonNode.DeepEquals(old, updated))
                return _source[node.Start..node.End];

            return (node, old, updated) switch
            {
                (ObjectNode o, JsonObject oldObject, JsonObject newObject) => EmitObject(o, oldObject, newObject, parentInline),
                (ArrayNode a, JsonArray oldArray, JsonArray newArray) => EmitArray(a, oldArray, newArray, parentInline),
                _ => Serialize(updated, parentInline, LineIndent(node.Start), null),
            };
        }

        private byte[] EmitObject(ObjectNode node, JsonObject old, JsonObject updated, bool parentInline)
        {
            bool inline = node.Members.Count > 0 ? IsSingleLine(node) : parentInline;
            string indent = ItemIndent(node, node.Members.Select(m => m.NameStart), inline);
            byte[] colon = node.Members.Count > 0 ? _source[node.Members[0].NameEnd..node.Members[0].Value.Start]
                : inline ? _objectStyle.Colon : _colon;

            Dictionary<string, int> byName = [];
            for (int i = 0; i < node.Members.Count; i++)
                byName[node.Members[i].Name] = i;

            List<OutItem> items = [];
            foreach ((string name, JsonNode? value) in updated)
            {
                if (byName.TryGetValue(name, out int i))
                {
                    Member member = node.Members[i];
                    items.Add(new OutItem(i, [.. _source[member.NameStart..member.Value.Start], .. Emit(member.Value, old[name], value, inline)]));
                }
                else
                {
                    items.Add(new OutItem(-1, [.. JsonSerializer.SerializeToUtf8Bytes(name, ScalarOptions), .. colon, .. Serialize(value, inline, indent, null)]));
                }
            }

            return Assemble(node, '{', '}', [.. node.Members.Select(m => (m.NameStart, m.Value.End))], items, inline, indent, _objectStyle);
        }

        private byte[] EmitArray(ArrayNode node, JsonArray old, JsonArray updated, bool parentInline)
        {
            bool inline = node.Items.Count > 0 ? IsSingleLine(node) : parentInline;
            string indent = ItemIndent(node, node.Items.Select(item => item.Start), inline);
            int[] originalOf = Align(old, updated);

            List<OutItem> items = [];
            int previousOriginal = -1;
            for (int j = 0; j < updated.Count; j++)
            {
                int i = originalOf[j];
                if (i >= 0)
                {
                    items.Add(new OutItem(i, Emit(node.Items[i], old[i], updated[j], inline)));
                    previousOriginal = i;
                    continue;
                }

                // Write a new element like the original it follows (or the first one): a list of
                // one-line objects gets another one-line object.
                Node? sibling = node.Items.Count == 0 ? null : node.Items[Math.Max(previousOriginal, 0)];
                InlineStyle? siblingStyle = (sibling, updated[j]) switch
                {
                    (ObjectNode { Members.Count: > 0 } o, JsonObject) when IsSingleLine(o) => StyleOf(o),
                    (ArrayNode { Items.Count: > 0 } a, JsonArray) when IsSingleLine(a) => StyleOf(a),
                    _ => null,
                };
                items.Add(new OutItem(-1, Serialize(updated[j], inline || siblingStyle is not null, indent, siblingStyle)));
            }

            return Assemble(node, '[', ']', [.. node.Items.Select(item => (item.Start, item.End))], items, inline, indent, _arrayStyle);
        }

        /// <summary>
        /// Joins the items between the brackets, reusing the original's whitespace: its leading
        /// and trailing gaps, and each kept item's original separator. A new item is separated
        /// like the original's first pair.
        /// </summary>
        private byte[] Assemble(
            Node node,
            char open,
            char close,
            List<(int Start, int End)> spans,
            List<OutItem> items,
            bool inline,
            string indent,
            InlineStyle style)
        {
            List<byte> output = [(byte)open];
            if (items.Count > 0)
            {
                byte[] leading, trailing, newSeparator;
                if (spans.Count > 0)
                {
                    leading = _source[(node.Start + 1)..spans[0].Start];
                    trailing = _source[spans[^1].End..(node.End - 1)];
                    // With one item there is no separator to copy: a one-line container takes the
                    // document's (`["a"]` grows to `["a", "b"]`), an expanded one breaks the line
                    // the way its first item is broken from the bracket.
                    newSeparator = spans.Count > 1 ? _source[spans[0].End..spans[1].Start]
                        : inline ? style.Comma
                        : [(byte)',', .. leading];
                }
                else if (inline)
                {
                    leading = trailing = style.Pad ? " "u8.ToArray() : [];
                    newSeparator = style.Comma;
                }
                else
                {
                    leading = [.. _newLine, .. Encoding.UTF8.GetBytes(indent)];
                    trailing = [.. _newLine, .. Encoding.UTF8.GetBytes(LineIndent(node.Start))];
                    newSeparator = [(byte)',', .. leading];
                }

                for (int k = 0; k < items.Count; k++)
                {
                    int i = items[k].Original;
                    if (k == 0) output.AddRange(leading);
                    else if (i > 0) output.AddRange(_source[spans[i - 1].End..spans[i].Start]);
                    else output.AddRange(newSeparator);
                    output.AddRange(items[k].Bytes);
                }
                output.AddRange(trailing);
            }
            output.Add((byte)close);
            return [.. output];
        }

        /// <summary>
        /// Writes a value that has no counterpart in the original: on one line, in
        /// <paramref name="hint"/>'s style or the document's, or expanded at
        /// <paramref name="indent"/> (the indentation of the line it starts on).
        /// </summary>
        private byte[] Serialize(JsonNode? value, bool inline, string indent, InlineStyle? hint)
        {
            List<byte> output = [];
            if (inline)
                WriteInline(value, hint, output);
            else
                WriteExpanded(value, indent, output);
            return [.. output];
        }

        private void WriteInline(JsonNode? value, InlineStyle? hint, List<byte> output)
        {
            switch (value)
            {
                case JsonObject { Count: > 0 } obj:
                    // An object style has a colon; an array style (from an array sibling) does not.
                    InlineStyle objectStyle = hint is { Colon.Length: > 0 } ? hint : _objectStyle;
                    output.Add((byte)'{');
                    if (objectStyle.Pad) output.Add((byte)' ');
                    bool first = true;
                    foreach ((string name, JsonNode? member) in obj)
                    {
                        if (!first) output.AddRange(objectStyle.Comma);
                        first = false;
                        output.AddRange(JsonSerializer.SerializeToUtf8Bytes(name, ScalarOptions));
                        output.AddRange(objectStyle.Colon);
                        WriteInline(member, null, output);
                    }
                    if (objectStyle.Pad) output.Add((byte)' ');
                    output.Add((byte)'}');
                    break;

                case JsonArray { Count: > 0 } array:
                    InlineStyle arrayStyle = hint is { Colon.Length: 0 } ? hint : _arrayStyle;
                    output.Add((byte)'[');
                    if (arrayStyle.Pad) output.Add((byte)' ');
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (i > 0) output.AddRange(arrayStyle.Comma);
                        WriteInline(array[i], null, output);
                    }
                    if (arrayStyle.Pad) output.Add((byte)' ');
                    output.Add((byte)']');
                    break;

                default:
                    WriteScalarOrEmpty(value, output);
                    break;
            }
        }

        private void WriteExpanded(JsonNode? value, string indent, List<byte> output)
        {
            string inner = indent + _indentUnit;
            switch (value)
            {
                case JsonObject { Count: > 0 } obj:
                    output.Add((byte)'{');
                    bool first = true;
                    foreach ((string name, JsonNode? member) in obj)
                    {
                        if (!first) output.Add((byte)',');
                        first = false;
                        output.AddRange(_newLine);
                        output.AddRange(Encoding.UTF8.GetBytes(inner));
                        output.AddRange(JsonSerializer.SerializeToUtf8Bytes(name, ScalarOptions));
                        output.AddRange(_colon);
                        WriteExpanded(member, inner, output);
                    }
                    output.AddRange(_newLine);
                    output.AddRange(Encoding.UTF8.GetBytes(indent));
                    output.Add((byte)'}');
                    break;

                case JsonArray { Count: > 0 } array:
                    output.Add((byte)'[');
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (i > 0) output.Add((byte)',');
                        output.AddRange(_newLine);
                        output.AddRange(Encoding.UTF8.GetBytes(inner));
                        WriteExpanded(array[i], inner, output);
                    }
                    output.AddRange(_newLine);
                    output.AddRange(Encoding.UTF8.GetBytes(indent));
                    output.Add((byte)']');
                    break;

                default:
                    WriteScalarOrEmpty(value, output);
                    break;
            }
        }

        private static void WriteScalarOrEmpty(JsonNode? value, List<byte> output)
        {
            switch (value)
            {
                case null: output.AddRange("null"u8); break;
                case JsonObject: output.AddRange("{}"u8); break;
                case JsonArray: output.AddRange("[]"u8); break;
                default: output.AddRange(Encoding.UTF8.GetBytes(value.ToJsonString(ScalarOptions))); break;
            }
        }

        // ───── layout detection ─────

        private bool IsSingleLine(Node node) => _source.AsSpan(node.Start, node.End - node.Start).IndexOf((byte)'\n') < 0;

        /// <summary>The spaces and tabs that start the line holding byte <paramref name="position"/>.</summary>
        private string LineIndent(int position)
        {
            int lineStart = _source.AsSpan(0, position).LastIndexOf((byte)'\n') + 1;
            int end = lineStart;
            while (end < position && _source[end] is (byte)' ' or (byte)'\t') end++;
            return Encoding.UTF8.GetString(_source, lineStart, end - lineStart);
        }

        /// <summary>
        /// Where a new item of an expanded container starts its line: where the first item does,
        /// or one indent step past the container's own line when it is empty.
        /// </summary>
        private string ItemIndent(Node node, IEnumerable<int> itemStarts, bool inline)
        {
            if (inline) return string.Empty;
            foreach (int start in itemStarts)
                return LineIndent(start);
            return LineIndent(node.Start) + _indentUnit;
        }

        private byte[] ColonOf(ObjectNode node) => _source[node.Members[0].NameEnd..node.Members[0].Value.Start];

        private InlineStyle StyleOf(ObjectNode node)
        {
            byte[] colon = ColonOf(node);
            byte[] comma = node.Members.Count > 1
                ? _source[node.Members[0].Value.End..node.Members[1].NameStart]
                : colon.AsSpan().IndexOf((byte)' ') >= 0 ? ", "u8.ToArray() : ","u8.ToArray();
            return new InlineStyle(_source[node.Start + 1] == (byte)' ', colon, comma);
        }

        private InlineStyle StyleOf(ArrayNode node)
        {
            byte[] comma = node.Items.Count > 1 ? _source[node.Items[0].End..node.Items[1].Start] : ", "u8.ToArray();
            return new InlineStyle(_source[node.Start + 1] == (byte)' ', [], comma);
        }

        private static IEnumerable<Node> DocumentOrder(Node node)
        {
            yield return node;
            IEnumerable<Node> children = node switch
            {
                ObjectNode o => o.Members.Select(m => m.Value),
                ArrayNode a => a.Items,
                _ => [],
            };
            foreach (Node child in children)
                foreach (Node descendant in DocumentOrder(child))
                    yield return descendant;
        }
    }

    /// <summary>
    /// For each element of <paramref name="updated"/>, the index of the original element it
    /// continues, or -1 for a new one. Equal elements are matched by longest common
    /// subsequence; between two matches, leftover old and new elements are paired in order (an
    /// edit), and what remains is a removal or an insertion.
    /// </summary>
    private static int[] Align(JsonArray old, JsonArray updated)
    {
        int n = old.Count, m = updated.Count;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = JsonNode.DeepEquals(old[i], updated[j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int[] originalOf = new int[m];
        Array.Fill(originalOf, -1);
        List<(int Old, int New)> matches = [];
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (JsonNode.DeepEquals(old[i], updated[j]) && lcs[i, j] == lcs[i + 1, j + 1] + 1)
            {
                matches.Add((i, j));
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }
        matches.Add((n, m)); // sentinel closing the last gap

        int oldFrom = 0, newFrom = 0;
        foreach ((int oldMatch, int newMatch) in matches)
        {
            for (int k = 0; oldFrom + k < oldMatch && newFrom + k < newMatch; k++)
                originalOf[newFrom + k] = oldFrom + k;
            if (newMatch < m) originalOf[newMatch] = oldMatch;
            (oldFrom, newFrom) = (oldMatch + 1, newMatch + 1);
        }
        return originalOf;
    }
}
