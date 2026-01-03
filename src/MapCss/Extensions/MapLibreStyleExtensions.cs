using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using MapCss.Styling;

namespace MapCss.Extensions;

public enum MapLibreGeometryType
{
	Point,
	Line,
	Polygon
}

public enum MapLibreLayerType
{
	Circle,
	Line,
	Fill,
	Symbol
}

public sealed class MapLibreStyleOptions
{
	public double? IconBaseSize { get; init; }
	public Func<string, bool>? TextValueIsTag { get; init; }
}

public sealed class MapLibreStyleWarning
{
	public MapLibreStyleWarning(string mapCssProperty, string message)
	{
		MapCssProperty = mapCssProperty;
		Message = message;
	}

	public string MapCssProperty { get; }
	public string Message { get; }
}

public sealed class MapLibreStyleLayer
{
	public MapLibreStyleLayer(
		MapLibreLayerType layerType,
		IReadOnlyDictionary<string, object> paint,
		IReadOnlyDictionary<string, object> layout,
		IReadOnlyList<MapLibreStyleWarning> warnings)
	{
		LayerType = layerType;
		Paint = paint;
		Layout = layout;
		Warnings = warnings;
	}

	public MapLibreLayerType LayerType { get; }
	public IReadOnlyDictionary<string, object> Paint { get; }
	public IReadOnlyDictionary<string, object> Layout { get; }
	public IReadOnlyList<MapLibreStyleWarning> Warnings { get; }
}

public sealed class MapLibreStyleResult
{
	public MapLibreStyleResult(
		IReadOnlyDictionary<string, IReadOnlyList<MapLibreStyleLayer>> layers,
		IReadOnlyCollection<string> classes)
	{
		Layers = layers;
		Classes = classes;
	}

	public IReadOnlyDictionary<string, IReadOnlyList<MapLibreStyleLayer>> Layers { get; }
	public IReadOnlyCollection<string> Classes { get; }
}

public static class MapLibreStyleExtensions
{
	private static readonly Regex TagKeyRegex = new("^[A-Za-z0-9_:-]+$", RegexOptions.Compiled);
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

	public static MapLibreStyleResult ToMapLibreStyle(
		this MapCssStyleResult style,
		MapLibreGeometryType geometry,
		MapLibreStyleOptions? options = null)
	{
		if (style is null)
		{
			throw new ArgumentNullException(nameof(style));
		}

		options ??= new MapLibreStyleOptions();

		var layers = new Dictionary<string, IReadOnlyList<MapLibreStyleLayer>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (subpart, layer) in style.Layers)
		{
			layers[subpart] = ConvertLayer(layer, geometry, options);
		}

