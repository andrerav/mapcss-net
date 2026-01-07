using System;
using System.Collections.Generic;
using System.Linq;
using MapCss.Extensions;
using MapCss.Styling;
using NUnit.Framework;

namespace MapCss.Tests;

[TestFixture]
public class MapLibreStyleExtensionsTests
{
	private static MapLibreStyleResult Evaluate(
		string css,
		MapCssElementType elementType,
		MapLibreGeometryType geometry,
		MapLibreStyleOptions? options = null,
		IReadOnlyDictionary<string, string>? tags = null)
	{
		var element = new MapCssElement(elementType, tags ?? new Dictionary<string, string>());
		var engine = new MapCssStyleEngine(css);
		var style = engine.Evaluate(new MapCssQuery(element));
		return style.ToMapLibreStyle(geometry, options);
	}

	private static MapLibreStyleLayer GetLayer(
		MapLibreStyleResult result,
		MapLibreLayerType layerType,
		string subpart = "")
	{
		Assert.That(result.Layers.ContainsKey(subpart), Is.True);
		var layers = result.Layers[subpart];
		var layer = layers.SingleOrDefault(x => x.LayerType == layerType);
		Assert.That(layer, Is.Not.Null, $"Missing layer type {layerType}.");
		return layer!;
	}

	[Test]
	public void PointGeometryMapsCircleAndSymbolProperties()
	{
		var css = @"
node {
	symbol-size: 4;
	symbol-fill-color: red;
	symbol-stroke-color: black;
	symbol-fill-opacity: 0.75;
	text: ""seamark:name"";
	text-color: blue;
	text-halo-colour: white;
	text-halo-radius: 2;
	font-family: ""Arial, Sans"";
	font-size: 12;
	icon-image: ""http://example/icon.svg"";
	icon-width: 16;
	icon-offset-x: 1;
	icon-offset-y: -2;
	icon-opacity: 0.8;
	icon-orientation: 45;
	z-index: 3;
}";
		var result = Evaluate(
			css,
			MapCssElementType.Node,
			MapLibreGeometryType.Point,
			new MapLibreStyleOptions { IconBaseSize = 16 });

		var circle = GetLayer(result, MapLibreLayerType.Circle);
		Assert.That(circle.Paint["circle-radius"], Is.EqualTo(4d));
		Assert.That(circle.Paint["circle-color"], Is.EqualTo("red"));
		Assert.That(circle.Paint["circle-stroke-color"], Is.EqualTo("black"));
		Assert.That(circle.Paint["circle-opacity"], Is.EqualTo(0.75d));

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		var textField = (object[])symbol.Layout["text-field"];
		Assert.That(textField, Is.EqualTo(new object[] { "get", "seamark:name" }));
		Assert.That(symbol.Paint["text-color"], Is.EqualTo("blue"));
		Assert.That(symbol.Paint["text-halo-color"], Is.EqualTo("white"));
		Assert.That(symbol.Paint["text-halo-width"], Is.EqualTo(2d));
		Assert.That((string[])symbol.Layout["text-font"], Is.EqualTo(new[] { "Arial", "Sans" }));
		Assert.That(symbol.Layout["text-size"], Is.EqualTo(12d));
		Assert.That(symbol.Layout["icon-image"], Is.EqualTo("http://example/icon.svg"));
		Assert.That(symbol.Layout["icon-size"], Is.EqualTo(1d));
		Assert.That((double[])symbol.Layout["icon-offset"], Is.EqualTo(new[] { 1d, -2d }));
		Assert.That(symbol.Paint["icon-opacity"], Is.EqualTo(0.8d));
		Assert.That(symbol.Layout["icon-rotate"], Is.EqualTo(45d));
		Assert.That(symbol.Layout["symbol-sort-key"], Is.EqualTo(3d));
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-image"), Is.True);
	}

