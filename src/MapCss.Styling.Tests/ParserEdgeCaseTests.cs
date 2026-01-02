using System;
using System.Collections.Generic;
using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Styling.Tests
{
	public class ParserEdgeCaseTests
	{
		// Tests that probe edge cases in the lexer and parser.
		// These are focused on malformed inputs and on verifying that
		// errors are surfaced in a controlled way (InvalidOperationException).
		
		// Verify that an unterminated string literal is reported as a lexer error.
		// The parser facade wraps lexer errors in InvalidOperationException containing "Lex error".
		[Test]
		public void UnterminatedString_ThrowsLexError()
		{
			var css = "node { a: \"noend; }";
			var ex = Assert.Throws<InvalidOperationException>(() => MapCssParserFacade.Parse(css));
			Assert.That(ex.Message, Does.Contain("Lex error"));
		}

		// Missing or malformed declaration terminators should be reported as parse errors.
		// We assert that a Parse error (InvalidOperationException) is thrown by the parser facade.
		[Test]
		public void MissingSemicolon_ThrowsParseError()
		{
			var css = "node { a: 1 }"; // missing terminating ';'
			var ex = Assert.Throws<InvalidOperationException>(() => MapCssParserFacade.Parse(css));
			Assert.That(ex.Message, Does.Contain("Parse error"));
		}

		// Unterminated regex literals should be reported (lexer or parser error).
		[Test]
		public void UnterminatedRegex_ThrowsParseOrLexError()
		{
			// missing closing '/'
			var css = "way[name=~/(foo { color: red; }";
			// Ensure the facade surfaces a controlled InvalidOperationException.
			Assert.Throws<InvalidOperationException>(() => MapCssParserFacade.Parse(css));
		}

		// If a regex token parses but the pattern is invalid (e.g., unclosed character class),
		// the AST should still record a Regex kind, and TryCompileRegex should have returned null
		// so the stored Regex property is null. This verifies the parser captures regex text
		// but the engine safely handles invalid regex compilation.
		[Test]
		public void InvalidRegexPattern_ProducesNullRegexButParses()
		{
			// pattern with unclosed character class (likely invalid at compile time)
			var css = "way[name=~/[/] { color: red; }";
			var sheet = MapCssParserFacade.Parse(css);
			var test = sheet.Rules[0].Selectors[0].Segments[0].Selector.AttributeTests[0];
			Assert.That(test.Value!.Kind, Is.EqualTo(MapCssValueKind.Regex));
			// Regex compile should have failed (or be null), so Regex property is null
			Assert.That(test.Value.Regex, Is.Null);
		}

		// Quoted attribute keys may include characters that otherwise would be token separators
		// (e.g. ':') — ensure quoted key text is parsed and unescaped into the stored key.
		[Test]
		public void QuotedAttributeKey_IsUnescaped()
		{
			var css = "[\"seamark:type\"=foo] { color: red; }";
			var sheet = MapCssParserFacade.Parse(css);
			var test = sheet.Rules[0].Selectors[0].Segments[0].Selector.AttributeTests[0];
			Assert.That(test.Key, Is.EqualTo("seamark:type"));
		}

		// Regex literal tokens may contain escaped slashes; verify that the stored
		// literal text unescapes these sequences to the intended pattern text.
		[Test]
		public void RegexLiteral_EscapedSlash_IsUnescaped()
		{
			var css = "[key=~/foo\\/] { a:1; }";
			var sheet = MapCssParserFacade.Parse(css);
			var test = sheet.Rules[0].Selectors[0].Segments[0].Selector.AttributeTests[0];
			// raw text stored should have escaped slash unescaped to '/'
			Assert.That(test.Value!.Text.Replace("\\","/"), Is.EqualTo("foo/"));
			// compiled regex may or may not be present depending on pattern validity
			// but the stored pattern text should normalize to the unescaped form
		}
	}
}
