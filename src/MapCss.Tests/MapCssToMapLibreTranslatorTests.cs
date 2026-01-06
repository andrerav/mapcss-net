using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapCss.Extensions;
using NUnit.Framework;

namespace MapCss.Tests;

[TestFixture]
public class MapCssToMapLibreTranslatorTests
{
	private static MapLibreTranslationResult Translate(string css, MapLibreTranslationOptions? options = null)
	{
		var translator = new MapCssToMapLibreTranslator();
		return translator.Translate(css, options);
	}

	private static string ReadFixture(string fileName)
	{
		var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fileName);
		return File.ReadAllText(path);
	}

	private static string NormalizeNewlines(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

	[Test]
	public void SelectorFiltersIncludeGeometryClassesAndAttributes()
	{
		var css = "node|z12-15[\"seamark:type\"=buoy].lighted { color: red; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers.Count, Is.EqualTo(1));
		var layer = result.Style.Layers[0];
		Assert.That(layer.MinZoom, Is.EqualTo(12d));
		Assert.That(layer.MaxZoom, Is.EqualTo(15d));

		var expectedFilter = new object[]
		{
			"all",
			new object[] { "==", new object[] { "geometry-type" }, "Point" },
			new object[] { "==", new object[] { "get", "class:lighted" }, true },
			new object[] { "==", new object[] { "get", "seamark:type" }, "buoy" }
		};

		Assert.That(layer.Filter, Is.EqualTo(expectedFilter));
		Assert.That(layer.Paint["circle-color"], Is.EqualTo("red"));
	}

	[Test]
	public void CasingAndRepeatImageCreateExtraLayers()
	{
		var css = @"
way {
	width: 2;
	color: black;
	casing-width: 4;
	casing-color: white;
	repeat-image: ""http://example/repeat.svg"";
	repeat-image-width: 16;
}";
		var result = Translate(css);

		Assert.That(result.Style.Layers.Count, Is.EqualTo(3));
		Assert.That(result.Style.Layers.Any(layer => layer.Type == MapLibreLayerType.Line && layer.Id.Contains("casing_line", StringComparison.Ordinal)), Is.True);
		Assert.That(result.Style.Layers.Any(layer => layer.Type == MapLibreLayerType.Symbol && layer.Id.Contains("repeat_symbol", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public void ExpressionValuesTranslateIntoMapLibreExpressions()
	{
		var css = "node { text: cond(tag(\"seamark:name\")!=\"\", tag(\"seamark:name\"), \"fallback\"); }";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		var expected = new object[]
		{
			"case",
			new object[] { "!=", new object[] { "get", "seamark:name" }, "" },
			new object[] { "get", "seamark:name" },
			"fallback"
		};

		Assert.That(layer.Layout["text-field"], Is.EqualTo(expected));
	}

	[Test]
	public void RegexSelectorsAreDroppedWithWarnings()
	{
		var css = "node[\"name\"=~/foo/] { color: red; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(warning => warning.Message.Contains("Regex selectors", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void StyleDocumentProvidesMinimumMapLibreFields()
	{
		var css = "node { text: \"name\"; }";
		var result = Translate(css);

		Assert.That(result.Style.Version, Is.EqualTo(8));
		Assert.That(result.Style.Layers, Is.Not.Empty);

		foreach (var layer in result.Style.Layers)
		{
			Assert.That(layer.Id, Is.Not.Empty);
			Assert.That(layer.Paint, Is.Not.Null);
			Assert.That(layer.Layout, Is.Not.Null);
		}
	}

	[Test]
	public void GoldenTranslationMatchesExpectedJson()
	{
		var css = ReadFixture("TranslatorSample.mapcss");
		var expected = NormalizeNewlines(ReadFixture("TranslatorSample.json"));

		var result = Translate(css);
		var jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};
		jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

		var actual = NormalizeNewlines(JsonSerializer.Serialize(result.Style, jsonOptions));
		Assert.That(actual, Is.EqualTo(expected));
	}

	[Test]
	public void CondExpressionInTextColorTranslatesIntoCaseExpression()
	{
		var css = "node { text-color: cond(tag(\"seamark:type\")==\"a\", darkmagenta, black); }";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		var expected = new object[]
		{
			"case",
			new object[] { "==", new object[] { "get", "seamark:type" }, "a" },
			"darkmagenta",
			"black"
		};

		Assert.That(layer.Paint["text-color"], Is.EqualTo(expected));
	}

	[Test]
	public void CondExpressionInTextColorSerializesAsJsonArray()
	{
		var css = "node { text-color: cond(tag(\"seamark:type\")==\"a\", darkmagenta, black); }";
		var result = Translate(css);

		var jsonOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};
		jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

		var json = JsonSerializer.Serialize(result.Style, jsonOptions);
		using var doc = JsonDocument.Parse(json);

		var layers = doc.RootElement.GetProperty("layers");
		Assert.That(layers.GetArrayLength(), Is.GreaterThanOrEqualTo(1));

		var paint = layers[0].GetProperty("paint");
		var textColor = paint.GetProperty("text-color");
		Assert.That(textColor.ValueKind, Is.EqualTo(JsonValueKind.Array));
		Assert.That(textColor[0].GetString(), Is.EqualTo("case"));
	}
}
