using System.Globalization;
using System.Text;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>An element read by <see cref="WebDavXml"/>: its namespace, local name, children and text.</summary>
/// <param name="Namespace">The namespace URI the element's prefix resolves to (<c>DAV:</c>), empty when none.</param>
/// <param name="LocalName">The name without its prefix (<c>response</c> for <c>d:response</c>).</param>
public sealed record WebDavXmlElement(string Namespace, string LocalName)
{
    /// <summary>Gets the child elements, in order.</summary>
    public List<WebDavXmlElement> Children { get; } = [];

    /// <summary>Gets the element's own text (character data and CDATA, entities decoded).</summary>
    public StringBuilder Text { get; } = new();

    /// <summary>Gets the element's text and its descendants' text, as <c>XElement.Value</c> would.</summary>
    public string Value
    {
        get
        {
            if (Children.Count == 0)
            {
                return Text.ToString();
            }

            StringBuilder all = new();
            all.Append(Text);
            foreach (WebDavXmlElement child in Children)
            {
                all.Append(child.Value);
            }

            return all.ToString();
        }
    }

    /// <summary>Gets the children named <paramref name="localName"/> in <paramref name="ns"/>.</summary>
    /// <param name="ns">The namespace URI.</param>
    /// <param name="localName">The local name.</param>
    /// <returns>The matching children, in order.</returns>
    public IEnumerable<WebDavXmlElement> Elements(string ns, string localName) =>
        Children.Where(child => child.LocalName == localName && child.Namespace == ns);

    /// <summary>Gets the first child named <paramref name="localName"/> in <paramref name="ns"/>.</summary>
    /// <param name="ns">The namespace URI.</param>
    /// <param name="localName">The local name.</param>
    /// <returns>The child, or <see langword="null"/>.</returns>
    public WebDavXmlElement? Element(string ns, string localName) => Elements(ns, localName).FirstOrDefault();
}

/// <summary>
/// The small XML reader the sample uses for <c>207 Multi-Status</c> answers. The published Kazo OS
/// ships no XML library (<c>System.Xml</c> would add about 1 MB to every start), and the package
/// tool refuses a provider that calls one; so, like the OS's S3 connector, the sample reads the
/// little XML it needs itself. It resolves namespace prefixes (servers write <c>d:</c>, <c>D:</c>
/// or a default namespace), decodes entities and CDATA, skips comments and processing
/// instructions, and refuses a <c>DOCTYPE</c>: the server is not trusted (no entity expansion).
/// </summary>
public static class WebDavXml
{
    /// <summary>The deepest nesting accepted; a multistatus answer is about seven levels deep.</summary>
    public const int MaxDepth = 64;

    /// <summary>The most elements accepted in one document.</summary>
    public const int MaxElements = 2_000_000;

