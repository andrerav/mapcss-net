using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using MapCss.Parser;
using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Styling.Tests
{
	public class ParsingTests
	{
		// Higher-level tests that cover integration points of the parser with
		// the generated ANTLR listener/visitor and the MapCssParserFacade.
		// These validate that common constructs parse and the AST contains
		// the expected structure for subsequent engine use.
		[Test]
		public void VisitorAndListener_WalkAndVisit_DoNotThrow()
		{
			var css = @"meta { author: ""me""; }
node|z10[class=foo]::subpart, way >[role=inner] way { stroke-color: #fff; set .foo; }";

			// build parser and tree
			var input = new AntlrInputStream(css);
			var lexer = new MapCssLexer(input);
			var tokens = new CommonTokenStream(lexer);
			var parser = new MapCssParser(tokens);
			var tree = parser.stylesheet();

			// Walk with base listener
			ParseTreeWalker.Default.Walk(new MapCssParserBaseListener(), tree);

			// Visit with base visitor
			var v = new MapCssParserBaseVisitor<object>();
			v.Visit(tree);

			// And ensure facade parser returns a non-null stylesheet
			var sheet = MapCssParserFacade.Parse(css);
			Assert.That(sheet, Is.Not.Null);
			Assert.That(sheet.Rules, Is.Not.Empty);
		}

		[TestCase("node|z10", 10, null, true, 9, false)]
		[TestCase("node|z15-20", 15, 20, true, 21, false)]
		[TestCase("node|z15-", 15, null, true, 100, true)]
		public void ParseZoomRange_CorrectlyParsesRanges(string selectorPrefix, int min, int? max, bool matchNull, int testZoom, bool expectedMatch)
		{
			var css = selectorPrefix + " { a: 1; }";
			var sheet = MapCssParserFacade.Parse(css);
			Assert.That(sheet.Rules.Count, Is.EqualTo(1));
			var selector = sheet.Rules[0].Selectors[0];
			var zoomRanges = selector.Segments[0].Selector.ZoomRanges;
			Assert.That(zoomRanges.Count, Is.EqualTo(1));
			var zr = zoomRanges[0];
			Assert.That(zr.Min, Is.EqualTo(min));
			Assert.That(zr.Max, Is.EqualTo(max));
			Assert.That(zr.Matches(null), Is.EqualTo(matchNull));
			Assert.That(zr.Matches(testZoom), Is.EqualTo(expectedMatch));
		}

		// Confirm that attribute-only segments (link filters) are merged properly
		// into adjacent selector segments so that link filters are available on the
		// corresponding selector segment after parsing.
		[Test]
		public void MergeLinkSelectors_MergesAttributeOnlySegment()
		{
			var css = "node >[role=inner] way { a: 1; }";
			var sheet = MapCssParserFacade.Parse(css);
			var selector = sheet.Rules[0].Selectors[0];
			// after merging, there should be 2 segments (node and way)
			Assert.That(selector.Segments.Count, Is.EqualTo(2));
			// the second segment should have link filters (from the middle attribute)
			var second = selector.Segments[1];
			Assert.That(second.LinkFiltersToPrevious, Is.Not.Null.And.Not.Empty);
			Assert.That(second.LinkFiltersToPrevious[0].Key, Is.EqualTo("role"));
		}

		// Tests for existence and truthy/not-truthy attribute checks.
		// Verifies that foo? and foo?! behave as truthy checks and their negation.
		[Test]
		public void ExistenceChecks_TruthyAndNotTruthyBehaveAsExpected()
		{
			var cssTruthy = "[foo?] { a:1; }";
			var cssNotTruthy = "[foo?!] { a:1; }";

			var selTruthy = MapCssParserFacade.Parse(cssTruthy).Rules[0].Selectors[0];
			var selNotTruthy = MapCssParserFacade.Parse(cssNotTruthy).Rules[0].Selectors[0];

			string key = "foo";

			void AssertMatch(MapCssSelector s, IReadOnlyDictionary<string,string> tags, bool expected)
			{
				var ctx = new MapCssContext(new MapCssElement(MapCssElementType.Node, tags));
				var q = new MapCssQuery(ctx);
				Assert.That(MapCssSelectorMatcher.Matches(s, q, Array.Empty<string>()), Is.EqualTo(expected));
			}

			// missing tag: foo? -> false, foo?! -> true (not truthy of missing => true)
			AssertMatch(selTruthy, new Dictionary<string,string>(), false);
			AssertMatch(selNotTruthy, new Dictionary<string,string>(), true);

			// 0 and false and no are considered falsey
			AssertMatch(selTruthy, new Dictionary<string,string>{{key,"0"}}, false);
			AssertMatch(selTruthy, new Dictionary<string,string>{{key,"false"}}, false);
			AssertMatch(selTruthy, new Dictionary<string,string>{{key,"no"}}, false);

			// truthy value
			AssertMatch(selTruthy, new Dictionary<string,string>{{key,"1"}}, true);
			AssertMatch(selNotTruthy, new Dictionary<string,string>{{key,"1"}}, false);
		}
		// Iterate a broad collection of small grammar snippets and ensure
		// that lexing/parsing and tree walking/visiting do not throw exceptions.
		[Test]
		public void ParserGrammar_ExhaustiveSnippets_DoNotThrow()
		{
			var snippets = new[]
			{
				"meta { author: \"me\"; }",
				"node { a: 1; }",
				"[key=value] { a:1; }",
				"[\"quoted:key\"=value] { a:1; }",
				"[key*=val] { a:1; }",
				"[key^=val] { a:1; }",
				"[key$=val] { a:1; }",
				"[key~=val] { a:1; }",
				"[key!~=val] { a:1; }",
				"[key=~/foo\\/] { a:1; }",
				"[key!~/foo\\/] { a:1; }",
				"[key] { a:1; }",
				"[key?!] { a:1; }",
				".class { a:1; }",
				"::subpart { a:1; }",
				":pseudo(1,2) { a:1; }",
				"node > way { a:1; }",
				"node way { a:1; }",
				"node { a: 1, 2; }",
				"node { set foo, .bar; }",
				"[key=calling-in_point] { a:1; }",
				"node { a: func(1,2+3); }",
				"node { a: concat('a','b'); }",
				"node { a: 'line\\nbreak'; }",
				"[key=/a\\/b/] { a:1; }",
				"node|z10 { a:1; }",
				"node|z15- { a:1; }",
				"node|z15-20 { a:1; }",
				"[x=~/.*/] { a:1; }",
				"[x!~/.*foo/] { a:1; }",
				"[x=y] { a:1; }",
				"node { a: 1 ? 2 : 3; }",
				"node { a: (1 + 2) * 3 - 4 / 5; }",
				"node { a: !true; }",
				"node { a: -5; }",
			};

			foreach (var s in snippets)
			{
				var input = new AntlrInputStream(s);
				var lexer = new MapCssLexer(input);
				// replace default error listeners with throwing ones so syntax/lex errors
				// cause exceptions and fail the test (instead of merely logging warnings).
				lexer.RemoveErrorListeners();
				lexer.AddErrorListener(new ThrowingLexerErrorListener());
				var tokens = new CommonTokenStream(lexer);
				var parser = new MapCssParser(tokens);
				parser.RemoveErrorListeners();
				parser.AddErrorListener(new ThrowingParserErrorListener());
				var tree = parser.stylesheet();

				// walk and visit the tree to exercise generated listener/visitor
				ParseTreeWalker.Default.Walk(new MapCssParserBaseListener(), tree);
				new MapCssParserBaseVisitor<object>().Visit(tree);
			}
		}
	}
}
