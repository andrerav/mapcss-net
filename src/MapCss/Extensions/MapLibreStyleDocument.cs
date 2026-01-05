using System;

namespace MapCss.Extensions;

/// <summary>
/// Represents a MapLibre style document with the generated layer list.
/// </summary>
public sealed class MapLibreStyleDocument
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibreStyleDocument"/> class.
	/// </summary>
	/// <param name="layers">The compiled MapLibre layer definitions.</param>
	public MapLibreStyleDocument(IReadOnlyList<MapLibreLayerDefinition> layers)
	{
		Layers = layers ?? throw new ArgumentNullException(nameof(layers));
	}

	/// <summary>
	/// Gets the MapLibre style specification version (always 8).
	/// </summary>
	public int Version { get; } = 8;

	/// <summary>
	/// Gets the compiled MapLibre layer definitions in draw order.
	/// </summary>
	public IReadOnlyList<MapLibreLayerDefinition> Layers { get; }
}

/// <summary>
/// Represents a single MapLibre layer definition.
/// </summary>
public sealed class MapLibreLayerDefinition
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibreLayerDefinition"/> class.
	/// </summary>
	/// <param name="id">The unique layer identifier.</param>
	/// <param name="type">The MapLibre layer type.</param>
	/// <param name="filter">The filter expression for the layer.</param>
	/// <param name="paint">The paint properties for the layer.</param>
	/// <param name="layout">The layout properties for the layer.</param>
	/// <param name="minZoom">Optional minimum zoom.</param>
	/// <param name="maxZoom">Optional maximum zoom.</param>
	public MapLibreLayerDefinition(
		string id,
		MapLibreLayerType type,
		object? filter,
		IReadOnlyDictionary<string, object> paint,
		IReadOnlyDictionary<string, object> layout,
		double? minZoom,
		double? maxZoom)
	{
		Id = id ?? throw new ArgumentNullException(nameof(id));
		Type = type;
		Filter = filter;
		Paint = paint ?? throw new ArgumentNullException(nameof(paint));
		Layout = layout ?? throw new ArgumentNullException(nameof(layout));
		MinZoom = minZoom;
		MaxZoom = maxZoom;
	}

	/// <summary>
	/// Gets the unique layer identifier.
	/// </summary>
	public string Id { get; }

	/// <summary>
	/// Gets the MapLibre layer type.
	/// </summary>
	public MapLibreLayerType Type { get; }

	/// <summary>
	/// Gets the MapLibre filter expression.
	/// </summary>
	public object? Filter { get; }

	/// <summary>
	/// Gets the paint property map.
	/// </summary>
	public IReadOnlyDictionary<string, object> Paint { get; }

	/// <summary>
	/// Gets the layout property map.
	/// </summary>
	public IReadOnlyDictionary<string, object> Layout { get; }

	/// <summary>
	/// Gets the minimum zoom level for the layer.
	/// </summary>
	public double? MinZoom { get; }

	/// <summary>
	/// Gets the maximum zoom level for the layer.
	/// </summary>
	public double? MaxZoom { get; }
}

/// <summary>
/// Represents a translation warning emitted during compilation.
/// </summary>
public sealed class MapLibreTranslationWarning
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibreTranslationWarning"/> class.
	/// </summary>
	/// <param name="ruleIndex">The sequential rule index that emitted the warning.</param>
	/// <param name="selectorIndex">The selector index within the ruleset.</param>
	/// <param name="property">The MapCSS property name, if applicable.</param>
	/// <param name="message">The warning message.</param>
	public MapLibreTranslationWarning(int ruleIndex, int selectorIndex, string? property, string message)
	{
		RuleIndex = ruleIndex;
		SelectorIndex = selectorIndex;
		Property = property;
		Message = message ?? throw new ArgumentNullException(nameof(message));
	}

	/// <summary>
	/// Gets the sequential rule index that emitted the warning.
	/// </summary>
	public int RuleIndex { get; }

	/// <summary>
	/// Gets the selector index within the ruleset.
	/// </summary>
	public int SelectorIndex { get; }

	/// <summary>
	/// Gets the MapCSS property name, if applicable.
	/// </summary>
	public string? Property { get; }

	/// <summary>
	/// Gets the warning message.
	/// </summary>
	public string Message { get; }
}

/// <summary>
/// Represents the result of translating MapCSS into MapLibre layer definitions.
/// </summary>
public sealed class MapLibreTranslationResult
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapLibreTranslationResult"/> class.
	/// </summary>
	/// <param name="style">The generated MapLibre style document.</param>
	/// <param name="warnings">The collection of translation warnings.</param>
	public MapLibreTranslationResult(
		MapLibreStyleDocument style,
		IReadOnlyList<MapLibreTranslationWarning> warnings)
	{
		Style = style ?? throw new ArgumentNullException(nameof(style));
		Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
	}

	/// <summary>
	/// Gets the generated MapLibre style document.
	/// </summary>
	public MapLibreStyleDocument Style { get; }

	/// <summary>
	/// Gets the collection of translation warnings.
	/// </summary>
	public IReadOnlyList<MapLibreTranslationWarning> Warnings { get; }
}
