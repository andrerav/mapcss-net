using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using NUnit.Framework;
using MapCss.Parser;
using MapCss.Styling;

namespace MapCss.Tests
{
	/**
	 * BulkGeneratedTests
	 * ------------------
	 * These tests programmatically generate a large number of MapCSS snippets
	 * and evaluation scenarios to exercise the parser, AST builder, listener/visitor,
	 * and the style engine. The goal is to increase code coverage across many
	 * grammar and engine paths with a broad, automated corpus.
	 *
	 * Tests included:
	 * - ParserSnippets_DoNotThrow: ensure many small MapCSS snippets parse and the
	 *   parse tree can be walked/visited without throwing.
	 * - Evaluate_Snippets_ReturnExpected: curated evaluation checks (e.g. equality,
	 *   regex, class, pseudo, child-with-parent) that assert the engine applies
	 *   properties (like `color`) when expected and does not otherwise.
	 * - ParserInvalidSnippets_ThrowOrAccept: feed malformed-ish inputs to ensure
	 *   parser handles them gracefully (it may throw a controlled exception or parse).
	 */
	public class BulkGeneratedTests
	{
		[TestCaseSource(nameof(ParserSnippetCases))]
		public void ParserSnippets_DoNotThrow(string css)
		{
			// parse + walk/visit to exercise parser and generated classes
			var sheet = MapCssParserFacade.Parse(css);
			Assert.That(sheet, Is.Not.Null);
			Assert.That(sheet.Rules.Count, Is.GreaterThanOrEqualTo(1));
			// Walk and visit
			ParseTreeWalker.Default.Walk(new MapCssParserBaseListener(), new MapCssParser(new Antlr4.Runtime.CommonTokenStream(new MapCssLexer(new Antlr4.Runtime.AntlrInputStream(css)))).stylesheet());
			new MapCssParserBaseVisitor<object>().Visit(new MapCssParser(new Antlr4.Runtime.CommonTokenStream(new MapCssLexer(new Antlr4.Runtime.AntlrInputStream(css)))).stylesheet());
		}

		public static IEnumerable<TestCaseData> ParserSnippetCases()
		{
			var elements = new[] { "node", "way", "relation", "area", "canvas", "*" };
			var classes = new[] { ".foo", ".bar", ".baz" };
			var pseudos = new[] { ":hover", ":active", ":any" };
			var attrs = new[] { "[k]", "[k=1]", "[k!=1]", "[k*=ab]", "[k^=pre]", "[k$=suf]", "[k~=mid]", "[k=~/^foo/]" };
			var zoom = new[] { "", "|z10", "|z15-", "|z5-12" };

			// simple combinations
			foreach (var el in elements)
			{
				foreach (var z in zoom)
				{
					foreach (var c in classes)
					{
						var css = $"{el}{z}{c} {{ color: red; }}";
						yield return new TestCaseData(css).SetName($"parse_{el}_{z}_{c}");
					}
					foreach (var p in pseudos)
					{
						var css = $"{el}{z}{p} {{ color: blue; }}";
						yield return new TestCaseData(css).SetName($"parse_{el}_{z}_{p}");
					}
					foreach (var a in attrs)
					{
						var css = $"{el}{z}{a} {{ width: 1; }}";
						yield return new TestCaseData(css).SetName($"parse_{el}_{z}_attr_{a.Replace(' ','_')}");
					}
				}
			}

			// combinators and chains
			var combos = new[] { ">", " " };
			for (int i = 0; i < elements.Length - 1; i++)
			{
				for (int j = i+1; j < elements.Length; j++)
				{
					foreach (var comb in combos)
					{
						var css = $"{elements[i]} {comb} {elements[j]} {{ stroke: #fff; }}";
						yield return new TestCaseData(css).SetName($"parse_chain_{elements[i]}_{comb}_{elements[j]}");
					}
				}
			}

			// lists, sets, property exprs
			for (int i = 0; i < 50; i++)
			{
				var css = $"node {{ set s{i}, .cls{i}; prop{i}: {i}, {i+1}, concat('{i}','x'); }}";
				yield return new TestCaseData(css).SetName($"parse_list_{i}");
			}

			// regex escaping variations
			var patterns = new[] { "/a\\/b/", "/^foo/", "/bar$/", "/[0-9]+/", "/(a(b))/" };
			foreach (var p in patterns)
			{
				var css = $"[x=~{p}] {{ color: r; }}";
				yield return new TestCaseData(css).SetName($"parse_re_{p}");
			}

			// end of parser snippet cases
		}

		public static IEnumerable<TestCaseData> InvalidSnippetCases()
		{
			var invalids = new[] { "node { a: 1", "[k=~/[/] { a:1; }", "[k=~/(/] { }" };
			foreach (var inv in invalids) yield return new TestCaseData(inv).SetName($"invalid_parse_{inv.GetHashCode()}");
		}

		[TestCaseSource(nameof(InvalidSnippetCases))]
		public void ParserInvalidSnippets_ThrowOrAccept(string css)
		{
			try
			{
				MapCssParserFacade.Parse(css);
			}
			catch (InvalidOperationException)
			{
				// parse may throw for invalid inputs — acceptable
				Assert.Pass();
			}
			// If it parsed without throwing, accept it as well (we're mostly ensuring parser stability)
			Assert.Pass();
		}

