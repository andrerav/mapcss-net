using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using MapCss.Parser;

namespace MapCss.Styling;

internal static class MapCssParserFacade
{
	public static MapCssStylesheet Parse(string mapCss)
	{
		var inputStream = new AntlrInputStream(mapCss);
		var lexer = new MapCssLexer(inputStream);
		lexer.RemoveErrorListeners();
		lexer.AddErrorListener(new ThrowingLexerErrorListener());

		var tokens = new CommonTokenStream(lexer);
		var parser = new MapCssParser(tokens);
		parser.RemoveErrorListeners();
		parser.AddErrorListener(new ThrowingParserErrorListener());

		var tree = parser.stylesheet();
		return MapCssAstBuilder.Build(tree, tokens);
	}
}

internal sealed class ThrowingLexerErrorListener : IAntlrErrorListener<int>
{
	public void SyntaxError(
		TextWriter output,
		IRecognizer recognizer,
		int offendingSymbol,
		int line,
		int charPositionInLine,
		string msg,
		RecognitionException e)
	{
		var where = $"{line}:{charPositionInLine}";
		throw new InvalidOperationException($"Lex error at {where}: {msg}", e);
	}
}

internal sealed class ThrowingParserErrorListener : BaseErrorListener
{
	public override void SyntaxError(
		TextWriter output,
		IRecognizer recognizer,
		IToken offendingSymbol,
		int line,
		int charPositionInLine,
		string msg,
		RecognitionException e)
	{
		var where = $"{line}:{charPositionInLine}";
		throw new InvalidOperationException($"Parse error at {where}: {msg}", e);
	}
}

internal static class MapCssAstBuilder
{
	public static MapCssStylesheet Build(MapCssParser.StylesheetContext context, CommonTokenStream tokens)
	{
		var rules = new List<MapCssRuleSet>();
		var meta = new Dictionary<string, IReadOnlyList<MapCssValue>>(StringComparer.OrdinalIgnoreCase);

		foreach (var statement in context.statement())
		{
			if (statement.metaBlock() is { } metaBlock)
			{
				ParseMetaBlock(metaBlock, tokens, meta);
				continue;
			}

			if (statement.ruleSet() is { } ruleSet)
			{
				rules.Add(ParseRuleSet(ruleSet, tokens));
			}
		}

		return new MapCssStylesheet(rules, meta);
	}

	private static void ParseMetaBlock(
		MapCssParser.MetaBlockContext metaBlock,
		CommonTokenStream tokens,
		Dictionary<string, IReadOnlyList<MapCssValue>> meta)
	{
		if (metaBlock.block() is not { } block)
		{
			return;
		}

		foreach (var declaration in ParseDeclarations(block, tokens))
		{
			if (declaration is MapCssPropertyDeclaration property)
			{
				meta[property.Name] = property.Values;
			}
		}
	}

	private static MapCssRuleSet ParseRuleSet(MapCssParser.RuleSetContext ruleSet, CommonTokenStream tokens)
	{
		var selectors = new List<MapCssSelector>();
		foreach (var selector in ruleSet.selectorGroup().selector())
		{
			selectors.Add(ParseSelector(selector, tokens));
		}

		var declarations = ParseDeclarations(ruleSet.block(), tokens);
		return new MapCssRuleSet(selectors, declarations);
	}

	private static List<MapCssDeclaration> ParseDeclarations(MapCssParser.BlockContext block, CommonTokenStream tokens)
	{
		var declarations = new List<MapCssDeclaration>();
		foreach (var declaration in block.declaration())
		{
			if (declaration.setStatement() is { } setStatement)
			{
				declarations.Add(ParseSetStatement(setStatement));
				continue;
			}

			if (declaration.propertyDeclaration() is { } propertyDeclaration)
			{
				declarations.Add(ParsePropertyDeclaration(propertyDeclaration, tokens));
			}
		}

		return declarations;
	}

	private static MapCssSetDeclaration ParseSetStatement(MapCssParser.SetStatementContext setStatement)
	{
		var classes = new List<string>();
		foreach (var item in setStatement.setItem())
		{
			var ident = item.IDENT();
			if (ident is null)
			{
				continue;
			}

			classes.Add(ident.GetText());
		}

		return new MapCssSetDeclaration(classes);
	}

	private static MapCssPropertyDeclaration ParsePropertyDeclaration(
		MapCssParser.PropertyDeclarationContext declaration,
		CommonTokenStream tokens)
	{
		var name = declaration.propertyName().GetText();
		var values = new List<MapCssValue>();
		foreach (var expr in declaration.valueList().expr())
		{
			values.Add(new MapCssValue(GetText(tokens, expr)));
		}

		return new MapCssPropertyDeclaration(name, values);
	}