	[Test]
	public void LineGeometryMapsLineAndRepeatImageProperties()
	{
		var css = @"
way {
	width: 3;
	color: black;
	opacity: 0.5;
	dashes: 10, 5;
	linecap: square;
	casing-width: 2;
	repeat-image: ""http://example/marker.svg"";
	repeat-image-width: 16;
	repeat-image-spacing: 8;
	repeat-image-phase: 4;
}";
		var result = Evaluate(
			css,
			MapCssElementType.Way,
			MapLibreGeometryType.Line,
			new MapLibreStyleOptions { IconBaseSize = 16 });

		var line = GetLayer(result, MapLibreLayerType.Line);
		Assert.That(line.Paint["line-width"], Is.EqualTo(3d));
		Assert.That(line.Paint["line-color"], Is.EqualTo("black"));
		Assert.That(line.Paint["line-opacity"], Is.EqualTo(0.5d));
		Assert.That((double[])line.Paint["line-dasharray"], Is.EqualTo(new[] { 10d, 5d }));
		Assert.That(line.Layout["line-cap"], Is.EqualTo("square"));
		Assert.That(line.Warnings.Any(w => w.MapCssProperty == "casing-width"), Is.True);

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["symbol-placement"], Is.EqualTo("line"));
		Assert.That(symbol.Layout["icon-image"], Is.EqualTo("http://example/marker.svg"));
		Assert.That(symbol.Layout["icon-size"], Is.EqualTo(1d));
		Assert.That(symbol.Layout["symbol-spacing"], Is.EqualTo(8d));
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "repeat-image-phase"), Is.True);
	}

	[Test]
	public void PolygonGeometryMapsFillAndOutlineProperties()
	{
		var css = @"
area {
	fill-color: blue;
	fill-opacity: 0.25;
	fill-image: ""http://example/pattern.svg"";
	color: gray;
	width: 2;
}";
		var result = Evaluate(css, MapCssElementType.Area, MapLibreGeometryType.Polygon);

		var fill = GetLayer(result, MapLibreLayerType.Fill);
		Assert.That(fill.Paint["fill-color"], Is.EqualTo("blue"));
		Assert.That(fill.Paint["fill-opacity"], Is.EqualTo(0.25d));
		Assert.That(fill.Paint["fill-pattern"], Is.EqualTo("http://example/pattern.svg"));
		Assert.That(fill.Warnings.Any(w => w.MapCssProperty == "fill-image"), Is.True);

		var line = GetLayer(result, MapLibreLayerType.Line);
		Assert.That(line.Paint["line-color"], Is.EqualTo("gray"));
		Assert.That(line.Paint["line-width"], Is.EqualTo(2d));
	}

	[Test]
	public void TextHeuristicTreatsTagKeysAndSkipsAuto()
	{
		var depthResult = Evaluate("node { text: depth; }", MapCssElementType.Node, MapLibreGeometryType.Point);
		var depthLayer = GetLayer(depthResult, MapLibreLayerType.Symbol);
		var depthText = (object[])depthLayer.Layout["text-field"];
		Assert.That(depthText, Is.EqualTo(new object[] { "get", "depth" }));

		var autoResult = Evaluate("node { text: auto; }", MapCssElementType.Node, MapLibreGeometryType.Point);
		var autoLayer = GetLayer(autoResult, MapLibreLayerType.Symbol);
		Assert.That(autoLayer.Layout["text-field"], Is.EqualTo("auto"));

		var colonResult = Evaluate("node { text: \"seamark:name\"; }", MapCssElementType.Node, MapLibreGeometryType.Point);
		var colonLayer = GetLayer(colonResult, MapLibreLayerType.Symbol);
		var colonText = (object[])colonLayer.Layout["text-field"];
		Assert.That(colonText, Is.EqualTo(new object[] { "get", "seamark:name" }));
	}

	[Test]
	public void QueryOverloadInfersGeometry()
	{
		var css = "area { fill-color: blue; }";
		var element = new MapCssElement(MapCssElementType.Area, new Dictionary<string, string>());
		var engine = new MapCssStyleEngine(css);
		var style = engine.Evaluate(new MapCssQuery(element));

		var result = style.ToMapLibreStyle(new MapCssQuery(element));
		var fill = GetLayer(result, MapLibreLayerType.Fill);
		Assert.That(fill.Paint["fill-color"], Is.EqualTo("blue"));
	}

	[Test]
	public void UnsupportedPropertiesProduceWarnings()
	{
		var css = @"
node {
	symbol-shape: square;
	text-halo-opacity: 0.5;
	font-style: italic;
	default-points: false;
	antialiasing: full;
	unknown-prop: 1;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);

		var circle = GetLayer(result, MapLibreLayerType.Circle);
		Assert.That(circle.Warnings.Any(w => w.MapCssProperty == "symbol-shape"), Is.True);

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "text-halo-opacity"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "font-style"), Is.True);

		var anyWarnings = result.Layers[""].SelectMany(x => x.Warnings).ToList();
		Assert.That(anyWarnings.Any(w => w.MapCssProperty == "default-points"), Is.True);
		Assert.That(anyWarnings.Any(w => w.MapCssProperty == "antialiasing"), Is.True);
		Assert.That(anyWarnings.Any(w => w.MapCssProperty == "unknown-prop"), Is.True);
	}

	[Test]
	public void TextAnchorsCombineIntoMapLibreAnchor()
	{
		var css = @"
node {
	text: ""name"";
	text-anchor-horizontal: left;
	text-anchor-vertical: above;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["text-anchor"], Is.EqualTo("top-left"));
	}

	[Test]
	public void IconSizeWithoutBaseEmitsWarningAndUsesRawValue()
	{
		var css = @"
node {
	icon-image: ""http://example/icon.svg"";
	icon-width: 24;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["icon-size"], Is.EqualTo(24d));
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-width"), Is.True);
	}

	[Test]
	public void RepeatImageOnPointEmitsGeometryWarning()
	{
		var css = @"
node {
	repeat-image: ""http://example/marker.svg"";
	repeat-image-width: 16;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["symbol-placement"], Is.EqualTo("line"));
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "repeat-image"), Is.True);
	}

	[Test]
	public void TextPositionCenterSetsCenterAnchor()
	{
		var css = @"
node {
	text: ""name"";
	text-position: center;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["text-anchor"], Is.EqualTo("center"));
	}

	[Test]
	public void PointGeometry_LineOnlyPropertiesProduceFallbackWarnings()
	{
		var css = @"
node {
	dashes: 1, 2;
	linecap: round;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);

		var circle = GetLayer(result, MapLibreLayerType.Circle);
		Assert.That(circle.Paint, Is.Empty);
		Assert.That(circle.Layout, Is.Empty);
		Assert.That(circle.Warnings.Any(w => w.Message.Contains("No compatible layer", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void PointGeometry_UnassignedWarningsAttachToExistingLayer()
	{
		var css = @"
node {
	text: ""name"";
	dashes: 1, 2;
	linecap: round;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Warnings.Any(w => w.Message.Contains("No compatible layer", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void RepeatImageWidthWithoutIconBaseSizeEmitsScaleWarning()
	{
		var css = @"
way {
	repeat-image: ""http://example/marker.svg"";
	repeat-image-width: 16;
}";
		var result = Evaluate(css, MapCssElementType.Way, MapLibreGeometryType.Line, new MapLibreStyleOptions());

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["icon-size"], Is.EqualTo(16d));
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "repeat-image-width" && w.Message.Contains("expects a scale", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void TextValueIsTagOverride_ForcesGetExpression()
	{
		var css = "node { text: foo; }";
		var result = Evaluate(
			css,
			MapCssElementType.Node,
			MapLibreGeometryType.Point,
			new MapLibreStyleOptions { TextValueIsTag = _ => true });

		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["text-field"], Is.EqualTo(new object[] { "get", "foo" }));
	}

	[Test]
	public void IconWidthAndHeightMismatch_UsesMaxAndWarns()
	{
		var css = @"
node {
	icon-image: ""http://example/icon.svg"";
	icon-width: 16;
	icon-height: 32;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point, new MapLibreStyleOptions { IconBaseSize = 16 });
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);
		Assert.That(symbol.Layout["icon-size"], Is.EqualTo(2d));
		Assert.That(symbol.Warnings.Any(w => w.Message.Contains("max(icon-width, icon-height)", StringComparison.OrdinalIgnoreCase)), Is.True);
	}

	[Test]
	public void RelativeNumericValuesAreParsed()
	{
		var css = "way { width: +2; }";
		var result = Evaluate(css, MapCssElementType.Way, MapLibreGeometryType.Line);
		var line = GetLayer(result, MapLibreLayerType.Line);
		Assert.That(line.Paint["line-width"], Is.EqualTo(2d));
	}

	[Test]
	public void ToMapLibreStyle_ThrowsOnNullInputs()
	{
		Assert.Throws<ArgumentNullException>(() => MapLibreStyleExtensions.ToMapLibreStyle(null!, MapLibreGeometryType.Point));

		var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
		var style = new MapCssStyleEngine("node { text: \"name\"; }").Evaluate(new MapCssQuery(element));
		Assert.Throws<ArgumentNullException>(() => style.ToMapLibreStyle((MapCssQuery)null!));
	}

	[Test]
	public void InvalidNumericValuesEmitWarnings()
	{
		var css = @"
node {
	text: ""name"";
	text-position: left;
	text-halo-radius: nope;
	font-size: nope;
	z-index: nope;
	icon-image: ""http://example/icon.svg"";
	icon-opacity: nope;
	icon-offset-x: nope;
	icon-offset-y: nope;
	icon-orientation: nope;
}";
		var result = Evaluate(css, MapCssElementType.Node, MapLibreGeometryType.Point);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);

		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "text-position"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "text-halo-radius"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "font-size"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "z-index"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-opacity"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-offset-x"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-offset-y"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "icon-orientation"), Is.True);
	}

	[Test]
	public void InvalidRepeatImageValuesEmitWarnings()
	{
		var css = "way { repeat-image-width: nope; repeat-image-spacing: nope; }";
		var result = Evaluate(css, MapCssElementType.Way, MapLibreGeometryType.Line);
		var symbol = GetLayer(result, MapLibreLayerType.Symbol);

		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "repeat-image-width"), Is.True);
		Assert.That(symbol.Warnings.Any(w => w.MapCssProperty == "repeat-image-spacing"), Is.True);
	}
}
