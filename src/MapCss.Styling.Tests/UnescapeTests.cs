using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Styling.Tests
{
	public class UnescapeTests
	{
		// Tests that verify how literal and regex escaping is handled during parsing.
		// These ensure that escape sequences such as '\n' and escaped '/' in regex
		// are converted to the intended internal textual representation.
		[Test]
		public void UnescapeString_HandlesEscapes()
		{
			// use parser to get access to helper behavior via literal parsing
			var css = "[k=\"line\\nnew\"] { a:1; }"; // value is "line\nnew"
			var sheet = MapCssParserFacade.Parse(css);
			var v = sheet.Rules[0].Selectors[0].Segments[0].Selector.AttributeTests[0].Value;
			Assert.That(v!.Text, Is.EqualTo("line\nnew"));
		}

		// Ensure regex literal text unescapes escaped slash sequences (e.g. a\/b -> a/b)
		[Test]
		public void UnescapeRegex_HandlesEscapedSlash()
		{
			var css = "[k=~/a\\/b/] { a:1; }"; // pattern a\/b => a/b
			var sheet = MapCssParserFacade.Parse(css);
			var v = sheet.Rules[0].Selectors[0].Segments[0].Selector.AttributeTests[0].Value;
			Assert.That(v!.Text, Is.EqualTo("a/b"));
		}
	}
}
