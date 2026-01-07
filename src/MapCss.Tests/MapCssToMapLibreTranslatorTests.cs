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

	[Test]
	public void PropertyContext_StoresInputs_AndRejectsNullName()
	{
		var context = new MapLibrePropertyContext("width", MapLibreGeometryType.Line, MapLibreLayerType.Line);
		Assert.That(context.PropertyName, Is.EqualTo("width"));
		Assert.That(context.Geometry, Is.EqualTo(MapLibreGeometryType.Line));
		Assert.That(context.LayerType, Is.EqualTo(MapLibreLayerType.Line));

		Assert.Throws<ArgumentNullException>(() => _ = new MapLibrePropertyContext(null!, MapLibreGeometryType.Line, MapLibreLayerType.Line));
	}

	[Test]
	public void PropertyMapping_StoresInputs_AndRejectsNullTarget()
	{
		var mapping = new MapLibrePropertyMapping("line-join", isLayout: true);
		Assert.That(mapping.Target, Is.EqualTo("line-join"));
		Assert.That(mapping.IsLayout, Is.True);

		Assert.Throws<ArgumentNullException>(() => _ = new MapLibrePropertyMapping(null!, isLayout: false));
	}

	[Test]
	public void PropertyMappingResolver_CanOverridePaintProperty()
	{
		var css = "way { width: 3; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			PropertyMappingResolver = ctx =>
				string.Equals(ctx.PropertyName, "width", StringComparison.OrdinalIgnoreCase)
					? new MapLibrePropertyMapping("line-gap-width", isLayout: false)
					: null
		});

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Type, Is.EqualTo(MapLibreLayerType.Line));
		Assert.That(layer.Paint.ContainsKey("line-width"), Is.False);
		Assert.That(layer.Paint["line-gap-width"], Is.EqualTo(3d));
	}

	[Test]
	public void PropertyMappingResolver_CanOverrideLayoutProperty()
	{
		var css = "way { linecap: round; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			PropertyMappingResolver = ctx =>
				string.Equals(ctx.PropertyName, "linecap", StringComparison.OrdinalIgnoreCase)
					? new MapLibrePropertyMapping("line-join", isLayout: true)
					: null
		});

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Layout.ContainsKey("line-cap"), Is.False);
		Assert.That(layer.Layout["line-join"], Is.EqualTo("round"));
	}

	[Test]
	public void GeometryResolver_OverridesInferredGeometry()
	{
		var css = "node { width: 2; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			GeometryResolver = _ => MapLibreGeometryType.Line
		});

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Type, Is.EqualTo(MapLibreLayerType.Line));
		Assert.That(layer.Paint.ContainsKey("line-width"), Is.True);
		Assert.That(layer.Paint["line-width"], Is.EqualTo(2d));
	}

	[Test]
	public void StrictExpressions_ThrowsOnUnsupportedExpression()
	{
		var css = "node { width: unknown_func(1); }";
		var translator = new MapCssToMapLibreTranslator();
		Assert.Throws<InvalidOperationException>(() => translator.Translate(css, new MapLibreTranslationOptions { StrictExpressions = true }));
	}

	[Test]
	public void ClassPropertyResolver_ReturningEmptyKey_DropsRuleWithWarning()
	{
		var css = "node.test { color: red; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			ClassPropertyResolver = _ => ""
		});

		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("empty key", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void PseudoClassSelector_IsRejectedWithWarning()
	{
		var css = "node:closed { color: red; }";
		var result = Translate(css);
		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Pseudo-classes", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void CanvasSelector_IsRejectedWithWarning()
	{
		var css = "canvas { color: red; }";
		var result = Translate(css);
		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Canvas selectors", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void LayerIds_UseNormalizedStylesheetIdAndPrefix()
	{
		var css = "node { text: \"name\"; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			StylesheetId = "My Style!",
			LayerIdPrefix = "PRE-"
		});

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Id, Is.EqualTo("PRE-my_style__0_default_symbol"));
	}

	[Test]
	public void SetDeclarations_AreIgnoredWithWarning()
	{
		var css = "node { set foo; width: 2; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Not.Empty);
		Assert.That(result.Warnings.Any(w => w.Property == "set" && w.Message.Contains("ignored", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void MultiSegmentSelectors_AreRejectedWithWarning()
	{
		var css = "node > way { color: red; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("multi-segment", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void AttributeOperators_TranslateIntoMapLibreFilters()
	{
		var css = "node.testclass[foo?!][bar][!baz][height=2.5][flag=true][name*=oba][name^=foo][name$=bar][name~=baz][name!~=qux] { text: \"name\"; }";
		var result = Translate(css, new MapLibreTranslationOptions
		{
			ClassPropertyResolver = cls => $"custom:{cls}"
		});

		var layer = result.Style.Layers.Single();

		var expectedFilter = new object[]
		{
			"all",
			new object[] { "==", new object[] { "geometry-type" }, "Point" },
			new object[] { "==", new object[] { "get", "custom:testclass" }, true },
			new object[] { "!", new object[] { "has", "foo" } },
			new object[] { "has", "bar" },
			new object[] { "!", new object[] { "has", "baz" } },
			new object[] { "==", new object[] { "get", "height" }, 2.5d },
			new object[] { "==", new object[] { "get", "flag" }, true },
			new object[] { "!=", new object[] { "index-of", "oba", new object[] { "get", "name" } }, -1 },
			new object[] { "starts-with", new object[] { "get", "name" }, "foo" },
			new object[] { "ends-with", new object[] { "get", "name" }, "bar" },
			new object[] { "!=", new object[] { "index-of", "baz", new object[] { "get", "name" } }, -1 },
			new object[] { "==", new object[] { "index-of", "qux", new object[] { "get", "name" } }, -1 }
		};

		Assert.That(layer.Filter, Is.EqualTo(expectedFilter));
		Assert.That(result.Warnings.Any(w => w.Property == "foo" && w.Message.Contains("not-truthy", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void Expressions_TranslateConcatAnyHasTagKeyAndComparisons()
	{
		var css = @"
node {
	text: ""name"";
	text-color: cond(has_tag_key(""foo""), concat(""a\n"", tag(""bar"")), any(tag(""x""), ""fallback""));
}
way {
	width: cond(tag(""depth"")>=10, 2, 1);
}";
		var result = Translate(css);

		var symbol = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Symbol);
		var expectedTextColor = new object[]
		{
			"case",
			new object[] { "has", "foo" },
			new object[] { "concat", "a\n", new object[] { "get", "bar" } },
			new object[] { "coalesce", new object[] { "get", "x" }, "fallback" }
		};
		Assert.That(symbol.Paint["text-color"], Is.EqualTo(expectedTextColor));

		var line = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Line);
		var expectedLineWidth = new object[]
		{
			"case",
			new object[] { ">=", new object[] { "get", "depth" }, 10d },
			2d,
			1d
		};
		Assert.That(line.Paint["line-width"], Is.EqualTo(expectedLineWidth));
	}

	[Test]
	public void UnsupportedExpressions_FallBackToLiteralWithWarning_WhenNotStrict()
	{
		var css = "node { text: tag(1); }";
		var result = Translate(css, new MapLibreTranslationOptions { StrictExpressions = false });

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Layout["text-field"], Is.EqualTo("tag(1)"));
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Unsupported expression", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void NumericTranslation_ParsesNamedAndRelativeValues_AndWarnsOnBadDashes()
	{
		var css = "way { width: thickest; opacity: +0.5; dashes: a, b; linecap: round; }";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		Assert.That(layer.Paint["line-width"], Is.EqualTo(3d));
		Assert.That(layer.Paint["line-opacity"], Is.EqualTo(0.5d));
		Assert.That(layer.Layout["line-cap"], Is.EqualTo("round"));
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Relative values", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("dash values", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void IconSize_CombinesWidthAndHeight_AndScalesUsingIconBaseSize()
	{
		var css = @"
node {
	icon-image: ""http://example/icon.svg"";
	icon-width: 16;
	icon-height: 32;
	icon-offset-x: 1;
	icon-offset-y: -2;
	text: ""name"";
	text-anchor-horizontal: left;
	text-anchor-vertical: above;
	text-position: center;
	z-index: 3;
}";
		var result = Translate(css, new MapLibreTranslationOptions { IconBaseSize = 16 });

		var layer = result.Style.Layers.Single();
		Assert.That(layer.Layout["icon-size"], Is.EqualTo(2d));
		Assert.That((double[])layer.Layout["icon-offset"], Is.EqualTo(new[] { 1d, -2d }));
		Assert.That(layer.Layout["text-anchor"], Is.EqualTo("center"));
		Assert.That(layer.Layout["symbol-sort-key"], Is.EqualTo(3d));
		Assert.That(result.Warnings.Any(w => w.Message.Contains("max(icon-width, icon-height)", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void RepeatImageWithoutGeometryInference_EmitsGeometryAndInferenceWarnings()
	{
		var css = "* { repeat-image: \"http://example/repeat.svg\"; repeat-image-width: 16; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Not.Empty);
		Assert.That(result.Style.Layers.Any(l => l.Type == MapLibreLayerType.Symbol), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Could not infer geometry", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Repeat-image is only supported", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void Properties_MapAcrossFillCircleAndSymbolLayerTypes()
	{
		var css = @"
area {
	fill-color: blue;
	fill-opacity: 0.25;
	fill-image: ""http://example/pattern.svg"";
	color: gray;
	opacity: 0.8;
}
node {
	symbol-size: 4;
	symbol-fill-color: red;
	symbol-fill-opacity: 0.75;
	symbol-stroke-color: black;
}
node.label {
	text: depth;
	text-color: blue;
	text-halo-color: white;
	text-halo-radius: 2;
	font-family: ""Arial, Sans"";
	font-size: 12;
	icon-image: ""http://example/icon.svg"";
	icon-opacity: 0.8;
	icon-orientation: 45;
	icon-width: cond(tag(""w"") != """", 32, 16);
	icon-offset-x: cond(tag(""ox"") != """", 1, 2);
	icon-offset-y: cond(tag(""oy"") != """", 3, 4);
}";
		var result = Translate(css, new MapLibreTranslationOptions { IconBaseSize = 16 });

		var fill = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Fill);
		Assert.That(fill.Paint["fill-color"], Is.EqualTo("blue"));
		Assert.That(fill.Paint["fill-opacity"], Is.EqualTo(0.8d));
		Assert.That(fill.Paint["fill-pattern"], Is.EqualTo("http://example/pattern.svg"));
		Assert.That(fill.Paint["fill-outline-color"], Is.EqualTo("gray"));

		var circle = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Circle);
		Assert.That(circle.Paint["circle-radius"], Is.EqualTo(4d));
		Assert.That(circle.Paint["circle-color"], Is.EqualTo("red"));
		Assert.That(circle.Paint["circle-opacity"], Is.EqualTo(0.75d));
		Assert.That(circle.Paint["circle-stroke-color"], Is.EqualTo("black"));

		var symbol = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Symbol);
		Assert.That(symbol.Paint["text-color"], Is.EqualTo("blue"));
		Assert.That(symbol.Paint["text-halo-color"], Is.EqualTo("white"));
		Assert.That(symbol.Paint["text-halo-width"], Is.EqualTo(2d));
		Assert.That(symbol.Paint["icon-opacity"], Is.EqualTo(0.8d));
		Assert.That(symbol.Layout["text-size"], Is.EqualTo(12d));
		Assert.That(symbol.Layout["icon-rotate"], Is.EqualTo(45d));
		Assert.That(symbol.Layout.ContainsKey("icon-size"), Is.True);
		Assert.That(result.Warnings.Any(w => w.Property == "icon-offset-x" && w.Message.Contains("expects a numeric value", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Property == "icon-offset-y" && w.Message.Contains("expects a numeric value", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void UnsupportedOrMismatchedProperties_EmitWarnings()
	{
		var css = @"
node {
	text: ""name"";
	color: red;
	width: 2;
	opacity: 0.5;
	dashes: 1, 2;
	fill-color: blue;
	fill-opacity: 0.3;
	fill-image: ""http://example/pattern.svg"";
	text-halo-opacity: 0.5;
	font-style: italic;
	title: test;
	antialiasing: full;
}";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Not.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("does not support 'color'", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("does not support 'width'", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("only applies to fill layers", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("halo opacity", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("metadata", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void Translate_ThrowsOnNullInput()
	{
		var translator = new MapCssToMapLibreTranslator();
		Assert.Throws<ArgumentNullException>(() => translator.Translate(null!));
	}

	[Test]
	public void RuleWithOnlySetDeclarations_ProducesNoLayers()
	{
		var css = "node { set foo; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Empty);
		Assert.That(result.Warnings.Any(w => w.Property == "set"), Is.True);
	}

	[Test]
	public void WildcardGeometryInference_UsesPropertyHeuristics()
	{
		var css = @"
* { fill-color: blue; }
* { width: 2; }
* { text: ""name""; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers.Count, Is.EqualTo(3));
		Assert.That(result.Style.Layers.Any(l => l.Type == MapLibreLayerType.Fill), Is.True);
		Assert.That(result.Style.Layers.Any(l => l.Type == MapLibreLayerType.Line), Is.True);
		Assert.That(result.Style.Layers.Any(l => l.Type == MapLibreLayerType.Symbol), Is.True);
	}

	[Test]
	public void ExpressionParser_SupportsMoreOperatorsGroupingAndBooleans()
	{
		var css = @"
node {
	text: cond((tag(""a"")<1)==true, ""lt"", cond(tag(""b"")>1, ""gt"", ""other""));
	font-size: cond(tag(""z"")<=5, 12, 10);
	text-color: cond(false, red, blue);
}";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		var expectedText = new object[]
		{
			"case",
			new object[] { "==", new object[] { "<", new object[] { "get", "a" }, 1d }, true },
			"lt",
			new object[] { "case", new object[] { ">", new object[] { "get", "b" }, 1d }, "gt", "other" }
		};
		Assert.That(layer.Layout["text-field"], Is.EqualTo(expectedText));

		var expectedFontSize = new object[]
		{
			"case",
			new object[] { "<=", new object[] { "get", "z" }, 5d },
			12d,
			10d
		};
		Assert.That(layer.Layout["text-size"], Is.EqualTo(expectedFontSize));

		var expectedTextColor = new object[]
		{
			"case",
			false,
			"red",
			"blue"
		};
		Assert.That(layer.Paint["text-color"], Is.EqualTo(expectedTextColor));
	}

	[Test]
	public void InvalidValues_AreIgnoredAndReportedAsWarnings()
	{
		var css = @"
way {
	color: black;
	width: nope;
	opacity: nope;
	dashes: none;
}
area {
	fill-color: blue;
	fill-opacity: nope;
}
node {
	symbol-fill-color: red;
	symbol-size: nope;
	symbol-shape: square;
}
node {
	text: ""name"";
	text-halo-radius: nope;
	font-size: nope;
	z-index: nope;
}
way {
	casing-width: 2;
	casing-opacity: nope;
	repeat-image: ""http://example/repeat.svg"";
	repeat-image-width: nope;
	repeat-image-phase: 1;
}";
		var result = Translate(css);

		Assert.That(result.Style.Layers, Is.Not.Empty);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Unsupported numeric value", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Could not parse any numeric dash values", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("Only circle maps directly", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(result.Warnings.Any(w => w.Message.Contains("MapLibre does not support phase offsets", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void ExpressionFunctions_WithEmptyArgs_ReturnDefaults()
	{
		var css = @"
node {
	text: concat();
	text-color: any();
	text-halo-color: tag();
	text-halo-radius: eval();
	icon-image: ""http://example/icon.svg"";
	icon-width: tag();
	icon-opacity: has_tag_key();
}";
		var result = Translate(css, new MapLibreTranslationOptions { IconBaseSize = 16 });
		var layer = result.Style.Layers.Single();

		Assert.That(layer.Layout["text-field"], Is.EqualTo(string.Empty));
		Assert.That(layer.Paint["text-color"], Is.EqualTo(string.Empty));
		Assert.That(layer.Paint["text-halo-color"], Is.EqualTo(string.Empty));
		Assert.That(layer.Paint["icon-opacity"], Is.EqualTo(false));
	}

	[Test]
	public void CircleLayer_MapsColorWidthAndOpacity()
	{
		var css = "node { color: red; width: 2; opacity: 0.5; }";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		Assert.That(layer.Type, Is.EqualTo(MapLibreLayerType.Circle));
		Assert.That(layer.Paint["circle-color"], Is.EqualTo("red"));
		Assert.That(layer.Paint["circle-radius"], Is.EqualTo(2d));
		Assert.That(layer.Paint["circle-opacity"], Is.EqualTo(0.5d));
	}

	[Test]
	public void CirclePropertiesOnSymbolLayer_EmitWarnings()
	{
		var css = @"
node {
	text: ""name"";
	symbol-size: 4;
	symbol-fill-color: red;
	symbol-fill-opacity: 0.5;
	symbol-stroke-color: black;
}";
		var result = Translate(css);

		Assert.That(result.Style.Layers.Single().Type, Is.EqualTo(MapLibreLayerType.Symbol));
		Assert.That(result.Warnings.Any(w => w.Message.Contains("only applies to circle layers", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void RepeatImageSpacing_MapsToSymbolSpacing()
	{
		var css = "way { repeat-image: \"http://example/repeat.svg\"; repeat-image-spacing: 8; }";
		var result = Translate(css);

		var symbol = result.Style.Layers.Single(l => l.Type == MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["symbol-placement"], Is.EqualTo("line"));
		Assert.That(symbol.Layout["symbol-spacing"], Is.EqualTo(8d));
	}

	[Test]
	public void Expressions_HandleBareValuesAndUnsupportedFunctions()
	{
		var css = @"
node {
	text: ""name"";
	text-color: #fff;
	text-halo-color: has_tag_key(1);
	text-halo-radius: cond(1, 2);
}";
		var result = Translate(css);
		var layer = result.Style.Layers.Single();

		Assert.That(layer.Paint["text-color"], Is.EqualTo("#fff"));
		Assert.That(result.Warnings.Count(w => w.Message.Contains("Unsupported expression", StringComparison.OrdinalIgnoreCase)), Is.GreaterThanOrEqualTo(2));
	}

	[Test]
	public void UnsupportedTextPosition_EmitsWarning()
	{
		var css = "node { text: \"name\"; text-position: left; }";
		var result = Translate(css);

		Assert.That(result.Style.Layers.Single().Type, Is.EqualTo(MapLibreLayerType.Symbol));
		Assert.That(result.Warnings.Any(w => w.Property == "text-position" && w.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)), Is.True);
	}
}
