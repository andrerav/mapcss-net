using System.Collections.Generic;
using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Tests
{
	public class SelectorMatcherTests
	{
		/// <summary>
		/// Unit tests that exercise the selector matching logic (MapCssSelectorMatcher).
		/// These tests construct MapCssContext trees (parent/child) and MapCssElement
		/// instances to verify correct behavior for combinators, classes/pseudos,
		/// link filters, and attribute operators.
		///</summary>
		[Test]
		public void ChildCombinator_MatchesDirectParent()
		{
			var css = "node > way { a: 1; }";
			var sheet = MapCssParserFacade.Parse(css);
			var selector = sheet.Rules[0].Selectors[0];

			var parent = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
			var child = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());
			var ctxParent = new MapCssContext(parent);
			var ctxChild = new MapCssContext(child, ctxParent);
			var query = new MapCssQuery(ctxChild);

			Assert.That(MapCssSelectorMatcher.Matches(selector, query, new string[0]), Is.True);
		}

		// Verify that implicit descendant combinators match deeper ancestors
		// (A B should match when B has A as an ancestor at any level).
		[Test]
		public void DescendantCombinator_MatchesDeepAncestor()
		{
			var css = "node way { a: 1; }";
			var sheet = MapCssParserFacade.Parse(css);
			var selector = sheet.Rules[0].Selectors[0];

			var anc = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
			var mid = new MapCssElement(MapCssElementType.Area, new Dictionary<string, string>());
			var leaf = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());

			var ctxAnc = new MapCssContext(anc);
			var ctxMid = new MapCssContext(mid, ctxAnc);
			var ctxLeaf = new MapCssContext(leaf, ctxMid);
			var q = new MapCssQuery(ctxLeaf);

			Assert.That(MapCssSelectorMatcher.Matches(selector, q, new string[0]), Is.True);
		}

		// Test link filters applied to intermediate selector segments.
		// When a selector includes link filters (e.g. >[role=inner] way), the
		// matching context must provide `LinkTags` with the required key/value pairs.
		[Test]
		public void LinkFilters_Require_LinkTags()
		{
			var css = "node >[role=inner] way { a: 1; }";
			var sheet = MapCssParserFacade.Parse(css);
			var selector = sheet.Rules[0].Selectors[0];

			var parent = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
			var linkTags = new Dictionary<string, string>{{"role","inner"}};
			var child = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());
			var ctxParent = new MapCssContext(parent);
			var ctxChildWithLink = new MapCssContext(child, ctxParent, linkTags);
			var qGood = new MapCssQuery(ctxChildWithLink);

			Assert.That(MapCssSelectorMatcher.Matches(selector, qGood, new string[0]), Is.True);

			// without link tags it must fail
			var ctxChildNoLink = new MapCssContext(child, ctxParent);
			var qBad = new MapCssQuery(ctxChildNoLink);
			Assert.That(MapCssSelectorMatcher.Matches(selector, qBad, new string[0]), Is.False);
		}

		// Test common attribute operator semantics by parsing a selector with
		// the operator and evaluating it against MapCssElement instances.
		// The AttributeOperatorCases yields covering examples for =, !=, *=, ^=, $=, ~= and regex matching.
		[TestCaseSource(nameof(AttributeOperatorCases))]
		public void AttributeOperators_BehaveAsExpected(string css, MapCssElement element, bool expected)
		{
			var sheet = MapCssParserFacade.Parse(css);
			var selector = sheet.Rules[0].Selectors[0];
			var q = new MapCssQuery(new MapCssContext(element));
			Assert.That(MapCssSelectorMatcher.Matches(selector, q, new string[0]), Is.EqualTo(expected));
		}

		public static IEnumerable<TestCaseData> AttributeOperatorCases()
		{
			yield return new TestCaseData("way[name=foo] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foo"}}), true).SetName("attr_eq_match");
			yield return new TestCaseData("way[name=foo] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>()), false).SetName("attr_eq_missing");
			yield return new TestCaseData("way[name!=foo] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","bar"}}), true).SetName("attr_noteq_match");
			yield return new TestCaseData("way[name*=bar] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foobarbaz"}}), true).SetName("attr_contains_match");
			yield return new TestCaseData("way[name^=foo] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foobaz"}}), true).SetName("attr_prefix_match");
			yield return new TestCaseData("way[name$=bar] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foobar"}}), true).SetName("attr_suffix_match");
			yield return new TestCaseData("way[name~=bar] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","this has bar inside"}}), true).SetName("attr_match_contains");
			yield return new TestCaseData("way[name!~=bar] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","bar"}}), false).SetName("attr_nmatch_negate");
			yield return new TestCaseData("way[name=~/^foo/] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","foobar"}}), true).SetName("attr_re_match");
			yield return new TestCaseData("way[name!~/^foo/] { a:1; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string,string>{{"name","bar"}}), true).SetName("attr_re_nmatch");
		}

		[Test]
		public void ClassesAndPseudo_AreMatchedCorrectly()
		{
			var css = ".foo:bar { a:1; }";
			var s = MapCssParserFacade.Parse(css).Rules[0].Selectors[0];

			var el = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>(), new[] { "foo" }, new[] { "bar" });
			var q = new MapCssQuery(new MapCssContext(el));
			// sanity-check parsed selector contents
			Assert.That(s.Segments[0].Selector.Classes, Is.EquivalentTo(new[] { "foo" }));
			Assert.That(s.Segments[0].Selector.PseudoClasses, Is.EquivalentTo(new[] { "bar" }));
			Assert.That(MapCssSelectorMatcher.Matches(s, q, new[] { "foo" }), Is.True);
			
			// missing pseudo
			var el2 = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>(), new[] { "foo" });
			var q2 = new MapCssQuery(new MapCssContext(el2));
			Assert.That(MapCssSelectorMatcher.Matches(s, q2, new[] { "foo" }), Is.False);
		}

		[Test]
		public void LeafClasses_AreUsedForLastSegment()
		{
			var css = ".leaf { a:1; }";
			var s = MapCssParserFacade.Parse(css).Rules[0].Selectors[0];

			// element itself lacks classes, but leafClasses contains the class
			var el = new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>());
			var q = new MapCssQuery(new MapCssContext(el));
			Assert.That(MapCssSelectorMatcher.Matches(s, q, new[] { "leaf" }), Is.True);
		}
	}
}