    /// <summary>Reads a document and returns its root element.</summary>
    /// <param name="xml">The document's text.</param>
    /// <returns>The root element.</returns>
    /// <exception cref="FormatException">The text is not well-formed XML this reader accepts.</exception>
    public static WebDavXmlElement Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return new Reader(xml).ReadDocument();
    }

    /// <summary>Decodes the five XML entities and character references (<c>&amp;#38;</c>, <c>&amp;#x26;</c>).</summary>
    /// <param name="text">Character data.</param>
    /// <returns>The decoded text.</returns>
    /// <exception cref="FormatException">An entity is unknown or a reference is not a character.</exception>
    public static string Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (c != '&')
            {
                builder.Append(c);
                index++;
                continue;
            }

            int semicolon = text.IndexOf(';', index);
            if (semicolon < 0)
            {
                throw new FormatException("The XML has an entity without ';'.");
            }

            string entity = text[(index + 1)..semicolon];
            builder.Append(entity switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                _ when entity.StartsWith("#x", StringComparison.Ordinal) => Character(entity[2..], NumberStyles.AllowHexSpecifier),
                _ when entity.StartsWith('#') => Character(entity[1..], NumberStyles.None),
                _ => throw new FormatException($"The XML has the unknown entity '&{entity};'."),
            });
            index = semicolon + 1;
        }

        return builder.ToString();
    }

    private static string Character(string digits, NumberStyles style) =>
        int.TryParse(digits, style, CultureInfo.InvariantCulture, out int code) && code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
            ? char.ConvertFromUtf32(code)
            : throw new FormatException($"The XML has the invalid character reference '{digits}'.");

    /// <summary>One pass over the text, with the open elements and their namespace scopes on a stack.</summary>
    private sealed class Reader(string xml)
    {
        private readonly Stack<OpenElement> _open = new();
        private WebDavXmlElement? _root;
        private int _index;
        private int _elements;

        public WebDavXmlElement ReadDocument()
        {
            while (_index < xml.Length)
            {
                if (xml[_index] != '<')
                {
                    ReadText();
                }
                else if (Starts("<?"))
                {
                    Skip("?>");
                }
                else if (Starts("<!--"))
                {
                    Skip("-->");
                }
                else if (Starts("<![CDATA["))
                {
                    ReadCData();
                }
                else if (Starts("<!"))
                {
                    // A DOCTYPE could declare entities that expand without bound: refused.
                    throw Malformed("a DOCTYPE, which is not accepted");
                }
                else if (Starts("</"))
                {
                    CloseElement();
                }
                else
                {
                    StartElement();
                }
            }

            return _open.Count == 0 && _root is not null ? _root : throw Malformed("an element that is not closed");
        }

        private void ReadText()
        {
            string text = ReadUntil('<');
            if (_open.Count > 0)
            {
                _open.Peek().Element.Text.Append(Decode(text));
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                throw Malformed("text outside the root element");
            }
        }

        private void ReadCData()
        {
            int start = _index + "<![CDATA[".Length;
            int end = xml.IndexOf("]]>", start, StringComparison.Ordinal);
            if (end < 0 || _open.Count == 0)
            {
                throw Malformed("a CDATA section outside an element or not closed");
            }

            _open.Peek().Element.Text.Append(xml, start, end - start);
            _index = end + "]]>".Length;
        }

        private void StartElement()
        {
            if (_open.Count == 0 && _root is not null)
            {
                throw Malformed("a second root element");
            }

            if (++_elements > MaxElements || _open.Count >= MaxDepth)
            {
                throw Malformed("too many or too deeply nested elements");
            }

            _index++;
            string name = ReadName();
            Dictionary<string, string> namespaces = _open.Count > 0
                ? new Dictionary<string, string>(_open.Peek().Namespaces, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["xml"] = "http://www.w3.org/XML/1998/namespace" };
            bool empty = ReadAttributes(namespaces);

            int colon = name.IndexOf(':', StringComparison.Ordinal);
            string prefix = colon < 0 ? string.Empty : name[..colon];
            if (!namespaces.TryGetValue(prefix, out string? ns))
            {
                ns = prefix.Length == 0 ? string.Empty : throw Malformed($"the undeclared prefix '{prefix}'");
            }

            WebDavXmlElement element = new(ns, colon < 0 ? name : name[(colon + 1)..]);
            if (_open.Count > 0)
            {
                _open.Peek().Element.Children.Add(element);
            }
            else
            {
                _root = element;
            }

            if (!empty)
            {
                _open.Push(new OpenElement(element, name, namespaces));
            }
        }

        /// <summary>Reads the attributes up to the tag's end; returns whether the tag is empty (<c>/&gt;</c>).</summary>
        private bool ReadAttributes(Dictionary<string, string> namespaces)
        {
            while (true)
            {
                SkipSpaces();
                if (_index >= xml.Length)
                {
                    throw Malformed("a start tag that is not closed");
                }

                if (Starts("/>"))
                {
                    _index += 2;
                    return true;
                }

                if (xml[_index] == '>')
                {
                    _index++;
                    return false;
                }

                string attribute = ReadName();
                SkipSpaces();
                Expect('=');
                SkipSpaces();
                char quote = _index < xml.Length ? xml[_index] : '\0';
                if (quote is not ('"' or '\''))
                {
                    throw Malformed("an attribute without quotes");
                }

                _index++;
                int end = xml.IndexOf(quote, _index);
                if (end < 0)
                {
                    throw Malformed("an attribute value that is not closed");
                }

                string value = Decode(xml[_index..end]);
                _index = end + 1;
                if (attribute == "xmlns")
                {
                    namespaces[string.Empty] = value;
                }
                else if (attribute.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    namespaces[attribute["xmlns:".Length..]] = value;
                }
            }
        }

        private void CloseElement()
        {
            _index += 2;
            string name = ReadName();
            SkipSpaces();
            Expect('>');
            if (_open.Count == 0 || _open.Peek().QualifiedName != name)
            {
                throw Malformed($"the end tag '{name}' that does not match its start tag");
            }

            _open.Pop();
        }

        private string ReadName()
        {
            int start = _index;
            while (_index < xml.Length && !char.IsWhiteSpace(xml[_index]) && xml[_index] is not ('>' or '/' or '=' or '<'))
            {
                _index++;
            }

            return _index > start ? xml[start.._index] : throw Malformed("a tag without a name");
        }

        private string ReadUntil(char stop)
        {
            int end = xml.IndexOf(stop, _index);
            if (end < 0)
            {
                end = xml.Length;
            }

            string text = xml[_index..end];
            _index = end;
            return text;
        }

        private void Skip(string terminator)
        {
            int end = xml.IndexOf(terminator, _index, StringComparison.Ordinal);
            _index = end < 0 ? throw Malformed($"a section not closed by '{terminator}'") : end + terminator.Length;
        }

        private void SkipSpaces()
        {
            while (_index < xml.Length && char.IsWhiteSpace(xml[_index]))
            {
                _index++;
            }
        }

        private void Expect(char c)
        {
            if (_index >= xml.Length || xml[_index] != c)
            {
                throw Malformed($"a missing '{c}'");
            }

            _index++;
        }

        private bool Starts(string text) => string.CompareOrdinal(xml, _index, text, 0, text.Length) == 0;

        private static FormatException Malformed(string what) => new($"The XML has {what}.");

        private sealed record OpenElement(WebDavXmlElement Element, string QualifiedName, Dictionary<string, string> Namespaces);
    }
}
