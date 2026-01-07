using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MapCss.Styling;

namespace MapCss.Extensions;

/// <summary>
/// Provides configuration options for translating MapCSS into MapLibre layers.
/// </summary>
public sealed class MapLibreTranslationOptions
{
	/// <summary>
	/// Gets the base identifier used to construct deterministic layer IDs.
	/// </summary>
	public string? StylesheetId { get; init; }

	/// <summary>
	/// Gets the optional prefix applied to generated layer IDs.
	/// </summary>
	public string? LayerIdPrefix { get; init; }

	/// <summary>
	/// Gets the base pixel size used to scale icon widths/heights into MapLibre icon-size values.
	/// </summary>
	public double? IconBaseSize { get; init; }

	/// <summary>
	/// Gets the function used to resolve class names into tile property keys.
	/// </summary>
	public Func<string, string>? ClassPropertyResolver { get; init; }

	/// <summary>
	/// Gets the function used to override MapCSS-to-MapLibre property mappings.
	/// </summary>
	public Func<MapLibrePropertyContext, MapLibrePropertyMapping?>? PropertyMappingResolver { get; init; }

	/// <summary>
	/// Gets the function used to override geometry inference when selectors are ambiguous.
	/// </summary>
	public Func<MapCssElementType?, MapLibreGeometryType?>? GeometryResolver { get; init; }

	/// <summary>
	/// Gets a value indicating whether unsupported expressions should throw instead of falling back to literals.
	/// </summary>
	public bool StrictExpressions { get; init; }
}

/// <summary>
/// Describes the context used to resolve a MapCSS property into a MapLibre property.
/// </summary>
public sealed class MapLibrePropertyContext
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibrePropertyContext"/> class.
	/// </summary>
	/// <param name="propertyName">The MapCSS property name.</param>
	/// <param name="geometry">The inferred geometry for the rule.</param>
	/// <param name="layerType">The MapLibre layer type selected for the rule.</param>
	public MapLibrePropertyContext(string propertyName, MapLibreGeometryType geometry, MapLibreLayerType layerType)
	{
		PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
		Geometry = geometry;
		LayerType = layerType;
	}

	/// <summary>
	/// Gets the MapCSS property name.
	/// </summary>
	public string PropertyName { get; }

	/// <summary>
	/// Gets the inferred geometry for the rule.
	/// </summary>
	public MapLibreGeometryType Geometry { get; }

	/// <summary>
	/// Gets the MapLibre layer type selected for the rule.
	/// </summary>
	public MapLibreLayerType LayerType { get; }
}

/// <summary>
/// Represents an override mapping for a MapCSS property.
/// </summary>
public sealed class MapLibrePropertyMapping
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibrePropertyMapping"/> class.
	/// </summary>
	/// <param name="target">The MapLibre property name.</param>
	/// <param name="isLayout">True to map into layout properties; false to map into paint properties.</param>
	public MapLibrePropertyMapping(string target, bool isLayout)
	{
		Target = target ?? throw new ArgumentNullException(nameof(target));
		IsLayout = isLayout;
	}

	/// <summary>
	/// Gets the MapLibre property name.
	/// </summary>
	public string Target { get; }

	/// <summary>
	/// Gets a value indicating whether the target property should be placed in layout.
	/// </summary>
	public bool IsLayout { get; }
}