	/// <summary>
	/// Parses an ANTLR <c>SelectorContext</c> into a <see cref="MapCssSelector"/>.
	/// </summary>
	/// <remarks>
	/// This method converts the parsed selector chain into a sequence of <see cref="MapCssSelectorSegment"/>s:
	/// it parses each simple selector, determines the combinators (child vs descendant) between them,
	/// merges link-only selector segments where appropriate (so attribute-only selectors used for linking
	/// become "link filters" on the following segment), and finally determines an optional subpart
	/// identifier (the last subpart token if present). The produced <see cref="MapCssSelector"/> is
	/// the canonical AST representation used by the style engine for matching.
	/// </remarks>
	/// <param name="selector">The ANTLR selector parse tree.</param>
	/// <param name="tokens">Token stream used when raw text is required (e.g. attribute literals).</param>
	/// <returns>A <see cref="MapCssSelector"/> representing the selector.</returns>
	private static MapCssSelector ParseSelector(MapCssParser.SelectorContext selector, CommonTokenStream tokens)
	{
		var chain = selector.selectorChain();
		var selectors = new List<MapCssSimpleSelector>();
		foreach (var simpleSelector in chain.simpleSelector())
		{
			selectors.Add(ParseSimpleSelector(simpleSelector, tokens));
		}

		var combinators = ParseCombinators(chain, selectors, tokens);
		var segments = new List<MapCssSelectorSegment>();
		for (var i = 0; i < selectors.Count; i++)
		{
			var combinator = i == 0 ? MapCssCombinator.None : combinators[i - 1];
			segments.Add(new MapCssSelectorSegment(selectors[i], combinator, Array.Empty<MapCssAttributeTest>()));
		}

		segments = MergeLinkSelectors(segments);

		string? subpart = null;
		foreach (var segment in segments)
		{
			if (segment.Selector.Subparts.Count > 0)
			{
				subpart = segment.Selector.Subparts[^1];
			}
		}

		return new MapCssSelector(segments, subpart);
	}

	/// <summary>
	/// Determine combinators between adjacent simple selectors in a selector chain.
	/// </summary>
	/// <remarks>
	/// For each adjacent pair of simple selectors, the method inspects the token stream for a
	/// literal '>' (GT) token between the two nodes. If present, the relationship is a
	/// <see cref="MapCssCombinator.Child"/>, otherwise it is a
	/// <see cref="MapCssCombinator.Descendant"/>. The returned list has length
	/// <c>selectors.Count - 1</c> and preserves order left-to-right.
	/// </remarks>
	/// <param name="chain">The selector chain parse context.</param>
	/// <param name="selectors">Already-parsed simple selectors for the chain.</param>
	/// <param name="tokens">Token stream used to inspect raw tokens between nodes.</param>
	/// <returns>List of combinators between adjacent selectors.</returns>
	private static List<MapCssCombinator> ParseCombinators(
		MapCssParser.SelectorChainContext chain,
		IReadOnlyList<MapCssSimpleSelector> selectors,
		CommonTokenStream tokens)
	{
		var simpleSelectors = chain.simpleSelector();
		var combinators = new List<MapCssCombinator>();
		for (var i = 0; i < selectors.Count - 1; i++)
		{
			var left = simpleSelectors[i];
			var right = simpleSelectors[i + 1];
			var hasGt = HasTokenBetween(tokens, left.Stop.TokenIndex, right.Start.TokenIndex, MapCssLexer.GT);
			combinators.Add(hasGt ? MapCssCombinator.Child : MapCssCombinator.Descendant);
		}

		return combinators;
	}