		return new MapLibreStyleResult(
			new ReadOnlyDictionary<string, IReadOnlyList<MapLibreStyleLayer>>(layers),
			style.Classes);
	}

	public static MapLibreStyleResult ToMapLibreStyle(
		this MapCssStyleResult style,
		MapCssQuery query,
		MapLibreStyleOptions? options = null)
	{
		if (query is null)
		{
			throw new ArgumentNullException(nameof(query));
		}

		var geometry = MapGeometryFromElementType(query.Context.Element.Type);
		return style.ToMapLibreStyle(geometry, options);
	}

	private static IReadOnlyList<MapLibreStyleLayer> ConvertLayer(
		MapCssStyleLayer layer,
		MapLibreGeometryType geometry,
		MapLibreStyleOptions options)
	{
		var circle = geometry == MapLibreGeometryType.Point
			? new LayerBuilder(MapLibreLayerType.Circle)
			: null;
		var line = geometry == MapLibreGeometryType.Line || geometry == MapLibreGeometryType.Polygon
			? new LayerBuilder(MapLibreLayerType.Line)
			: null;
		var fill = geometry == MapLibreGeometryType.Polygon
			? new LayerBuilder(MapLibreLayerType.Fill)
			: null;
		var symbol = new LayerBuilder(MapLibreLayerType.Symbol);

		var unassignedWarnings = new List<MapLibreStyleWarning>();

		foreach (var (rawKey, values) in layer.Properties)
		{
			var key = rawKey.ToLowerInvariant();
			switch (key)
			{
				case "width":
					if (TryGetNumber(values, out var width, out var widthWarning))
					{
						if (geometry == MapLibreGeometryType.Point)
						{
							SetPaint(circle, "circle-radius", width, unassignedWarnings);
						}
						else
						{
							SetPaint(line, "line-width", width, unassignedWarnings);
						}
					}
					else
					{
						AddWarning(line ?? circle, "width", widthWarning ?? "Unsupported width value.", unassignedWarnings);
					}
					break;

				case "color":
					if (geometry == MapLibreGeometryType.Point)
					{
						SetPaint(circle, "circle-color", FirstValue(values), unassignedWarnings);
					}
					else if (geometry == MapLibreGeometryType.Polygon)
					{
						SetPaint(line, "line-color", FirstValue(values), unassignedWarnings);
					}
					else
					{
						SetPaint(line, "line-color", FirstValue(values), unassignedWarnings);
					}
					break;

				case "opacity":
					if (TryGetNumber(values, out var opacity, out var opacityWarning))
					{
						if (geometry == MapLibreGeometryType.Point)
						{
							SetPaint(circle, "circle-opacity", opacity, unassignedWarnings);
						}
						else if (geometry == MapLibreGeometryType.Polygon)
						{
							SetPaint(line, "line-opacity", opacity, unassignedWarnings);
						}
						else
						{
							SetPaint(line, "line-opacity", opacity, unassignedWarnings);
						}
					}
					else
					{
						AddWarning(line ?? circle, "opacity", opacityWarning ?? "Unsupported opacity value.", unassignedWarnings);
					}
					break;

				case "dashes":
					SetPaint(line, "line-dasharray", ExtractNumbers(values), unassignedWarnings);
					break;

				case "linecap":
					SetLayout(line, "line-cap", FirstValue(values), unassignedWarnings);
					break;

				case "casing-width":
				case "casing-color":
				case "casing-opacity":
					AddWarning(line, key, "Casing requires a separate line layer.", unassignedWarnings);
					break;

				case "fill-color":
					SetPaint(fill, "fill-color", FirstValue(values), unassignedWarnings);
					break;

				case "fill-opacity":
					if (TryGetNumber(values, out var fillOpacity, out var fillOpacityWarning))
					{
						SetPaint(fill, "fill-opacity", fillOpacity, unassignedWarnings);
					}
					else
					{
						AddWarning(fill, "fill-opacity", fillOpacityWarning ?? "Unsupported fill-opacity value.", unassignedWarnings);
					}
					break;

				case "fill-image":
					SetPaint(fill, "fill-pattern", FirstValue(values), unassignedWarnings);
					AddWarning(fill, "fill-image", "MapLibre requires the image to be registered via sprite/addImage.", unassignedWarnings);
					break;

				case "symbol-shape":
				{
					var shape = FirstValue(values);
					if (!string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase))
					{
						AddWarning(circle, "symbol-shape", "Only circle maps directly to MapLibre; other shapes need custom icons.", unassignedWarnings);
					}
					break;
				}

				case "symbol-size":
					if (TryGetNumber(values, out var size, out var sizeWarning))
					{
						SetPaint(circle, "circle-radius", size, unassignedWarnings);
					}
					else
					{
						AddWarning(circle, "symbol-size", sizeWarning ?? "Unsupported symbol-size value.", unassignedWarnings);
					}
					break;

				case "symbol-stroke-color":
					SetPaint(circle, "circle-stroke-color", FirstValue(values), unassignedWarnings);
					break;

				case "symbol-fill-color":
					SetPaint(circle, "circle-color", FirstValue(values), unassignedWarnings);
					break;

				case "symbol-fill-opacity":
					if (TryGetNumber(values, out var fillOpacity2, out var fillOpacityWarning2))
					{
						SetPaint(circle, "circle-opacity", fillOpacity2, unassignedWarnings);
					}
					else
					{
						AddWarning(circle, "symbol-fill-opacity", fillOpacityWarning2 ?? "Unsupported symbol-fill-opacity value.", unassignedWarnings);
					}
					break;

				case "icon-image":
					SetLayout(symbol, "icon-image", FirstValue(values), unassignedWarnings);
					AddWarning(symbol, "icon-image", "MapLibre requires the image to be registered via sprite/addImage.", unassignedWarnings);
					break;

				case "icon-width":
				case "icon-height":
					if (TryGetNumber(values, out var iconSize, out var iconSizeWarning))
					{
						if (options.IconBaseSize is > 0)
						{
							SetLayout(symbol, "icon-size", iconSize / options.IconBaseSize.Value, unassignedWarnings);
						}
						else
						{
							SetLayout(symbol, "icon-size", iconSize, unassignedWarnings);
							AddWarning(symbol, key, "MapLibre icon-size expects a scale; set IconBaseSize to convert.", unassignedWarnings);
						}
					}
					else
					{
						AddWarning(symbol, key, iconSizeWarning ?? "Unsupported icon size value.", unassignedWarnings);
					}
					break;

				case "icon-offset-x":
					if (TryGetNumber(values, out var iconOffsetX, out var iconOffsetXWarning))
					{
						symbol.SetIconOffsetX(iconOffsetX);
					}
					else
					{
						AddWarning(symbol, "icon-offset-x", iconOffsetXWarning ?? "Unsupported icon-offset-x value.", unassignedWarnings);
					}
					break;

				case "icon-offset-y":
					if (TryGetNumber(values, out var iconOffsetY, out var iconOffsetYWarning))
					{
						symbol.SetIconOffsetY(iconOffsetY);
					}
					else
					{
						AddWarning(symbol, "icon-offset-y", iconOffsetYWarning ?? "Unsupported icon-offset-y value.", unassignedWarnings);
					}
					break;

				case "icon-opacity":
					if (TryGetNumber(values, out var iconOpacity, out var iconOpacityWarning))
					{
						SetPaint(symbol, "icon-opacity", iconOpacity, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "icon-opacity", iconOpacityWarning ?? "Unsupported icon-opacity value.", unassignedWarnings);
					}
					break;

				case "icon-orientation":
					if (TryGetNumber(values, out var iconOrientation, out var iconOrientationWarning))
					{
						SetLayout(symbol, "icon-rotate", iconOrientation, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "icon-orientation", iconOrientationWarning ?? "Unsupported icon-orientation value.", unassignedWarnings);
					}
					break;

				case "repeat-image":
					if (geometry != MapLibreGeometryType.Line)
					{
						AddWarning(symbol, "repeat-image", "Repeat images are only supported on line geometries.", unassignedWarnings);
					}
					SetLayout(symbol, "symbol-placement", "line", unassignedWarnings);
					SetLayout(symbol, "icon-image", FirstValue(values), unassignedWarnings);
					AddWarning(symbol, "repeat-image", "MapLibre requires the image to be registered via sprite/addImage.", unassignedWarnings);
					break;

				case "repeat-image-width":
					if (TryGetNumber(values, out var repeatSize, out var repeatSizeWarning))
					{
						if (options.IconBaseSize is > 0)
						{
							SetLayout(symbol, "icon-size", repeatSize / options.IconBaseSize.Value, unassignedWarnings);
						}
						else
						{
							SetLayout(symbol, "icon-size", repeatSize, unassignedWarnings);
							AddWarning(symbol, "repeat-image-width", "MapLibre icon-size expects a scale; set IconBaseSize to convert.", unassignedWarnings);
						}
					}
					else
					{
						AddWarning(symbol, "repeat-image-width", repeatSizeWarning ?? "Unsupported repeat-image-width value.", unassignedWarnings);
					}
					break;

				case "repeat-image-spacing":
					if (TryGetNumber(values, out var spacing, out var spacingWarning))
					{
						SetLayout(symbol, "symbol-spacing", spacing, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "repeat-image-spacing", spacingWarning ?? "Unsupported repeat-image-spacing value.", unassignedWarnings);
					}
					break;

				case "repeat-image-phase":
					AddWarning(symbol, "repeat-image-phase", "MapLibre does not support phase offsets.", unassignedWarnings);
					break;

				case "text":
				{
					var text = FirstValue(values);
					if (text is null)
					{
						break;
					}

					object fieldValue = text;
					if (ShouldUseTagExpression(text, options))
					{
						fieldValue = new object[] { "get", text };
					}

					SetLayout(symbol, "text-field", fieldValue, unassignedWarnings);
					break;
				}

				case "text-color":
					SetPaint(symbol, "text-color", FirstValue(values), unassignedWarnings);
					break;

				case "text-halo-colour":
					SetPaint(symbol, "text-halo-color", FirstValue(values), unassignedWarnings);
					break;

				case "text-halo-radius":
					if (TryGetNumber(values, out var haloWidth, out var haloWidthWarning))
					{
						SetPaint(symbol, "text-halo-width", haloWidth, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "text-halo-radius", haloWidthWarning ?? "Unsupported text-halo-radius value.", unassignedWarnings);
					}
					break;

				case "text-halo-opacity":
					AddWarning(symbol, "text-halo-opacity", "MapLibre has no halo opacity; bake alpha into the halo color.", unassignedWarnings);
					break;

				case "text-anchor-horizontal":
					symbol.SetTextAnchorHorizontal(FirstValue(values));
					break;

				case "text-anchor-vertical":
					symbol.SetTextAnchorVertical(FirstValue(values));
					break;

				case "text-position":
				{
					var position = FirstValue(values);
					if (string.Equals(position, "center", StringComparison.OrdinalIgnoreCase))
					{
						symbol.SetTextAnchorCenter();
					}
					else
					{
						AddWarning(symbol, "text-position", "Unsupported text-position value.", unassignedWarnings);
					}

					break;
				}

				case "font-family":
					SetLayout(symbol, "text-font", SplitFonts(FirstValue(values)), unassignedWarnings);
					break;

				case "font-size":
					if (TryGetNumber(values, out var fontSize, out var fontSizeWarning))
					{
						SetLayout(symbol, "text-size", fontSize, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "font-size", fontSizeWarning ?? "Unsupported font-size value.", unassignedWarnings);
					}
					break;

				case "font-style":
					AddWarning(symbol, "font-style", "MapLibre uses font names rather than a separate style.", unassignedWarnings);
					break;

				case "z-index":
					if (TryGetNumber(values, out var sortKey, out var sortKeyWarning))
					{
						SetLayout(symbol, "symbol-sort-key", sortKey, unassignedWarnings);
					}
					else
					{
						AddWarning(symbol, "z-index", sortKeyWarning ?? "Unsupported z-index value.", unassignedWarnings);
					}
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
					AddWarning(null, key, "MapCSS metadata or canvas settings are not supported in MapLibre.", unassignedWarnings);
					break;

				default:
					AddWarning(null, rawKey, "Unsupported MapCSS property.", unassignedWarnings);
					break;
			}
		}

		var output = new List<MapLibreStyleLayer>();
		if (unassignedWarnings.Count > 0)
		{
			var target = FindWarningTarget(fill, line, circle, symbol);
			if (target is null)
			{
				var fallback = geometry switch
				{
					MapLibreGeometryType.Point => new LayerBuilder(MapLibreLayerType.Circle),
					MapLibreGeometryType.Polygon => new LayerBuilder(MapLibreLayerType.Fill),
					_ => new LayerBuilder(MapLibreLayerType.Line)
				};
				fallback.AppendWarnings(unassignedWarnings);
				output.Add(fallback.Build());
				return output;
			}

			target.AppendWarnings(unassignedWarnings);
		}

		AddLayerIfHasContent(fill, output);
		AddLayerIfHasContent(line, output);
		AddLayerIfHasContent(circle, output);
		AddLayerIfHasContent(symbol, output);

		return output;
	}

	private static void AddLayerIfHasContent(LayerBuilder? builder, List<MapLibreStyleLayer> output)
	{
		if (builder is null || !builder.HasContent)
		{
			return;
		}

		output.Add(builder.Build());
	}

	private static LayerBuilder? FindWarningTarget(params LayerBuilder?[] builders)
	{
		foreach (var builder in builders)
		{
			if (builder is not null && builder.HasContent)
			{
				return builder;
			}
		}

		return null;
	}

	private static void SetPaint(
		LayerBuilder? builder,
		string key,
		object? value,
		List<MapLibreStyleWarning> unassigned)
	{
		if (builder is null)
		{
			AddWarning(null, key, "No compatible layer for this property.", unassigned);
			return;
		}

		if (value is null)
		{
			AddWarning(builder, key, "Missing value.", unassigned);
			return;
		}

		builder.SetPaint(key, value);
	}

	private static void SetLayout(
		LayerBuilder? builder,
		string key,
		object? value,
		List<MapLibreStyleWarning> unassigned)
	{
		if (builder is null)
		{
			AddWarning(null, key, "No compatible layer for this property.", unassigned);
			return;
		}

		if (value is null)
		{
			AddWarning(builder, key, "Missing value.", unassigned);
			return;
		}

		builder.SetLayout(key, value);
	}

	private static void AddWarning(
		LayerBuilder? builder,
		string mapCssProperty,
		string message,
		List<MapLibreStyleWarning> unassigned)
	{
		if (builder is null)
		{
			unassigned.Add(new MapLibreStyleWarning(mapCssProperty, message));
		}
		else
		{
			builder.AddWarning(mapCssProperty, message);
		}
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

	private static bool TryGetNumber(
		IReadOnlyList<string> values,
		out double number,
		out string? warning)
	{
		warning = null;
		number = 0;
		var value = FirstValue(values);
		if (value is null)
		{
			warning = "Missing numeric value.";
			return false;
		}

		if (NamedWidths.TryGetValue(value, out number))
		{
			return true;
		}

		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
		{
			if (value.StartsWith("+", StringComparison.Ordinal))
			{
				warning = "Relative values are approximated as absolute numbers.";
			}
			return true;
		}

		warning = $"Unsupported numeric value '{value}'.";
		return false;
	}

	private static double[] ExtractNumbers(IReadOnlyList<string> values)
	{
		var text = string.Join(" ", values);
		var matches = Regex.Matches(text, "[-+]?[0-9]*\\.?[0-9]+");
		var list = new List<double>(matches.Count);
		foreach (Match match in matches)
		{
			if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
			{
				list.Add(parsed);
			}
		}

		return list.ToArray();
	}

	private static string[] SplitFonts(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}

		return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static bool ShouldUseTagExpression(string text, MapLibreStyleOptions options)
	{
		if (options.TextValueIsTag != null)
		{
			return options.TextValueIsTag(text);
		}

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
			MapCssElementType.Canvas => MapLibreGeometryType.Polygon,
			MapCssElementType.Relation => MapLibreGeometryType.Polygon,
			_ => MapLibreGeometryType.Point
		};
	}

	private sealed class LayerBuilder
	{
		private readonly Dictionary<string, object> _paint = new(StringComparer.Ordinal);
		private readonly Dictionary<string, object> _layout = new(StringComparer.Ordinal);
		private readonly List<MapLibreStyleWarning> _warnings = new();
		private double _iconOffsetX;
		private double _iconOffsetY;
		private bool _hasIconOffset;
		private string? _textAnchorHorizontal;
		private string? _textAnchorVertical;

		public LayerBuilder(MapLibreLayerType layerType)
		{
			LayerType = layerType;
		}

		public MapLibreLayerType LayerType { get; }
		public bool HasContent => _paint.Count > 0 || _layout.Count > 0 || _warnings.Count > 0;

		public void SetPaint(string key, object value) => _paint[key] = value;
		public void SetLayout(string key, object value) => _layout[key] = value;

		public void AddWarning(string mapCssProperty, string message) =>
			_warnings.Add(new MapLibreStyleWarning(mapCssProperty, message));

		public void AppendWarnings(IEnumerable<MapLibreStyleWarning> warnings) =>
			_warnings.AddRange(warnings);

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

		public void SetTextAnchorHorizontal(string? value)
		{
			_textAnchorHorizontal = value;
			UpdateTextAnchor();
		}

		public void SetTextAnchorVertical(string? value)
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

		public MapLibreStyleLayer Build()
		{
			return new MapLibreStyleLayer(
				LayerType,
				new ReadOnlyDictionary<string, object>(_paint),
				new ReadOnlyDictionary<string, object>(_layout),
				new ReadOnlyCollection<MapLibreStyleWarning>(_warnings));
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
}
