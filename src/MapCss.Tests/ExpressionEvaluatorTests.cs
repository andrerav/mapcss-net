using System.Collections.Generic;
using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Tests
{
	public class ExpressionEvaluatorTests
	{
		// Tests for the lightweight ExpressionEvaluator used in property values.
		// These verify string functions (concat, split) and list functions (count/get)
		// and ensure expression evaluation results are embedded into property values
		// produced by the engine (engine applies evaluated value text into MapCssValue).
		[Test]
		public void ConcatAndSplitAndCount_GetWorkTogether()
		{
			var css = "node { foo: concat('a','b'); bar: count(split(',', 'a,b')); }";
			var engine = new MapCssStyleEngine(css);
			var q = new MapCssQuery(new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>())));
			var res = engine.Evaluate(q);
			var props = res.Layers[string.Empty].Properties;
			Assert.That(props.ContainsKey("foo"));
			Assert.That(props["foo"][0].Text, Is.EqualTo("ab"));
			Assert.That(props.ContainsKey("bar"));
			Assert.That(props["bar"][0].Text, Is.EqualTo("2"));
			// Direct evaluation sanity-check
			var direct = ExpressionEvaluator.Evaluate("count(split(',', 'a,b'))", q);
			Assert.That(direct, Is.EqualTo("2"));
		}

		// Verify that a failing eval inside a concat/expr does not prevent other properties
		// from being evaluated/applied. This ensures robustness of expression evaluation.
		[Test]
		public void NestedCondEval_FallsBackOnError()
		{
			// eval of non-existent var should not crash evaluation of other properties
			var css = "node { a: concat('x', eval('nonexistent')); b: 1; }";
			var engine = new MapCssStyleEngine(css);
			var q = new MapCssQuery(new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string,string>())));
			var res = engine.Evaluate(q);
			Assert.That(res.Layers[string.Empty].Properties.ContainsKey("a"));
			Assert.That(res.Layers[string.Empty].Properties.ContainsKey("b"));
		}
	}
}