	/// <summary>
	/// Scans tokens between two token indices for the presence of a specific token type.
	/// </summary>
	/// <remarks>
	/// This low-level helper is used when AST construction needs to inspect raw source tokens
	/// (for example, to distinguish a child combinator '>' from a whitespace descendant combinator).
	/// The scan excludes the start and end token positions themselves and inspects tokens in the open interval.
	/// </remarks>
	private static bool HasTokenBetween(CommonTokenStream tokens, int startTokenIndex, int endTokenIndex, int tokenType)
	{
		for (var i = startTokenIndex + 1; i < endTokenIndex; i++)
		{
			var token = tokens.Get(i);
			if (token.Type == tokenType)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Merge "link-only" selector segments into their following segment when appropriate.
	/// </summary>
	/// <remarks>
	/// MapCSS allows selectors that consist solely of attribute filters (for example, filters
	/// applied to a link between elements). In the internal selector representation these
	/// behave as "link filters" and are more conveniently represented by attaching the
	/// attribute tests to the following selector segment. This method performs that transformation
	/// under conservative conditions (child combinator to the link-only segment and descendant from the
	/// following segment) and avoids merging when the next segment already has link filters.
	/// </remarks>
	/// <param name="segments">List of selector segments built from left to right.</param>
	/// <returns>The transformed list with some link-only segments merged.</returns>
	private static List<MapCssSelectorSegment> MergeLinkSelectors(List<MapCssSelectorSegment> segments)
	{
		for (var i = 1; i < segments.Count - 1; i++)
		{
			var current = segments[i];
			if (!IsLinkOnly(current.Selector))
			{
				continue;
			}

			if (current.CombinatorToPrevious != MapCssCombinator.Child)
			{
				continue;
			}

			var next = segments[i + 1];
			if (next.CombinatorToPrevious != MapCssCombinator.Descendant || next.LinkFiltersToPrevious.Count > 0)
			{
				continue;
			}

			var merged = new MapCssSelectorSegment(
				next.Selector,
				MapCssCombinator.Child,
				current.Selector.AttributeTests);

			segments[i + 1] = merged;
			segments.RemoveAt(i);
			i--;
		}

		return segments;
	}

	/// <summary>
	/// Determines whether a simple selector contains only attribute tests and no other qualifiers.
	/// </summary>
	/// <remarks>
	/// A "link-only" selector is used to express filters on a link between elements (e.g. [oneway])
	/// and has no element, class, pseudo-class, zoom range or subpart. It must have at least one attribute test.
	/// </remarks>
	private static bool IsLinkOnly(MapCssSimpleSelector selector)
	{
		return selector.ElementType is null
			&& selector.ZoomRanges.Count == 0
			&& selector.Classes.Count == 0
			&& selector.PseudoClasses.Count == 0
			&& selector.Subparts.Count == 0
			&& selector.AttributeTests.Count > 0;
	}

	/// <summary>
	/// Parses a single simple selector (the building block of a full selector) from the parse tree.
	/// </summary>
	/// <remarks>
	/// A simple selector can contain:
	/// - an element type (node/way/relation/area/canvas/*),
	/// - zero or more zoom ranges (z<min>-<max>),
	/// - class selectors, pseudo-classes, subpart identifiers, and attribute tests.
	/// This method extracts each of these parts and returns a compact <see cref="MapCssSimpleSelector"/>
	/// instance that is later assembled into full selector segments.
	/// </remarks>
	/// <param name="context">The ANTLR <c>SimpleSelectorContext</c> to parse.</param>
	/// <param name="tokens">Token stream used to obtain raw text for some literal values.</param>
	/// <returns>A <see cref="MapCssSimpleSelector"/> with parsed components.</returns>
	private static MapCssSimpleSelector ParseSimpleSelector(
		MapCssParser.SimpleSelectorContext context,
		CommonTokenStream tokens)
	{
		MapCssElementType? elementType = null;
		if (context.element() is { } elementContext)
		{
			elementType = ParseElementType(elementContext);
		}

		var zoomRanges = new List<MapCssZoomRange>();
		var classes = new List<string>();
		var pseudoClasses = new List<string>();
		var subparts = new List<string>();
		var attributeTests = new List<MapCssAttributeTest>();

		foreach (var atom in context.selectorAtom())
		{
			if (atom.zoomSpec() is { } zoomSpec)
			{
				zoomRanges.Add(ParseZoomRange(zoomSpec));
				continue;
			}

			if (atom.selectorSuffix() is { } suffix)
			{
				if (suffix.classSelector() is { } classSelector)
				{
					classes.Add(classSelector.IDENT().GetText());
				}
				else if (suffix.subPart() is { } subPart)
				{
					subparts.Add(subPart.IDENT().GetText());
				}
				else if (suffix.pseudoClass() is { } pseudo)
				{
					pseudoClasses.Add(pseudo.IDENT().GetText());
				}

				continue;
			}

			if (atom.attributeFilter() is { } attributeFilter)
			{
				attributeTests.Add(ParseAttributeTest(attributeFilter.attributeTest(), tokens));
			}
		}

		return new MapCssSimpleSelector(
			elementType,
			zoomRanges,
			classes,
			pseudoClasses,
			subparts,
			attributeTests);
	}

	private static MapCssElementType ParseElementType(MapCssParser.ElementContext element)
	{
		return element.GetText() switch
		{
			"node" => MapCssElementType.Node,
			"way" => MapCssElementType.Way,
			"relation" => MapCssElementType.Relation,
			"area" => MapCssElementType.Area,
			"canvas" => MapCssElementType.Canvas,
			"*" => MapCssElementType.Any,
			_ => MapCssElementType.Any
		};
	}

	/// <summary>
	/// Parse a zoom range literal of the form <c>z&lt;min&gt;-&lt;max&gt;</c> or <c>z&lt;min&gt;</c>.
	/// </summary>
	/// <remarks>
	/// The method expects the token text starting with a leading 'z'. If parsing fails it defaults
	/// the minimum to 0. The maximum is optional and parsed when present.
	/// </remarks>
	/// <param name="zoomSpec">The zoom specification parse context.</param>
	/// <returns>A <see cref="MapCssZoomRange"/> representing the parsed min and optional max zoom.</returns>
	private static MapCssZoomRange ParseZoomRange(MapCssParser.ZoomSpecContext zoomSpec)
	{
		var text = zoomSpec.ZOOMRANGE().GetText();
		if (!text.StartsWith('z'))
		{
			return new MapCssZoomRange(0, null);
		}

		var body = text[1..];
		var parts = body.Split('-', 2);
		if (!int.TryParse(parts[0], out var min))
		{
			min = 0;
		}

		int? max = null;
		if (parts.Length == 2 && parts[1].Length > 0 && int.TryParse(parts[1], out var parsedMax))
		{
			max = parsedMax;
		}

		return new MapCssZoomRange(min, max);
	}

	/// <summary>
	/// Convert a parsed attribute-test node into a <see cref="MapCssAttributeTest"/> value.
	/// </summary>
	/// <remarks>
	/// An attribute test can include a key (possibly quoted), an existence modifier (! for not truthy),
	/// an operator (such as =, ~=, ^=, etc.) and a value (which may be a string, number, color, boolean
	/// literal, bareword or regex). This method orchestrates parsing each subpart and bundles them into
	/// a single <see cref="MapCssAttributeTest"/> value used later during selector matching.
	/// </remarks>
	/// <param name="context">The attribute test parse context.</param>
	/// <param name="tokens">Tokens used to read literal text where needed.</param>
	/// <returns>A <see cref="MapCssAttributeTest"/> describing the test.</returns>
	private static MapCssAttributeTest ParseAttributeTest(
		MapCssParser.AttributeTestContext context,
		CommonTokenStream tokens)
	{
		var negate = context.BANG() is not null;
		var key = ParseAttrKey(context.attrKey(), tokens);
		var existence = ParseAttrExistence(context.attrExistence());
		var op = ParseAttrOp(context.attrOp());
		var value = ParseAttrValue(context.attrValue(), tokens);

		return new MapCssAttributeTest(key, existence, op, value, negate);
	}

	private static string ParseAttrKey(MapCssParser.AttrKeyContext context, CommonTokenStream tokens)
	{
		var text = GetText(tokens, context);
		if (text.Length >= 2 && (text[0] == '"' || text[0] == '\''))
		{
			return UnescapeString(text);
		}

		return text;
	}

	private static MapCssAttributeExistence? ParseAttrExistence(MapCssParser.AttrExistenceContext? context)
	{
		if (context is null)
		{
			return null;
		}

		return context.BANG() is null
			? MapCssAttributeExistence.Truthy
			: MapCssAttributeExistence.NotTruthy;
	}

	private static MapCssAttributeOperator? ParseAttrOp(MapCssParser.AttrOpContext? context)
	{
		if (context is null)
		{
			return null;
		}

		return context.GetText() switch
		{
			"=" => MapCssAttributeOperator.Eq,
			"!=" => MapCssAttributeOperator.NotEq,
			"*=" => MapCssAttributeOperator.Contains,
			"^=" => MapCssAttributeOperator.Prefix,
			"$=" => MapCssAttributeOperator.Suffix,
			"~=" => MapCssAttributeOperator.Match,
			"!~=" => MapCssAttributeOperator.NMatch,
			"=~" => MapCssAttributeOperator.RegexMatch,
			"!~" => MapCssAttributeOperator.RegexNMatch,
			_ => null
		};
	}

	/// <summary>
	/// Parse an attribute test value into a <see cref="MapCssAttributeValue"/>.
	/// </summary>
	/// <remarks>
	/// The value may be one of:
	/// - a regular expression literal (e.g. /pattern/),
	/// - a quoted string, number, hex color, boolean literal, or a bare word.
	/// The method normalizes and unescapes the literal text and, for regex values, attempts to compile
	/// the pattern returning a <see cref="Regex"/> when compilation succeeds (otherwise <c>null</c>).
	/// </remarks>
	/// <param name="context">The attribute value parse context, or <c>null</c> for absent values.</param>
	/// <param name="tokens">Token stream used to read literal text for non-regex values.</param>
	/// <returns>A <see cref="MapCssAttributeValue"/> or <c>null</c> when no value is present.</returns>
	private static MapCssAttributeValue? ParseAttrValue(
		MapCssParser.AttrValueContext? context,
		CommonTokenStream tokens)
	{
		if (context is null)
		{
			return null;
		}

		if (context.REGEX() is { } regexToken)
		{
			var pattern = UnescapeRegex(regexToken.GetText());
			return new MapCssAttributeValue(pattern, MapCssValueKind.Regex, TryCompileRegex(pattern));
		}

		if (context.literal() is { } literal)
		{
			if (literal.literalString() is { } literalString)
			{
				var text = UnescapeString(GetText(tokens, literalString));
				return new MapCssAttributeValue(text, MapCssValueKind.String, null);
			}

			if (literal.NUMBER() is { } numberToken)
			{
				return new MapCssAttributeValue(numberToken.GetText(), MapCssValueKind.Number, null);
			}

			if (literal.HEXCOLOR() is { } colorToken)
			{
				return new MapCssAttributeValue(colorToken.GetText(), MapCssValueKind.Color, null);
			}

			if (literal.TRUE() is not null)
			{
				return new MapCssAttributeValue("true", MapCssValueKind.Boolean, null);
			}

			if (literal.FALSE() is not null)
			{
				return new MapCssAttributeValue("false", MapCssValueKind.Boolean, null);
			}
		}

		if (context.bareWord() is { } bareWord)
		{
			return new MapCssAttributeValue(GetText(tokens, bareWord), MapCssValueKind.BareWord, null);
		}

		return new MapCssAttributeValue(GetText(tokens, context), MapCssValueKind.BareWord, null);
	}

	private static Regex? TryCompileRegex(string pattern)
	{
		try
		{
			return new Regex(pattern, RegexOptions.CultureInvariant);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	private static string GetText(CommonTokenStream tokens, IRuleNode context)
	{
		return tokens.GetText(context.SourceInterval);
	}

	/// <summary>
	/// Unescape a quoted string literal from the token text.
	/// </summary>
	/// <remarks>
	/// The function expects a quoted string (single or double quotes). It removes the surrounding
	/// quotes and interprets common backslash escapes (\n, \r, \t) and preserves any other escaped
	/// character by outputting the character itself (\x -> x).
	/// </remarks>
	/// <param name="text">The raw token text including quotes.</param>
	/// <returns>The unescaped string content.</returns>
	private static string UnescapeString(string text)
	{
		if (text.Length < 2)
		{
			return text;
		}

		var quote = text[0];
		if (quote != '"' && quote != '\'')
		{
			return text;
		}

		var builder = new StringBuilder(text.Length - 2);
		for (var i = 1; i < text.Length - 1; i++)
		{
			var c = text[i];
			if (c == '\\' && i + 1 < text.Length - 1)
			{
				var next = text[++i];
				builder.Append(next switch
				{
					'n' => '\n',
					'r' => '\r',
					't' => '\t',
					_ => next
				});
			}
			else
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// Normalize a regex literal token by removing surrounding slashes and unescaping escaped slashes.
	/// </summary>
	/// <remarks>
	/// Accepts patterns of the form <c>/.../</c>. Any escaped slash sequence <c>\/</c> inside the
	/// literal is converted to a normal slash so the returned string can be compiled as a .NET <see cref="Regex"/>.
	/// </remarks>
	private static string UnescapeRegex(string text)
	{
		if (text.Length >= 2 && text[0] == '/' && text[^1] == '/')
		{
			text = text[1..^1];
		}

		return text.Replace("\\/", "/", StringComparison.Ordinal);
	}
}

internal sealed class MapCssStylesheet
{
	public MapCssStylesheet(
		IReadOnlyList<MapCssRuleSet> rules,
		IReadOnlyDictionary<string, IReadOnlyList<MapCssValue>> meta)
	{
		Rules = rules;
		Meta = meta;
	}

	public IReadOnlyList<MapCssRuleSet> Rules { get; }
	public IReadOnlyDictionary<string, IReadOnlyList<MapCssValue>> Meta { get; }
}

internal sealed class MapCssRuleSet
{
	public MapCssRuleSet(IReadOnlyList<MapCssSelector> selectors, IReadOnlyList<MapCssDeclaration> declarations)
	{
		Selectors = selectors;
		Declarations = declarations;
	}

	public IReadOnlyList<MapCssSelector> Selectors { get; }
	public IReadOnlyList<MapCssDeclaration> Declarations { get; }
}

internal sealed class MapCssSelector
{
	public MapCssSelector(IReadOnlyList<MapCssSelectorSegment> segments, string? subpart)
	{
		Segments = segments;
		Subpart = subpart;
	}

	public IReadOnlyList<MapCssSelectorSegment> Segments { get; }
	public string? Subpart { get; }
}

internal sealed record MapCssSelectorSegment(
	MapCssSimpleSelector Selector,
	MapCssCombinator CombinatorToPrevious,
	IReadOnlyList<MapCssAttributeTest> LinkFiltersToPrevious);

internal sealed class MapCssSimpleSelector
{
	public MapCssSimpleSelector(
		MapCssElementType? elementType,
		IReadOnlyList<MapCssZoomRange> zoomRanges,
		IReadOnlyList<string> classes,
		IReadOnlyList<string> pseudoClasses,
		IReadOnlyList<string> subparts,
		IReadOnlyList<MapCssAttributeTest> attributeTests)
	{
		ElementType = elementType;
		ZoomRanges = zoomRanges;
		Classes = classes;
		PseudoClasses = pseudoClasses;
		Subparts = subparts;
		AttributeTests = attributeTests;
	}

	public MapCssElementType? ElementType { get; }
	public IReadOnlyList<MapCssZoomRange> ZoomRanges { get; }
	public IReadOnlyList<string> Classes { get; }
	public IReadOnlyList<string> PseudoClasses { get; }
	public IReadOnlyList<string> Subparts { get; }
	public IReadOnlyList<MapCssAttributeTest> AttributeTests { get; }
}

internal abstract record MapCssDeclaration;

internal sealed record MapCssSetDeclaration(IReadOnlyList<string> Classes) : MapCssDeclaration;

internal sealed record MapCssPropertyDeclaration(string Name, IReadOnlyList<MapCssValue> Values) : MapCssDeclaration;

internal sealed record MapCssAttributeTest(
	string Key,
	MapCssAttributeExistence? Existence,
	MapCssAttributeOperator? Operator,
	MapCssAttributeValue? Value,
	bool Negate);

internal sealed record MapCssAttributeValue(string Text, MapCssValueKind Kind, Regex? Regex);

internal sealed record MapCssZoomRange(int Min, int? Max)
{
	public bool Matches(int? zoom)
	{
		if (zoom is null)
		{
			return true;
		}

		if (zoom < Min)
		{
			return false;
		}

		return Max is null || zoom <= Max.Value;
	}
}

internal enum MapCssAttributeExistence
{
	Truthy,
	NotTruthy
}

internal enum MapCssAttributeOperator
{
	Eq,
	NotEq,
	Contains,
	Prefix,
	Suffix,
	Match,
	NMatch,
	RegexMatch,
	RegexNMatch
}

internal enum MapCssCombinator
{
	None,
	Descendant,
	Child
}

internal enum MapCssValueKind
{
	String,
	Number,
	Color,
	Boolean,
	BareWord,
	Regex
}

internal static class MapCssSelectorMatcher
{
	/// <summary>
	/// Determine whether a selector matches a query context and leaf element classes.
	/// </summary>
	/// <remarks>
	/// This is the external entry point used by the style engine. It flattens the provided
	/// <see cref="MapCssContext"/>'s ancestry into a list and then attempts to match the
	/// selector's segments right-to-left (from the leaf-most segment to the root-most) using
	/// <see cref="MatchSegment"/>. The <c>leafClasses</c> parameter represents classes specifically
	/// present on the targeted leaf element (as opposed to classes on ancestor contexts).
	/// </remarks>
	/// <param name="selector">The selector AST to match.</param>
	/// <param name="query">The query providing the target context and zoom.</param>
	/// <param name="leafClasses">Classes present on the leaf element being matched.</param>
	/// <returns><c>true</c> if the selector matches the query.</returns>
	public static bool Matches(MapCssSelector selector, MapCssQuery query, IReadOnlyCollection<string> leafClasses)
	{
		var contexts = Flatten(query.Context);
		if (contexts.Count == 0)
		{
			return false;
		}

		return MatchSegment(selector.Segments, selector.Segments.Count - 1, contexts, 0, query.Zoom, leafClasses);
	}

	private static List<MapCssContext> Flatten(MapCssContext context)
	{
		var list = new List<MapCssContext>();
		var current = context;
		while (current is not null)
		{
			list.Add(current);
			current = current.Parent;
		}

		return list;
	}

	/// <summary>
	/// Recursively matches a selector segment against a context at a given position.
	/// </summary>
	/// <remarks>
	/// Matching is performed right-to-left: the method checks whether the current segment's
	/// simple selector matches the supplied context, then recursively enforces link filters
	/// and combinator constraints (<see cref="MapCssCombinator.Child"/> or <see cref="MapCssCombinator.Descendant"/>)
	/// by delegating to <see cref="MatchChild"/> or <see cref="MatchDescendant"/> as appropriate.
	/// The method treats the last selector segment specially by using the supplied <c>leafClasses</c>
	/// for class membership checks on the leaf element.
	/// </remarks>
	/// <param name="segments">List of selector segments (left-to-right order).</param>
	/// <param name="segmentIndex">Index of the segment to match (0..Count-1).</param>
	/// <param name="contexts">Flattened list of contexts (leaf first).</param>
	/// <param name="contextIndex">Index into <paramref name="contexts"/> to test against.</param>
	/// <param name="zoom">Optional zoom level used for zoom range checks.</param>
	/// <param name="leafClasses">Classes on the leaf element (used for segmentIndex == last).</param>
	/// <returns><c>true</c> if the segment (and its ancestors per combinators) match the provided contexts.</returns>
	private static bool MatchSegment(
		IReadOnlyList<MapCssSelectorSegment> segments,
		int segmentIndex,
		IReadOnlyList<MapCssContext> contexts,
		int contextIndex,
		int? zoom,
		IReadOnlyCollection<string> leafClasses)
	{
		var segment = segments[segmentIndex];
		var context = contexts[contextIndex];
		var classes = segmentIndex == segments.Count - 1 ? leafClasses : context.Element.Classes;

		if (!MatchesSimpleSelector(segment.Selector, context, classes, zoom))
		{
			return false;
		}

		if (segmentIndex == 0)
		{
			return true;
		}

		if (segment.LinkFiltersToPrevious.Count > 0)
		{
			if (!MatchesLinkFilters(segment.LinkFiltersToPrevious, context.LinkTags))
			{
				return false;
			}
		}

		return segment.CombinatorToPrevious switch
		{
			MapCssCombinator.Child => MatchChild(segments, segmentIndex, contexts, contextIndex, zoom, leafClasses),
			MapCssCombinator.Descendant => MatchDescendant(segments, segmentIndex, contexts, contextIndex, zoom, leafClasses),
			_ => false
		};
	}

	private static bool MatchChild(
		IReadOnlyList<MapCssSelectorSegment> segments,
		int segmentIndex,
		IReadOnlyList<MapCssContext> contexts,
		int contextIndex,
		int? zoom,
		IReadOnlyCollection<string> leafClasses)
	{
		var nextIndex = contextIndex + 1;
		if (nextIndex >= contexts.Count)
		{
			return false;
		}

		return MatchSegment(segments, segmentIndex - 1, contexts, nextIndex, zoom, leafClasses);
	}

	private static bool MatchDescendant(
		IReadOnlyList<MapCssSelectorSegment> segments,
		int segmentIndex,
		IReadOnlyList<MapCssContext> contexts,
		int contextIndex,
		int? zoom,
		IReadOnlyCollection<string> leafClasses)
	{
		for (var nextIndex = contextIndex + 1; nextIndex < contexts.Count; nextIndex++)
		{
			if (MatchSegment(segments, segmentIndex - 1, contexts, nextIndex, zoom, leafClasses))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Evaluate whether a simple selector matches a single context element.
	/// </summary>
	/// <remarks>
	/// The checks performed include element type, zoom ranges, classes, pseudo-classes and
	/// attribute tests. All conditions must pass for the selector to match. The <c>classes</c>
	/// parameter is used to provide the class set of the element being matched (for leaf segments
	/// this is supplied externally; for ancestor segments the context's element classes are used).
	/// </remarks>
	/// <param name="selector">The simple selector to evaluate.</param>
	/// <param name="context">The context providing element type, tags and pseudo-classes.</param>
	/// <param name="classes">The set of classes to test membership against.</param>
	/// <param name="zoom">Optional zoom level used when evaluating zoom ranges.</param>
	/// <returns><c>true</c> when every check succeeds.</returns>
	private static bool MatchesSimpleSelector(
		MapCssSimpleSelector selector,
		MapCssContext context,
		IReadOnlyCollection<string> classes,
		int? zoom)
	{
		if (selector.ElementType is { } elementType && elementType != MapCssElementType.Any)
		{
			if (context.Element.Type != elementType)
			{
				return false;
			}
		}

		foreach (var zoomRange in selector.ZoomRanges)
		{
			if (!zoomRange.Matches(zoom))
			{
				return false;
			}
		}

		foreach (var cls in selector.Classes)
		{
			if (!ContainsString(classes, cls))
			{
				return false;
			}
		}

		foreach (var pseudo in selector.PseudoClasses)
		{
			if (!ContainsString(context.Element.PseudoClasses, pseudo))
			{
				return false;
			}
		}

		foreach (var test in selector.AttributeTests)
		{
			if (!MatchesAttributeTest(test, context.Element.Tags))
			{
				return false;
			}
		}

		return true;
	}

	private static bool MatchesLinkFilters(
		IReadOnlyList<MapCssAttributeTest> filters,
		IReadOnlyDictionary<string, string>? linkTags)
	{
		if (linkTags is null)
		{
			return false;
		}

		foreach (var filter in filters)
		{
			if (!MatchesAttributeTest(filter, linkTags))
			{
				return false;
			}
		}

		return true;
	}

	private static bool MatchesAttributeTest(
		MapCssAttributeTest test,
		IReadOnlyDictionary<string, string> tags)
	{
		var result = EvaluateAttributeTest(test, tags);
		return test.Negate ? !result : result;
	}

	/// <summary>
	/// Evaluate a full attribute test against a set of tags.
	/// </summary>
	/// <remarks>
	/// The evaluation follows MapCSS semantics: it first evaluates existence modifiers (truthy / not truthy),
	/// then evaluates any operator-based comparison (e.g. =, !=, *=, etc.). If only an operator is present
	/// but no value is provided, the operator evaluation fails. When neither an operator nor existence is
	/// specified, the test reduces to a simple "has tag" check.
	/// </remarks>
	/// <param name="test">The attribute test to evaluate (may include negation flag).</param>
	/// <param name="tags">Dictionary of tag keys and values for the element being tested.</param>
	/// <returns><c>true</c> when the attribute test is satisfied.</returns>
	private static bool EvaluateAttributeTest(
		MapCssAttributeTest test,
		IReadOnlyDictionary<string, string> tags)
	{
		tags.TryGetValue(test.Key, out var tagValue);
		var hasTag = tagValue is not null;

		var existenceResult = true;
		if (test.Existence is { } existence)
		{
			var truthy = IsTruthy(tagValue);
			existenceResult = existence == MapCssAttributeExistence.Truthy ? truthy : !truthy;
		}

		var opResult = true;
		if (test.Operator is { } op && test.Value is { } value)
		{
			opResult = EvaluateOperator(op, tagValue, value);
		}
		else if (test.Operator is not null)
		{
			opResult = false;
		}
		else if (test.Existence is null)
		{
			opResult = hasTag;
		}

		return existenceResult && opResult;
	}

	/// <summary>
	/// Evaluate a binary attribute operator between the tag value and the specified comparison value.
	/// </summary>
	/// <remarks>
	/// Supports simple string operators (Eq/NotEq/Contains/Prefix/Suffix), pattern match operators
	/// (Match/NMatch) and regex-based operators (RegexMatch/RegexNMatch). For match operators, boolean
	/// negation is applied according to the operator variant.
	/// </remarks>
	/// <param name="op">The operator to evaluate.</param>
	/// <param name="tagValue">The actual tag value to compare (may be <c>null</c>).</param>
	/// <param name="value">The value to compare against (text and kind information).</param>
	/// <returns><c>true</c> if the operator relation holds for the provided tag value.</returns>
	private static bool EvaluateOperator(
		MapCssAttributeOperator op,
		string? tagValue,
		MapCssAttributeValue value)
	{
		if (tagValue is null)
		{
			return false;
		}

		var compareValue = value.Text;
		return op switch
		{
			MapCssAttributeOperator.Eq => string.Equals(tagValue, compareValue, StringComparison.Ordinal),
			MapCssAttributeOperator.NotEq => !string.Equals(tagValue, compareValue, StringComparison.Ordinal),
			MapCssAttributeOperator.Contains => tagValue.Contains(compareValue, StringComparison.Ordinal),
			MapCssAttributeOperator.Prefix => tagValue.StartsWith(compareValue, StringComparison.Ordinal),
			MapCssAttributeOperator.Suffix => tagValue.EndsWith(compareValue, StringComparison.Ordinal),
			MapCssAttributeOperator.Match => EvaluateMatch(tagValue, value, negate: false),
			MapCssAttributeOperator.NMatch => EvaluateMatch(tagValue, value, negate: true),
			MapCssAttributeOperator.RegexMatch => EvaluateRegex(tagValue, value, negate: false),
			MapCssAttributeOperator.RegexNMatch => EvaluateRegex(tagValue, value, negate: true),
			_ => false
		};
	}

	private static bool EvaluateMatch(string tagValue, MapCssAttributeValue value, bool negate)
	{
		var matched = value.Kind == MapCssValueKind.Regex
			? EvaluateRegex(tagValue, value, negate: false)
			: tagValue.Contains(value.Text, StringComparison.Ordinal);

		return negate ? !matched : matched;
	}

	private static bool EvaluateRegex(string tagValue, MapCssAttributeValue value, bool negate)
	{
		var regex = value.Regex ?? TryCompileRegex(value.Text);
		var matched = regex is not null && regex.IsMatch(tagValue);
		return negate ? !matched : matched;
	}

	/// <summary>
	/// Interpret a tag value as a boolean "truthy" according to MapCSS conventions.
	/// </summary>
	/// <remarks>
	/// Empty or whitespace-only strings are considered false. Additionally, the values
	/// "0", "false" and "no" (case-insensitive) are treated as false; all other
	/// non-empty values are considered true.
	/// </remarks>
	/// <param name="value">The tag value string to evaluate.</param>
	/// <returns><c>true</c> when the value is considered truthy.</returns>
	private static bool IsTruthy(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		return value.Trim().ToLowerInvariant() switch
		{
			"0" => false,
			"false" => false,
			"no" => false,
			_ => true
		};
	}

	private static bool ContainsString(IReadOnlyCollection<string> values, string candidate)
	{
		foreach (var value in values)
		{
			if (string.Equals(value, candidate, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static Regex? TryCompileRegex(string pattern)
	{
		try
		{
			return new Regex(pattern, RegexOptions.CultureInvariant);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}
}