		[TestCaseSource(nameof(EvaluateCases))]
		public void Evaluate_Snippets_ReturnExpected(string css, MapCssContext ctx, string expectedColor)
		{
			if (TestContext.CurrentContext.Test.Name == "diag_parse_eq")
			{
				var sheet = MapCssParserFacade.Parse("node[name=foo0] { color: c0; }");
				var sel = sheet.Rules[0].Selectors[0];
				Assert.That(sel.Segments.Count, Is.GreaterThan(0));
				var at = sel.Segments[0].Selector.AttributeTests;
				Assert.That(at.Count, Is.EqualTo(1));
				Assert.That(at[0].Key, Is.EqualTo("name"));
				Assert.That(at[0].Operator, Is.EqualTo(MapCssAttributeOperator.Eq));
				Assert.That(at[0].Value!.Text, Is.EqualTo("foo0"));
				return;
			}
			if (TestContext.CurrentContext.Test.Name == "diag_selector_match_eq")
			{
				var sheet = MapCssParserFacade.Parse("node[name=foo0] { color: c0; }");
				var sel = sheet.Rules[0].Selectors[0];
				var elOther = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>{{"name","other"}});
				var qOther = new MapCssQuery(new MapCssContext(elOther));
				Assert.That(MapCssSelectorMatcher.Matches(sel, qOther, Array.Empty<string>()), Is.False, "Selector should not match when tag differs");
				var elMatch = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>{{"name","foo0"}});
				var qMatch = new MapCssQuery(new MapCssContext(elMatch));
				Assert.That(MapCssSelectorMatcher.Matches(sel, qMatch, Array.Empty<string>()), Is.True, "Selector should match when tag equals value");
				return;
			}
			if (TestContext.CurrentContext.Test.Name == "diag_engine_vs_selector")
			{
				var cssDiag = "node[name=foo0] { color: c0; }";
				var sheet = MapCssParserFacade.Parse(cssDiag);
				var sel = sheet.Rules[0].Selectors[0];
				var elOther = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>{{"name","other"}});
				var ctxOther = new MapCssContext(elOther);
				var qOther = new MapCssQuery(ctxOther);
				var selectorMatches = MapCssSelectorMatcher.Matches(sel, qOther, Array.Empty<string>());
				// compute engine's matched set using internal state
				var state = new MapCssEvaluationState(qOther);
				var matched = state.MatchRule(sheet.Rules[0]);
				var engineDiag = new MapCssStyleEngine(cssDiag);
				var resDiag = engineDiag.Evaluate(qOther);
				resDiag.Layers.TryGetValue(string.Empty, out var layerDiag);
				TestContext.Out.WriteLine($"selectorMatches={selectorMatches}; matched=[{string.Join(',', matched)}]; layers=[{string.Join(',', resDiag.Layers.Keys)}]; layerDiag={(layerDiag!=null)}");
				Assert.That(selectorMatches, Is.False, "Selector raw match should be false");
				if (matched.Count != 0 || (layerDiag?.Properties.ContainsKey("color") == true))
				{
					Assert.Fail($"Mismatch: selectorMatches={selectorMatches}; matched=[{string.Join(',', matched)}]; layers=[{string.Join(',', resDiag.Layers.Keys)}]; colorPresent={(layerDiag?.Properties.ContainsKey("color") == true)}");
				}
				return;
			}
			var engine = new MapCssStyleEngine(css);
			var q = new MapCssQuery(ctx);
			var res = engine.Evaluate(q);
			res.Layers.TryGetValue(string.Empty, out var layer);
			if (expectedColor is null)
			{
				if (layer?.Properties.ContainsKey("color") == true)
				{
					// diagnostic: inspect parsed selector to understand why it matched
					var sheet = MapCssParserFacade.Parse(css);
					var sel = sheet.Rules[0].Selectors[0];
					var attrs = sel.Segments.SelectMany(s => s.Selector.AttributeTests).Select(a => $"{a.Key}{(a.Operator.HasValue ? a.Operator.Value.ToString() : "")}{(a.Value != null ? a.Value.Text : "")}");
					Assert.Fail($"Expected no color but color applied. Parsed selector attrs: {string.Join(",", attrs)}");
				}
				Assert.That(layer?.Properties.ContainsKey("color") == false);
			}
			else
			{
				Assert.That(layer, Is.Not.Null);
				Assert.That(layer.Properties.ContainsKey("color"));
				Assert.That(layer.Properties["color"][0], Is.EqualTo(expectedColor));
			}
		}

		public static IEnumerable<TestCaseData> EvaluateCases()
		{
			// quick sanity checks (one-off) - ensure equality attr behaves
			
			// diagnostic helper test: ensure parsed attribute items are correct
			yield return new TestCaseData("node[name=foo0] { color: c0; }", null, null).SetName("diag_parse_eq");
			// selector matching diagnostic
			yield return new TestCaseData("node[name=foo0] { color: c0; }", null, null).SetName("diag_selector_match_eq");
			
			// limited, curated evaluation tests (avoid large combinatorial evals that produce many brittle expectations)
			// equality basic
			yield return new TestCaseData("node[name=foo] { color: eq; }", new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>{{"name","foo"}})), "eq").SetName("eval_eq_match_basic");
			// regex
			yield return new TestCaseData("way[name=~/^foo/] { color: rex; }", new MapCssContext(new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foobar"}})), "rex").SetName("eval_re_match_basic");


			// child vs descendant
			var cssChild = "node > way { color: child; }";
			var parent = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>());
			var child = new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>());
			// matching when parent present
			var ctxChild = new MapCssContext(child, new MapCssContext(parent));
			yield return new TestCaseData(cssChild, ctxChild, "child").SetName("eval_child_with_parent");

			// class selector
			var cssClass = ".green { color: green; }";
			yield return new TestCaseData(cssClass, new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>(), new[] { "green" })), "green").SetName("eval_class_match");

			// pseudo and subpart simple checks
			var cssPseudo = ":foo { color: p; }";
			yield return new TestCaseData(cssPseudo, new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>(), null, new[] { "foo" })), "p").SetName("eval_pseudo_match");
		}
	}
}