/// <summary>
/// Translates MapCSS stylesheets into MapLibre layer definitions.
/// </summary>
public sealed class MapCssToMapLibreTranslator
{
	private static readonly Regex TagKeyRegex = new("^[A-Za-z0-9_:-]+$", RegexOptions.Compiled);
	private static readonly Regex NumberRegex = new("[-+]?[0-9]*\\.?[0-9]+", RegexOptions.Compiled);
	private static readonly HashSet<string> NonTagKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"auto"
	};

	private static readonly HashSet<string> DefaultTagKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"depth",
		"name",
		"ref",
		"ele",
		"height"
	};

	private static readonly IReadOnlyDictionary<string, double> NamedWidths =
		new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
		{
			["default"] = 1,
			["thinnest"] = 1,
			["thin"] = 1,
			["normal"] = 1,
			["thick"] = 2,
			["thickest"] = 3
		};

	/// <summary>
	/// Translate the provided MapCSS stylesheet text into a MapLibre style document.
	/// </summary>
	/// <param name="mapCss">The MapCSS stylesheet text.</param>
	/// <param name="options">Optional translation options.</param>
	/// <returns>The translation result containing layers and warnings.</returns>
	public MapLibreTranslationResult Translate(string mapCss, MapLibreTranslationOptions? options = null)
	{
		if (mapCss is null)
		{
			throw new ArgumentNullException(nameof(mapCss));
		}

		options ??= new MapLibreTranslationOptions();
		var stylesheet = MapCssParserFacade.Parse(mapCss);
		return TranslateStylesheet(stylesheet, options);
	}

	private static MapLibreTranslationResult TranslateStylesheet(
		MapCssStylesheet stylesheet,
		MapLibreTranslationOptions options)
	{
		var layers = new List<MapLibreLayerDefinition>();
		var warnings = new List<MapLibreTranslationWarning>();

		var ruleIndex = 0;
		for (var ruleSetIndex = 0; ruleSetIndex < stylesheet.Rules.Count; ruleSetIndex++)
		{
			var ruleSet = stylesheet.Rules[ruleSetIndex];
			for (var selectorIndex = 0; selectorIndex < ruleSet.Selectors.Count; selectorIndex++)
			{
				var selector = ruleSet.Selectors[selectorIndex];
				var ruleWarnings = new List<MapLibreTranslationWarning>();
				var translated = TranslateRule(ruleSet, selector, ruleIndex, selectorIndex, options, ruleWarnings);
				if (translated.Count > 0)
				{
					layers.AddRange(translated);
				}

				if (ruleWarnings.Count > 0)
				{
					warnings.AddRange(ruleWarnings);
				}

				ruleIndex++;
			}
		}

		var document = new MapLibreStyleDocument(new ReadOnlyCollection<MapLibreLayerDefinition>(layers));
		return new MapLibreTranslationResult(
			document,
			new ReadOnlyCollection<MapLibreTranslationWarning>(warnings));
	}
	private static List<MapLibreLayerDefinition> TranslateRule(
		MapCssRuleSet ruleSet,
		MapCssSelector selector,
		int ruleIndex,
		int selectorIndex,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings)
	{
		if (!IsSelectorSupported(selector, ruleIndex, selectorIndex, warnings))
		{
			return new List<MapLibreLayerDefinition>();
		}

		var properties = CollectProperties(ruleSet, ruleIndex, selectorIndex, warnings);
		if (properties.Count == 0)
		{
			return new List<MapLibreLayerDefinition>();
		}

		var geometry = InferGeometry(selector, properties.Keys, ruleIndex, selectorIndex, options, warnings);
		var layerType = InferLayerType(geometry, properties.Keys);

		var layerIdBase = BuildLayerIdBase(options, ruleIndex, selector.Subpart);
		var baseLayerId = $"{layerIdBase}_{layerType.ToString().ToLowerInvariant()}";

		var baseBuilder = new LayerBuilder(baseLayerId, layerType);
		LayerBuilder? casingBuilder = null;
		LayerBuilder? repeatBuilder = null;

		if (properties.ContainsKey("casing-width")
			|| properties.ContainsKey("casing-color")
			|| properties.ContainsKey("casing-opacity"))
		{
			casingBuilder = new LayerBuilder($"{layerIdBase}_casing_line", MapLibreLayerType.Line);
		}

		if (properties.ContainsKey("repeat-image")
			|| properties.ContainsKey("repeat-image-width")
			|| properties.ContainsKey("repeat-image-spacing")
			|| properties.ContainsKey("repeat-image-phase"))
		{
			repeatBuilder = new LayerBuilder($"{layerIdBase}_repeat_symbol", MapLibreLayerType.Symbol);
		}

		if (!TryBuildSelectorFilter(selector, options, ruleIndex, selectorIndex, warnings, out var filter, out var minZoom, out var maxZoom))
		{
			return new List<MapLibreLayerDefinition>();
		}

		ApplyFilter(baseBuilder, filter, minZoom, maxZoom);
		ApplyFilter(casingBuilder, filter, minZoom, maxZoom);
		ApplyFilter(repeatBuilder, filter, minZoom, maxZoom);

		foreach (var (property, values) in properties)
		{
			MapLibrePropertyMapping? overrideMapping = options.PropertyMappingResolver?.Invoke(
				new MapLibrePropertyContext(property, geometry, layerType));

			if (overrideMapping is not null)
			{
				ApplyOverrideMapping(baseBuilder, property, values, overrideMapping, ruleIndex, selectorIndex, options, warnings);
				continue;
			}

			switch (property)
			{
				case "color":
				case "colour":
					ApplyColor(baseBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "width":
					ApplyWidth(baseBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "opacity":
					ApplyOpacity(baseBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "dashes":
					ApplyDashes(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "linecap":
					ApplyLineCap(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "fill-color":
				case "fill-colour":
					ApplyFillColor(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "fill-opacity":
					ApplyFillOpacity(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "fill-image":
					ApplyFillImage(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "symbol-size":
					ApplySymbolSize(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "symbol-fill-color":
				case "symbol-fill-colour":
					ApplySymbolFillColor(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "symbol-fill-opacity":
					ApplySymbolFillOpacity(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "symbol-stroke-color":
				case "symbol-stroke-colour":
					ApplySymbolStrokeColor(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "symbol-shape":
					ApplySymbolShape(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "text":
					ApplyText(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "text-color":
				case "text-colour":
					ApplyTextColor(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "text-halo-colour":
				case "text-halo-color":
					ApplyTextHaloColor(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "text-halo-radius":
					ApplyTextHaloRadius(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "text-halo-opacity":
					AddWarning(warnings, ruleIndex, selectorIndex, property,
						"MapLibre has no halo opacity; bake alpha into the halo color.");
					break;
				case "text-anchor-horizontal":
					ApplyTextAnchorHorizontal(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "text-anchor-vertical":
					ApplyTextAnchorVertical(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "text-position":
					ApplyTextPosition(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "font-family":
					ApplyFontFamily(baseBuilder, values, ruleIndex, selectorIndex, warnings);
					break;
				case "font-size":
					ApplyFontSize(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "font-style":
					AddWarning(warnings, ruleIndex, selectorIndex, property,
						"MapLibre uses font names rather than a separate style.");
					break;
				case "icon-image":
					ApplyIconImage(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "icon-width":
				case "icon-height":
					ApplyIconSize(baseBuilder, property, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "icon-offset-x":
					ApplyIconOffsetX(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "icon-offset-y":
					ApplyIconOffsetY(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "icon-opacity":
					ApplyIconOpacity(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "icon-orientation":
					ApplyIconRotation(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "repeat-image":
					ApplyRepeatImage(repeatBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "repeat-image-width":
					ApplyRepeatImageWidth(repeatBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "repeat-image-spacing":
					ApplyRepeatImageSpacing(repeatBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "repeat-image-phase":
					AddWarning(warnings, ruleIndex, selectorIndex, property,
						"MapLibre does not support phase offsets.");
					break;
				case "z-index":
					ApplyZIndex(baseBuilder, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "casing-width":
					ApplyCasingWidth(casingBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "casing-color":
				case "casing-colour":
					ApplyCasingColor(casingBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "casing-opacity":
					ApplyCasingOpacity(casingBuilder, geometry, values, ruleIndex, selectorIndex, warnings, options);
					break;
				case "antialiasing":
				case "default-lines":
				case "default-points":
				case "icon":
				case "title":
				case "author":
				case "version":
				case "description":
				case "min-josm-version":
					AddWarning(warnings, ruleIndex, selectorIndex, property,
						"MapCSS metadata or canvas settings are not supported in MapLibre.");
					break;
				default:
					AddWarning(warnings, ruleIndex, selectorIndex, property, "Unsupported MapCSS property.");
					break;
			}
		}

		ApplyDeferredIconSize(baseBuilder, options, ruleIndex, selectorIndex, warnings);

		var layers = new List<MapLibreLayerDefinition>();
		if (casingBuilder is not null && casingBuilder.HasContent)
		{
			layers.Add(casingBuilder.Build());
		}

		if (baseBuilder.HasContent)
		{
			layers.Add(baseBuilder.Build());
		}

		if (repeatBuilder is not null && repeatBuilder.HasContent)
		{
			layers.Add(repeatBuilder.Build());
		}

		return layers;
	}

	private static bool IsSelectorSupported(
		MapCssSelector selector,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (selector.Segments.Count != 1)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Combinators and multi-segment selectors are not supported.");
			return false;
		}

		var segment = selector.Segments[0];
		if (segment.CombinatorToPrevious != MapCssCombinator.None)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Combinators are not supported by the translator.");
			return false;
		}

		if (segment.LinkFiltersToPrevious.Count > 0)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Link filters are not supported by the translator.");
			return false;
		}

		return true;
	}

	private static Dictionary<string, IReadOnlyList<string>> CollectProperties(
		MapCssRuleSet ruleSet,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		var properties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var declaration in ruleSet.Declarations)
		{
			if (declaration is MapCssSetDeclaration)
			{
				AddWarning(warnings, ruleIndex, selectorIndex, "set",
					"Set declarations are ignored; precompute class:* properties in tiles.");
				continue;
			}

			if (declaration is MapCssPropertyDeclaration property)
			{
				properties[property.Name.ToLowerInvariant()] = property.Values;
			}
		}

		return properties;
	}

	private static MapLibreGeometryType InferGeometry(
		MapCssSelector selector,
		IEnumerable<string> propertyNames,
		int ruleIndex,
		int selectorIndex,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings)
	{
		var elementType = selector.Segments[0].Selector.ElementType;
		var overridden = options.GeometryResolver?.Invoke(elementType);
		if (overridden.HasValue)
		{
			return overridden.Value;
		}

		if (elementType.HasValue && elementType != MapCssElementType.Any)
		{
			return MapGeometryFromElementType(elementType.Value);
		}

		var names = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
		if (names.Contains("fill-color") || names.Contains("fill-opacity") || names.Contains("fill-image"))
		{
			return MapLibreGeometryType.Polygon;
		}

		if (names.Contains("width") || names.Contains("dashes") || names.Contains("linecap")
			|| names.Contains("casing-width") || names.Contains("casing-color") || names.Contains("casing-opacity"))
		{
			return MapLibreGeometryType.Line;
		}

		if (names.Contains("text") || names.Contains("icon-image") || names.Contains("symbol-size"))
		{
			return MapLibreGeometryType.Point;
		}

		AddWarning(warnings, ruleIndex, selectorIndex, null,
			"Could not infer geometry; defaulting to point.");
		return MapLibreGeometryType.Point;
	}

	private static MapLibreLayerType InferLayerType(MapLibreGeometryType geometry, IEnumerable<string> propertyNames)
	{
		var names = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
		var hasFill = names.Contains("fill-color") || names.Contains("fill-opacity") || names.Contains("fill-image");
		var hasLine = names.Contains("color") || names.Contains("width") || names.Contains("opacity")
			|| names.Contains("dashes") || names.Contains("linecap")
			|| names.Contains("casing-width") || names.Contains("casing-color") || names.Contains("casing-opacity");
		var hasSymbol = names.Contains("text") || names.Contains("icon-image")
			|| names.Contains("text-color") || names.Contains("font-family") || names.Contains("font-size");
		var hasCircle = names.Contains("symbol-size") || names.Contains("symbol-fill-color")
			|| names.Contains("symbol-fill-opacity") || names.Contains("symbol-stroke-color");

		return geometry switch
		{
			MapLibreGeometryType.Polygon => hasFill ? MapLibreLayerType.Fill
				: hasLine ? MapLibreLayerType.Line
				: hasSymbol ? MapLibreLayerType.Symbol
				: MapLibreLayerType.Fill,
			MapLibreGeometryType.Line => hasLine ? MapLibreLayerType.Line
				: hasSymbol ? MapLibreLayerType.Symbol
				: MapLibreLayerType.Line,
			_ => hasSymbol ? MapLibreLayerType.Symbol
				: hasCircle ? MapLibreLayerType.Circle
				: MapLibreLayerType.Circle
		};
	}

	private static string BuildLayerIdBase(MapLibreTranslationOptions options, int ruleIndex, string? subpart)
	{
		var styleId = string.IsNullOrWhiteSpace(options.StylesheetId)
			? "mapcss"
			: options.StylesheetId!;
		var prefix = string.IsNullOrWhiteSpace(options.LayerIdPrefix) ? string.Empty : options.LayerIdPrefix!;
		var normalizedStyle = NormalizeId(styleId);
		var normalizedSubpart = NormalizeId(string.IsNullOrWhiteSpace(subpart) ? "default" : subpart!);
		return $"{prefix}{normalizedStyle}_{ruleIndex}_{normalizedSubpart}";
	}

	private static string NormalizeId(string value)
	{
		var builder = new StringBuilder(value.Length);
		foreach (var c in value)
		{
			if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
			{
				builder.Append(char.ToLowerInvariant(c));
			}
			else
			{
				builder.Append('_');
			}
		}

		return builder.ToString();
	}

	private static void ApplyOverrideMapping(
		LayerBuilder builder,
		string property,
		IReadOnlyList<string> values,
		MapLibrePropertyMapping mapping,
		int ruleIndex,
		int selectorIndex,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings)
	{
		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, property);
		if (value is null)
		{
			return;
		}

		if (mapping.IsLayout)
		{
			builder.Layout[mapping.Target] = value;
		}
		else
		{
			builder.Paint[mapping.Target] = value;
		}
	}

	private static void ApplyColor(
		LayerBuilder builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "color");
		if (value is null)
		{
			return;
		}

		switch (builder.LayerType)
		{
			case MapLibreLayerType.Line:
				builder.Paint["line-color"] = value;
				break;
			case MapLibreLayerType.Fill:
				builder.Paint["fill-outline-color"] = value;
				break;
			case MapLibreLayerType.Circle:
				builder.Paint["circle-color"] = value;
				break;
			default:
				AddWarning(warnings, ruleIndex, selectorIndex, "color",
					$"MapLibre layer type {builder.LayerType} does not support 'color'.");
				break;
		}
	}

	private static void ApplyWidth(
		LayerBuilder builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		var number = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "width");
		if (number is null)
		{
			return;
		}

		switch (builder.LayerType)
		{
			case MapLibreLayerType.Line:
				builder.Paint["line-width"] = number;
				break;
			case MapLibreLayerType.Circle:
				builder.Paint["circle-radius"] = number;
				break;
			default:
				AddWarning(warnings, ruleIndex, selectorIndex, "width",
					$"MapLibre layer type {builder.LayerType} does not support 'width'.");
				break;
		}
	}

	private static void ApplyOpacity(
		LayerBuilder builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		var number = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "opacity");
		if (number is null)
		{
			return;
		}

		switch (builder.LayerType)
		{
			case MapLibreLayerType.Line:
				builder.Paint["line-opacity"] = number;
				break;
			case MapLibreLayerType.Fill:
				builder.Paint["fill-opacity"] = number;
				break;
			case MapLibreLayerType.Circle:
				builder.Paint["circle-opacity"] = number;
				break;
			default:
				AddWarning(warnings, ruleIndex, selectorIndex, "opacity",
					$"MapLibre layer type {builder.LayerType} does not support 'opacity'.");
				break;
		}
	}

	private static void ApplyDashes(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "dashes",
				"MapLibre dashes only apply to line layers.");
			return;
		}

		var numbers = ExtractNumbers(values);
		if (numbers.Length == 0)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "dashes",
				"Could not parse any numeric dash values.");
			return;
		}

		builder.Paint["line-dasharray"] = numbers;
	}

	private static void ApplyLineCap(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "linecap",
				"MapLibre linecap only applies to line layers.");
			return;
		}

		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "linecap", "Missing linecap value.");
			return;
		}

		builder.Layout["line-cap"] = Unquote(value);
	}
	private static void ApplyFillColor(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Fill)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "fill-color",
				"MapLibre fill-color only applies to fill layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "fill-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["fill-color"] = value;
	}

	private static void ApplyFillOpacity(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Fill)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "fill-opacity",
				"MapLibre fill-opacity only applies to fill layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "fill-opacity");
		if (value is null)
		{
			return;
		}

		builder.Paint["fill-opacity"] = value;
	}

	private static void ApplyFillImage(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Fill)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "fill-image",
				"MapLibre fill-image only applies to fill layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "fill-image");
		if (value is null)
		{
			return;
		}

		builder.Paint["fill-pattern"] = value;
	}

	private static void ApplySymbolSize(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Circle)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-size",
				"MapLibre symbol-size only applies to circle layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "symbol-size");
		if (value is null)
		{
			return;
		}

		builder.Paint["circle-radius"] = value;
	}

	private static void ApplySymbolFillColor(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Circle)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-fill-color",
				"MapLibre symbol-fill-color only applies to circle layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "symbol-fill-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["circle-color"] = value;
	}

	private static void ApplySymbolFillOpacity(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Circle)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-fill-opacity",
				"MapLibre symbol-fill-opacity only applies to circle layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "symbol-fill-opacity");
		if (value is null)
		{
			return;
		}

		builder.Paint["circle-opacity"] = value;
	}

	private static void ApplySymbolStrokeColor(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Circle)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-stroke-color",
				"MapLibre symbol-stroke-color only applies to circle layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "symbol-stroke-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["circle-stroke-color"] = value;
	}

	private static void ApplySymbolShape(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-shape", "Missing symbol-shape value.");
			return;
		}

		value = Unquote(value);
		if (!string.Equals(value, "circle", StringComparison.OrdinalIgnoreCase))
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "symbol-shape",
				"Only circle maps directly to MapLibre; other shapes need icons.");
		}
	}

	private static void ApplyText(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text",
				"MapLibre text only applies to symbol layers.");
			return;
		}

		var raw = FirstValue(values);
		if (raw is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text", "Missing text value.");
			return;
		}

		object? translated = null;
		var isExpression = false;
		if (TryTranslateExpression(raw, options, warnings, ruleIndex, selectorIndex, "text", out translated, out isExpression))
		{
			if (translated is null)
			{
				return;
			}

			if (isExpression)
			{
				builder.Layout["text-field"] = translated;
				return;
			}
		}

		var text = translated as string ?? Unquote(raw);
		if (ShouldUseTagExpression(text))
		{
			builder.Layout["text-field"] = new object[] { "get", text };
			return;
		}

		builder.Layout["text-field"] = text;
	}

	private static void ApplyTextColor(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-color",
				"MapLibre text-color only applies to symbol layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "text-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["text-color"] = value;
	}

	private static void ApplyTextHaloColor(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-halo-color",
				"MapLibre text-halo-color only applies to symbol layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "text-halo-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["text-halo-color"] = value;
	}

	private static void ApplyTextHaloRadius(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-halo-radius",
				"MapLibre text-halo-radius only applies to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "text-halo-radius");
		if (value is null)
		{
			return;
		}

		builder.Paint["text-halo-width"] = value;
	}

	private static void ApplyTextAnchorHorizontal(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-anchor-horizontal",
				"MapLibre text-anchor-horizontal only applies to symbol layers.");
			return;
		}

		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-anchor-horizontal",
				"Missing text-anchor-horizontal value.");
			return;
		}

		builder.SetTextAnchorHorizontal(Unquote(value));
	}

	private static void ApplyTextAnchorVertical(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-anchor-vertical",
				"MapLibre text-anchor-vertical only applies to symbol layers.");
			return;
		}

		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-anchor-vertical",
				"Missing text-anchor-vertical value.");
			return;
		}

		builder.SetTextAnchorVertical(Unquote(value));
	}

	private static void ApplyTextPosition(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-position",
				"MapLibre text-position only applies to symbol layers.");
			return;
		}

		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "text-position",
				"Missing text-position value.");
			return;
		}

		value = Unquote(value);
		if (string.Equals(value, "center", StringComparison.OrdinalIgnoreCase))
		{
			builder.SetTextAnchorCenter();
			return;
		}

		AddWarning(warnings, ruleIndex, selectorIndex, "text-position",
			"Unsupported text-position value.");
	}

	private static void ApplyFontFamily(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "font-family",
				"MapLibre font-family only applies to symbol layers.");
			return;
		}

		var value = FirstValue(values);
		if (value is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "font-family", "Missing font-family value.");
			return;
		}

		builder.Layout["text-font"] = SplitFonts(Unquote(value));
	}

	private static void ApplyFontSize(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "font-size",
				"MapLibre font-size only applies to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "font-size");
		if (value is null)
		{
			return;
		}

		builder.Layout["text-size"] = value;
	}

	private static void ApplyIconImage(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-image",
				"MapLibre icon-image only applies to symbol layers.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "icon-image");
		if (value is null)
		{
			return;
		}

		builder.Layout["icon-image"] = value;
	}

	private static void ApplyIconSize(
		LayerBuilder builder,
		string property,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, property,
				"MapLibre icon sizes only apply to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, property);
		if (value is null)
		{
			return;
		}

		if (string.Equals(property, "icon-width", StringComparison.OrdinalIgnoreCase))
		{
			builder.SetIconWidth(value);
			return;
		}

		builder.SetIconHeight(value);
	}

	private static void ApplyIconOffsetX(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-offset-x",
				"MapLibre icon offsets only apply to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "icon-offset-x");
		if (value is null)
		{
			return;
		}

		if (!TryGetNumericValue(value, out var number))
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-offset-x",
				"MapLibre icon-offset-x expects a numeric value.");
			return;
		}

		builder.SetIconOffsetX(number);
	}

	private static void ApplyIconOffsetY(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-offset-y",
				"MapLibre icon offsets only apply to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "icon-offset-y");
		if (value is null)
		{
			return;
		}

		if (!TryGetNumericValue(value, out var number))
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-offset-y",
				"MapLibre icon-offset-y expects a numeric value.");
			return;
		}

		builder.SetIconOffsetY(number);
	}

	private static void ApplyIconOpacity(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-opacity",
				"MapLibre icon-opacity only applies to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "icon-opacity");
		if (value is null)
		{
			return;
		}

		builder.Paint["icon-opacity"] = value;
	}

	private static void ApplyIconRotation(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "icon-orientation",
				"MapLibre icon-orientation only applies to symbol layers.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "icon-orientation");
		if (value is null)
		{
			return;
		}

		builder.Layout["icon-rotate"] = value;
	}

	private static void ApplyRepeatImage(
		LayerBuilder? builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image",
				"Repeat-image requires a symbol layer.");
			return;
		}

		if (geometry != MapLibreGeometryType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image",
				"Repeat-image is only supported on line geometries.");
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "repeat-image");
		if (value is null)
		{
			return;
		}

		builder.Layout["symbol-placement"] = "line";
		builder.Layout["icon-image"] = value;
	}

	private static void ApplyRepeatImageWidth(
		LayerBuilder? builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image-width",
				"Repeat-image-width requires a symbol layer.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "repeat-image-width");
		if (value is null)
		{
			return;
		}

		if (options.IconBaseSize is > 0)
		{
			builder.Layout["icon-size"] = ScaleIconValue(value, options.IconBaseSize.Value, out var warning);
			if (warning is not null)
			{
				AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image-width", warning);
			}
		}
		else
		{
			builder.Layout["icon-size"] = value;
			AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image-width",
				"MapLibre icon-size expects a scale; set IconBaseSize to convert.");
		}
	}

	private static void ApplyRepeatImageSpacing(
		LayerBuilder? builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "repeat-image-spacing",
				"Repeat-image-spacing requires a symbol layer.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "repeat-image-spacing");
		if (value is null)
		{
			return;
		}

		builder.Layout["symbol-spacing"] = value;
	}

	private static void ApplyZIndex(
		LayerBuilder builder,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "z-index",
				"MapLibre z-index is only supported on symbol layers via symbol-sort-key.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "z-index");
		if (value is null)
		{
			return;
		}

		builder.Layout["symbol-sort-key"] = value;
	}

	private static void ApplyCasingWidth(
		LayerBuilder? builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-width",
				"Casing requires a line layer.");
			return;
		}

		if (geometry != MapLibreGeometryType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-width",
				"Casing is only supported on line geometries.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "casing-width");
		if (value is null)
		{
			return;
		}

		builder.Paint["line-width"] = value;
	}

	private static void ApplyCasingColor(
		LayerBuilder? builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-color",
				"Casing requires a line layer.");
			return;
		}

		if (geometry != MapLibreGeometryType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-color",
				"Casing is only supported on line geometries.");
			return;
		}

		var value = TranslateValue(values, options, warnings, ruleIndex, selectorIndex, "casing-color");
		if (value is null)
		{
			return;
		}

		builder.Paint["line-color"] = value;
	}

	private static void ApplyCasingOpacity(
		LayerBuilder? builder,
		MapLibreGeometryType geometry,
		IReadOnlyList<string> values,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		MapLibreTranslationOptions options)
	{
		if (builder is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-opacity",
				"Casing requires a line layer.");
			return;
		}

		if (geometry != MapLibreGeometryType.Line)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, "casing-opacity",
				"Casing is only supported on line geometries.");
			return;
		}

		var value = TranslateNumber(values, options, warnings, ruleIndex, selectorIndex, "casing-opacity");
		if (value is null)
		{
			return;
		}

		builder.Paint["line-opacity"] = value;
	}

	private static void ApplyFilter(LayerBuilder? builder, object? filter, double? minZoom, double? maxZoom)
	{
		if (builder is null)
		{
			return;
		}

		builder.Filter = filter;
		builder.MinZoom = minZoom;
		builder.MaxZoom = maxZoom;
	}

	private static void ApplyDeferredIconSize(
		LayerBuilder builder,
		MapLibreTranslationOptions options,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (builder.LayerType != MapLibreLayerType.Symbol)
		{
			return;
		}

		if (!builder.HasIconSize)
		{
			return;
		}

		var width = builder.IconWidth;
		var height = builder.IconHeight;
		object size = width ?? height!;
		var warningProperty = width is not null ? "icon-width" : "icon-height";

		if (width is not null && height is not null)
		{
			if (TryGetNumericValue(width, out var widthValue)
				&& TryGetNumericValue(height, out var heightValue))
			{
				if (Math.Abs(widthValue - heightValue) > 0.0001)
				{
					size = Math.Max(widthValue, heightValue);
					AddWarning(warnings, ruleIndex, selectorIndex, warningProperty,
						"MapLibre icon-size is uniform; using max(icon-width, icon-height).");
				}
				else
				{
					size = widthValue;
				}
			}
			else
			{
				size = width;
				AddWarning(warnings, ruleIndex, selectorIndex, warningProperty,
					"MapLibre icon-size is uniform; using icon-width.");
			}
		}

		if (options.IconBaseSize is > 0)
		{
			builder.Layout["icon-size"] = ScaleIconValue(size, options.IconBaseSize.Value, out var warning);
			if (warning is not null)
			{
				AddWarning(warnings, ruleIndex, selectorIndex, warningProperty, warning);
			}
			return;
		}

		builder.Layout["icon-size"] = size;
		AddWarning(warnings, ruleIndex, selectorIndex, warningProperty,
			"MapLibre icon-size expects a scale; set IconBaseSize to convert.");
	}
	private static bool TryBuildSelectorFilter(
		MapCssSelector selector,
		MapLibreTranslationOptions options,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		out object? filter,
		out double? minZoom,
		out double? maxZoom)
	{
		filter = null;
		minZoom = null;
		maxZoom = null;

		var segment = selector.Segments[0];
		var simple = segment.Selector;

		if (simple.PseudoClasses.Count > 0)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Pseudo-classes are not supported by the MapLibre translator.");
			return false;
		}

		if (simple.ElementType == MapCssElementType.Canvas)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Canvas selectors are not supported by the MapLibre translator.");
			return false;
		}

		if (!TryComputeZoomRange(simple.ZoomRanges, ruleIndex, selectorIndex, warnings, out minZoom, out maxZoom))
		{
			return false;
		}

		var filters = new List<object>();
		var resolvedGeometry = options.GeometryResolver?.Invoke(simple.ElementType);
		if (!resolvedGeometry.HasValue && simple.ElementType is { } elementType && elementType != MapCssElementType.Any)
		{
			resolvedGeometry = MapGeometryFromElementType(elementType);
		}

		if (resolvedGeometry.HasValue)
		{
			filters.Add(new object[]
			{
				"==",
				new object[] { "geometry-type" },
				GeometryTypeToFilterValue(resolvedGeometry.Value)
			});
		}

		foreach (var cls in simple.Classes)
		{
			var classKey = options.ClassPropertyResolver?.Invoke(cls) ?? $"class:{cls}";
			if (string.IsNullOrWhiteSpace(classKey))
			{
				AddWarning(warnings, ruleIndex, selectorIndex, cls,
					"Class property resolver returned an empty key.");
				return false;
			}

			filters.Add(new object[]
			{
				"==",
				new object[] { "get", classKey },
				true
			});
		}

		foreach (var test in simple.AttributeTests)
		{
			if (!TryBuildAttributeFilter(test, ruleIndex, selectorIndex, warnings, out var testFilter))
			{
				return false;
			}

			if (testFilter is not null)
			{
				filters.Add(testFilter);
			}
		}

		if (filters.Count == 0)
		{
			filter = null;
			return true;
		}

		if (filters.Count == 1)
		{
			filter = filters[0];
			return true;
		}

		var allFilter = new object[filters.Count + 1];
		allFilter[0] = "all";
		for (var i = 0; i < filters.Count; i++)
		{
			allFilter[i + 1] = filters[i];
		}

		filter = allFilter;
		return true;
	}

	private static bool TryComputeZoomRange(
		IReadOnlyList<MapCssZoomRange> ranges,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		out double? minZoom,
		out double? maxZoom)
	{
		minZoom = null;
		maxZoom = null;

		foreach (var range in ranges)
		{
			minZoom = minZoom.HasValue
				? Math.Max(minZoom.Value, range.Min)
				: range.Min;

			if (range.Max.HasValue)
			{
				maxZoom = maxZoom.HasValue
					? Math.Min(maxZoom.Value, range.Max.Value)
					: range.Max.Value;
			}
		}

		if (minZoom.HasValue && maxZoom.HasValue && maxZoom.Value < minZoom.Value)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, null,
				"Selector zoom ranges have no overlap.");
			return false;
		}

		return true;
	}

	private static bool TryBuildAttributeFilter(
		MapCssAttributeTest test,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		out object? filter)
	{
		filter = null;
		var conditions = new List<object>();

		if (test.Existence is { } existence)
		{
			var existenceFilter = BuildExistenceFilter(test.Key, existence, ruleIndex, selectorIndex, warnings);
			if (existenceFilter is not null)
			{
				conditions.Add(existenceFilter);
			}
		}

		if (test.Operator is { } op)
		{
			if (test.Value is null)
			{
				AddWarning(warnings, ruleIndex, selectorIndex, test.Key,
					"Missing attribute value for selector.");
				return false;
			}

			if (!TryBuildOperatorFilter(test.Key, op, test.Value, ruleIndex, selectorIndex, warnings, out var opFilter))
			{
				return false;
			}

			conditions.Add(opFilter);
		}
		else if (test.Existence is null)
		{
			conditions.Add(new object[] { "has", test.Key });
		}

		if (conditions.Count == 0)
		{
			return true;
		}

		object combined;
		if (conditions.Count == 1)
		{
			combined = conditions[0];
		}
		else
		{
			var combinedFilter = new object[conditions.Count + 1];
			combinedFilter[0] = "all";
			for (var i = 0; i < conditions.Count; i++)
			{
				combinedFilter[i + 1] = conditions[i];
			}
			combined = combinedFilter;
		}

		if (test.Negate)
		{
			combined = new object[] { "!", combined };
		}

		filter = combined;
		return true;
	}

	private static object? BuildExistenceFilter(
		string key,
		MapCssAttributeExistence existence,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings)
	{
		if (existence == MapCssAttributeExistence.Truthy)
		{
			return new object[] { "has", key };
		}

		AddWarning(warnings, ruleIndex, selectorIndex, key,
			"MapLibre cannot fully evaluate not-truthy; using !has.");
		return new object[] { "!", new object[] { "has", key } };
	}

	private static bool TryBuildOperatorFilter(
		string key,
		MapCssAttributeOperator op,
		MapCssAttributeValue value,
		int ruleIndex,
		int selectorIndex,
		List<MapLibreTranslationWarning> warnings,
		out object filter)
	{
		filter = null!;

		if (value.Kind == MapCssValueKind.Regex
			|| op == MapCssAttributeOperator.RegexMatch
			|| op == MapCssAttributeOperator.RegexNMatch)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, key,
				"Regex selectors are not supported by the MapLibre translator.");
			return false;
		}

		var literal = ConvertAttributeValue(value);
		switch (op)
		{
			case MapCssAttributeOperator.Eq:
				filter = new object[] { "==", new object[] { "get", key }, literal };
				return true;
			case MapCssAttributeOperator.NotEq:
				filter = new object[] { "!=", new object[] { "get", key }, literal };
				return true;
			case MapCssAttributeOperator.Contains:
				filter = BuildContainsFilter(key, literal);
				return true;
			case MapCssAttributeOperator.Prefix:
				filter = BuildStartsWithFilter(key, literal);
				return true;
			case MapCssAttributeOperator.Suffix:
				filter = BuildEndsWithFilter(key, literal);
				return true;
			case MapCssAttributeOperator.Match:
				filter = BuildContainsFilter(key, literal);
				return true;
			case MapCssAttributeOperator.NMatch:
				filter = BuildNotContainsFilter(key, literal);
				return true;
			default:
				AddWarning(warnings, ruleIndex, selectorIndex, key,
					"Unsupported selector operator.");
				return false;
		}
	}

	private static object ConvertAttributeValue(MapCssAttributeValue value)
	{
		switch (value.Kind)
		{
			case MapCssValueKind.Number:
				if (double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
				{
					return number;
				}
				return value.Text;
			case MapCssValueKind.Boolean:
				return string.Equals(value.Text, "true", StringComparison.OrdinalIgnoreCase);
			default:
				return value.Text;
		}
	}

	private static object BuildContainsFilter(string key, object literal)
	{
		var text = Convert.ToString(literal, CultureInfo.InvariantCulture) ?? string.Empty;
		return new object[] { "!=", new object[] { "index-of", text, new object[] { "get", key } }, -1 };
	}

	private static object BuildNotContainsFilter(string key, object literal)
	{
		var text = Convert.ToString(literal, CultureInfo.InvariantCulture) ?? string.Empty;
		return new object[] { "==", new object[] { "index-of", text, new object[] { "get", key } }, -1 };
	}

	private static object BuildStartsWithFilter(string key, object literal)
	{
		var text = Convert.ToString(literal, CultureInfo.InvariantCulture) ?? string.Empty;
		return new object[] { "starts-with", new object[] { "get", key }, text };
	}

	private static object BuildEndsWithFilter(string key, object literal)
	{
		var text = Convert.ToString(literal, CultureInfo.InvariantCulture) ?? string.Empty;
		return new object[] { "ends-with", new object[] { "get", key }, text };
	}

	private static void AddWarning(
		List<MapLibreTranslationWarning> warnings,
		int ruleIndex,
		int selectorIndex,
		string? property,
		string message)
	{
		warnings.Add(new MapLibreTranslationWarning(ruleIndex, selectorIndex, property, message));
	}

	private static object? TranslateValue(
		IReadOnlyList<string> values,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings,
		int ruleIndex,
		int selectorIndex,
		string property)
	{
		var raw = FirstValue(values);
		if (raw is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, property, "Missing value.");
			return null;
		}

		if (TryTranslateExpression(raw, options, warnings, ruleIndex, selectorIndex, property, out var translated, out var isExpression))
		{
			if (translated is null)
			{
				return null;
			}

			return isExpression ? translated : translated;
		}

		return Unquote(raw);
	}

	private static object? TranslateNumber(
		IReadOnlyList<string> values,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings,
		int ruleIndex,
		int selectorIndex,
		string property)
	{
		var raw = FirstValue(values);
		if (raw is null)
		{
			AddWarning(warnings, ruleIndex, selectorIndex, property, "Missing numeric value.");
			return null;
		}

		if (TryParseNumberLiteral(raw, out var literal, out var warning))
		{
			if (warning is not null)
			{
				AddWarning(warnings, ruleIndex, selectorIndex, property, warning);
			}
			return literal;
		}

		if (TryTranslateExpression(raw, options, warnings, ruleIndex, selectorIndex, property, out var translated, out var isExpression))
		{
			if (translated is null)
			{
				return null;
			}

			if (isExpression)
			{
				return translated;
			}

			if (translated is string text && TryParseNumberLiteral(text, out var parsed, out var translatedWarning))
			{
				if (translatedWarning is not null)
				{
					AddWarning(warnings, ruleIndex, selectorIndex, property, translatedWarning);
				}
				return parsed;
			}

			if (TryGetNumericValue(translated, out var number))
			{
				return number;
			}
		}

		AddWarning(warnings, ruleIndex, selectorIndex, property, $"Unsupported numeric value '{raw}'.");
		return null;
	}

	private static bool TryParseNumberLiteral(string value, out double number, out string? warning)
	{
		warning = null;
		number = 0;

		if (NamedWidths.TryGetValue(value, out number))
		{
			return true;
		}

		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
		{
			if (value.StartsWith('+'))
			{
				warning = "Relative values are approximated as absolute numbers.";
			}
			return true;
		}

		return false;
	}

	private static bool TryTranslateExpression(
		string raw,
		MapLibreTranslationOptions options,
		List<MapLibreTranslationWarning> warnings,
		int ruleIndex,
		int selectorIndex,
		string property,
		out object? translated,
		out bool isExpression)
	{
		translated = null;
		isExpression = false;

		try
		{
			var parser = new ExpressionParser(raw);
			var result = parser.Parse();
			translated = result.Value;
			isExpression = result.IsExpression;
			return true;
		}
		catch (InvalidOperationException)
		{
			if (options.StrictExpressions)
			{
				throw;
			}

			AddWarning(warnings, ruleIndex, selectorIndex, property,
				$"Unsupported expression '{raw}'.");
			return false;
		}
	}

	private static object ScaleIconValue(object value, double baseSize, out string? warning)
	{
		warning = null;

		if (TryGetNumericValue(value, out var number))
		{
			return number / baseSize;
		}

		if (value is object[] expression)
		{
			return new object[] { "/", expression, baseSize };
		}

		warning = "MapLibre icon-size expects a numeric value to scale; using raw value.";
		return value;
	}

	private static bool TryGetNumericValue(object? value, out double number)
	{
		number = 0;
		switch (value)
		{
			case double d:
				number = d;
				return true;
			case float f:
				number = f;
				return true;
			case int i:
				number = i;
				return true;
			case long l:
				number = l;
				return true;
			case decimal m:
				number = (double)m;
				return true;
			case short s:
				number = s;
				return true;
			case byte b:
				number = b;
				return true;
			case string text:
				return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
			default:
				return false;
		}
	}

	private static double[] ExtractNumbers(IReadOnlyList<string> values)
	{
		var text = string.Join(" ", values);
		var matches = NumberRegex.Matches(text);
		var numbers = new List<double>(matches.Count);
		foreach (Match match in matches)
		{
			if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
			{
				numbers.Add(parsed);
			}
		}

		return numbers.ToArray();
	}

	private static string? FirstValue(IReadOnlyList<string> values)
	{
		foreach (var value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}

		return null;
	}

	private static string[] SplitFonts(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}

		return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static bool ShouldUseTagExpression(string text)
	{
		if (NonTagKeywords.Contains(text))
		{
			return false;
		}

		if (text.Contains(':', StringComparison.Ordinal) && TagKeyRegex.IsMatch(text))
		{
			return true;
		}

		return DefaultTagKeys.Contains(text);
	}

	private static MapLibreGeometryType MapGeometryFromElementType(MapCssElementType elementType)
	{
		return elementType switch
		{
			MapCssElementType.Node => MapLibreGeometryType.Point,
			MapCssElementType.Way => MapLibreGeometryType.Line,
			MapCssElementType.Area => MapLibreGeometryType.Polygon,
			MapCssElementType.Relation => MapLibreGeometryType.Polygon,
			MapCssElementType.Canvas => MapLibreGeometryType.Polygon,
			_ => MapLibreGeometryType.Point
		};
	}

	private static string GeometryTypeToFilterValue(MapLibreGeometryType geometry)
	{
		return geometry switch
		{
			MapLibreGeometryType.Line => "LineString",
			MapLibreGeometryType.Polygon => "Polygon",
			_ => "Point"
		};
	}

	private static string Unquote(string value)
	{
		if (value.Length < 2)
		{
			return value;
		}

		var quote = value[0];
		if (quote != '"' && quote != '\'')
		{
			return value;
		}

		if (value[^1] != quote)
		{
			return value;
		}

		var builder = new StringBuilder(value.Length - 2);
		for (var i = 1; i < value.Length - 1; i++)
		{
			var c = value[i];
			if (c == '\\' && i + 1 < value.Length - 1)
			{
				var next = value[++i];
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
	private sealed class LayerBuilder
	{
		private readonly SortedDictionary<string, object> _paint = new(StringComparer.Ordinal);
		private readonly SortedDictionary<string, object> _layout = new(StringComparer.Ordinal);
		private double _iconOffsetX;
		private double _iconOffsetY;
		private bool _hasIconOffset;
		private string? _textAnchorHorizontal;
		private string? _textAnchorVertical;
		private object? _iconWidth;
		private object? _iconHeight;

		public LayerBuilder(string id, MapLibreLayerType layerType)
		{
			Id = id;
			LayerType = layerType;
		}

		public string Id { get; }
		public MapLibreLayerType LayerType { get; }
		public SortedDictionary<string, object> Paint => _paint;
		public SortedDictionary<string, object> Layout => _layout;
		public object? Filter { get; set; }
		public double? MinZoom { get; set; }
		public double? MaxZoom { get; set; }
		public bool HasContent => _paint.Count > 0 || _layout.Count > 0;
		public bool HasIconSize => _iconWidth is not null || _iconHeight is not null;
		public object? IconWidth => _iconWidth;
		public object? IconHeight => _iconHeight;

		public void SetIconWidth(object value) => _iconWidth = value;
		public void SetIconHeight(object value) => _iconHeight = value;

		public void SetIconOffsetX(double x)
		{
			_iconOffsetX = x;
			_hasIconOffset = true;
			UpdateIconOffset();
		}

		public void SetIconOffsetY(double y)
		{
			_iconOffsetY = y;
			_hasIconOffset = true;
			UpdateIconOffset();
		}

		public void SetTextAnchorHorizontal(string value)
		{
			_textAnchorHorizontal = value;
			UpdateTextAnchor();
		}

		public void SetTextAnchorVertical(string value)
		{
			_textAnchorVertical = value;
			UpdateTextAnchor();
		}

		public void SetTextAnchorCenter()
		{
			_textAnchorHorizontal = "center";
			_textAnchorVertical = "center";
			UpdateTextAnchor();
		}

		public MapLibreLayerDefinition Build()
		{
			return new MapLibreLayerDefinition(
				Id,
				LayerType,
				Filter,
				new ReadOnlyDictionary<string, object>(_paint),
				new ReadOnlyDictionary<string, object>(_layout),
				MinZoom,
				MaxZoom);
		}

		private void UpdateIconOffset()
		{
			if (!_hasIconOffset)
			{
				return;
			}

			_layout["icon-offset"] = new[] { _iconOffsetX, _iconOffsetY };
		}

		private void UpdateTextAnchor()
		{
			var horizontal = (_textAnchorHorizontal ?? "center").ToLowerInvariant();
			var vertical = (_textAnchorVertical ?? "center").ToLowerInvariant();

			var h = horizontal switch
			{
				"left" => "left",
				"right" => "right",
				"center" => "center",
				_ => "center"
			};

			var v = vertical switch
			{
				"above" => "top",
				"below" => "bottom",
				"top" => "top",
				"bottom" => "bottom",
				"center" => "center",
				_ => "center"
			};

			var anchor = v == "center" && h == "center"
				? "center"
				: v == "center"
					? h
					: h == "center"
						? v
						: $"{v}-{h}";

			_layout["text-anchor"] = anchor;
		}
	}

	private sealed class ExpressionParser
	{
		private readonly string _text;
		private int _index;
		private bool _sawExpression;

		public ExpressionParser(string text)
		{
			_text = text ?? string.Empty;
		}

		public ExpressionParseResult Parse()
		{
			_index = 0;
			_sawExpression = false;
			var value = ParseEquality();
			SkipWhitespace();
			if (_index < _text.Length)
			{
				throw new InvalidOperationException("Unexpected expression tokens.");
			}

			return new ExpressionParseResult(value, _sawExpression);
		}

		private object ParseEquality()
		{
			var left = ParsePrimary();
			SkipWhitespace();

			if (Match("==") || Match("="))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { "==", left, right };
			}

			if (Match("!="))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { "!=", left, right };
			}

			if (Match(">="))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { ">=", left, right };
			}

			if (Match("<="))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { "<=", left, right };
			}

			if (Match(">"))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { ">", left, right };
			}

			if (Match("<"))
			{
				var right = ParsePrimary();
				_sawExpression = true;
				return new object[] { "<", left, right };
			}

			return left;
		}

		private object ParsePrimary()
		{
			SkipWhitespace();
			if (_index >= _text.Length)
			{
				return string.Empty;
			}

			var c = _text[_index];
			if (c == '\'' || c == '"')
			{
				return ParseString();
			}

			if (IsNumberStart(c))
			{
				return ParseNumber();
			}

			if (char.IsLetter(c) || c == '_')
			{
				var ident = ParseIdent();
				SkipWhitespace();
				if (Peek() == '(')
				{
					return ParseFunctionCall(ident);
				}

				if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}

				return ident;
			}

			if (c == '(')
			{
				_index++;
				var inner = ParseEquality();
				SkipWhitespace();
				if (Peek() == ')')
				{
					_index++;
				}
				return inner;
			}

			return ParseBareValue();
		}

		private object ParseFunctionCall(string name)
		{
			_sawExpression = true;
			_index++;
			var args = new List<object>();
			SkipWhitespace();
			if (Peek() != ')')
			{
				while (true)
				{
					args.Add(ParseEquality());
					SkipWhitespace();
					if (Peek() == ',')
					{
						_index++;
						continue;
					}
					break;
				}
			}

			if (Peek() == ')')
			{
				_index++;
			}

			return TranslateFunction(name, args);
		}

		private object TranslateFunction(string name, List<object> args)
		{
			name = name.ToLowerInvariant();
			switch (name)
			{
				case "tag":
					if (args.Count < 1)
					{
						return string.Empty;
					}
					if (args[0] is string tagKey)
					{
						return new object[] { "get", tagKey };
					}
					// Support dynamic tag keys (e.g. tag(concat("seamark:", ...))) by converting the
					// argument to a MapLibre get-expression.
					return new object[] { "get", args[0] };
				case "has_tag_key":
					if (args.Count < 1)
					{
						return false;
					}
					if (args[0] is string hasKey)
					{
						return new object[] { "has", hasKey };
					}
					throw new InvalidOperationException("has_tag_key() expects a string argument.");
				case "concat":
					if (args.Count == 0)
					{
						return string.Empty;
					}
					var concatExpr = new object[args.Count + 1];
					concatExpr[0] = "concat";
					for (var i = 0; i < args.Count; i++)
					{
						concatExpr[i + 1] = args[i];
					}
					return concatExpr;
				case "any":
					if (args.Count == 0)
					{
						return string.Empty;
					}
					var coalesceExpr = new object[args.Count + 1];
					coalesceExpr[0] = "coalesce";
					for (var i = 0; i < args.Count; i++)
					{
						coalesceExpr[i + 1] = args[i];
					}
					return coalesceExpr;
				case "cond":
					if (args.Count < 3)
					{
						throw new InvalidOperationException("cond() expects at least three arguments.");
					}

					// MapCSS cond(condition, trueValue, falseValue)
					// MapLibre expressions use ["case", condition, trueValue, falseValue]
					return new object[] { "case", args[0], args[1], args[2] };
				case "eval":
					return args.Count > 0 ? args[0] : string.Empty;
				default:
					throw new InvalidOperationException($"Unsupported function '{name}'.");
			}
		}

		private bool Match(string token)
		{
			SkipWhitespace();
			if (_text.AsSpan(_index).StartsWith(token, StringComparison.Ordinal))
			{
				_index += token.Length;
				return true;
			}

			return false;
		}

		private string ParseIdent()
		{
			var start = _index;
			while (_index < _text.Length)
			{
				var c = _text[_index];
				if (char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '-')
				{
					_index++;
					continue;
				}
				break;
			}

			return _text[start.._index];
		}

		private object ParseString()
		{
			var quote = _text[_index++];
			var builder = new StringBuilder();
			while (_index < _text.Length)
			{
				var c = _text[_index++];
				if (c == quote)
				{
					break;
				}
				if (c == '\\' && _index < _text.Length)
				{
					var next = _text[_index++];
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

		private object ParseNumber()
		{
			var start = _index;
			if (_text[_index] == '+' || _text[_index] == '-')
			{
				_index++;
			}

			while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '.'))
			{
				_index++;
			}

			var slice = _text[start.._index];
			if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
			{
				return number;
			}

			return slice;
		}

		private object ParseBareValue()
		{
			var start = _index;
			while (_index < _text.Length)
			{
				var c = _text[_index];
				if (char.IsWhiteSpace(c) || c == ',' || c == ')' || c == '(')
				{
					break;
				}
				_index++;
			}

			return _text[start.._index];
		}

		private char Peek() => _index < _text.Length ? _text[_index] : '\0';

		private bool IsNumberStart(char c)
		{
			if (char.IsDigit(c))
			{
				return true;
			}

			if ((c == '+' || c == '-') && _index + 1 < _text.Length)
			{
				return char.IsDigit(_text[_index + 1]);
			}

			return false;
		}

		private void SkipWhitespace()
		{
			while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
			{
				_index++;
			}
		}
	}

	private readonly struct ExpressionParseResult
	{
		public ExpressionParseResult(object value, bool isExpression)
		{
			Value = value;
			IsExpression = isExpression;
		}

		public object Value { get; }
		public bool IsExpression { get; }
	}
}

