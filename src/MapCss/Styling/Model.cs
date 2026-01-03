using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapCss.Styling;

/// <summary>
/// Enumeration of element types understood by the MapCSS engine.
/// </summary>
/// <remarks>
/// Used by selectors and context objects to identify whether an element represents a node, way,
/// relation, area, canvas, or matches any element type.
/// </remarks>
public enum MapCssElementType
{
	Any,
	Node,
	Way,
	Relation,
	Area,
	Canvas
} 

/// <summary>
/// Represents a MapCSS element together with its metadata (tags, classes and pseudo-classes).
/// </summary>
/// <remarks>
/// Instances are immutable and provide the basic information used when matching selectors and
/// evaluating style rules for a single element.
/// </remarks>
public sealed class MapCssElement
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MapCssElement"/> class.
	/// </summary>
	/// <param name="type">The element type (node/way/relation/area/canvas).</param>
	/// <param name="tags">A dictionary of tag keys and values (must not be null).</param>
	/// <param name="classes">Optional collection of classes assigned to the element.</param>
	/// <param name="pseudoClasses">Optional collection of pseudo-classes assigned to the element.</param>
	public MapCssElement(
		MapCssElementType type,
		IReadOnlyDictionary<string, string> tags,
		IReadOnlyCollection<string>? classes = null,
		IReadOnlyCollection<string>? pseudoClasses = null)
	{
		Type = type;
		Tags = tags ?? throw new ArgumentNullException(nameof(tags));
		Classes = classes ?? Array.Empty<string>();
		PseudoClasses = pseudoClasses ?? Array.Empty<string>();
	}

	/// <summary>Gets the element type.</summary>
	public MapCssElementType Type { get; }

	/// <summary>Gets the element's tag dictionary.</summary>
	public IReadOnlyDictionary<string, string> Tags { get; }

	/// <summary>Gets the collection of classes assigned to the element.</summary>
	public IReadOnlyCollection<string> Classes { get; }

	/// <summary>Gets the collection of pseudo-classes for the element.</summary>
	public IReadOnlyCollection<string> PseudoClasses { get; }
} 

/// <summary>
/// Represents the contextual placement of an element within an ancestry chain.
/// </summary>
/// <remarks>
/// A <see cref="MapCssContext"/> holds an element and an optional parent context to model
/// ancestor relationships. When elements are related by links (e.g. from a way to a node),
/// the <see cref="LinkTags"/> may hold tags specific to the connecting link.
/// </remarks>
public sealed class MapCssContext
{
	/// <summary>
	/// Initializes a new instance of <see cref="MapCssContext"/>.
	/// </summary>
	/// <param name="element">The element for this context (must not be null).</param>
	/// <param name="parent">Optional parent context representing the immediate ancestor.</param>
	/// <param name="linkTags">Optional tags describing the link to the parent context.</param>
	public MapCssContext(
		MapCssElement element,
		MapCssContext? parent = null,
		IReadOnlyDictionary<string, string>? linkTags = null)
	{
		Element = element ?? throw new ArgumentNullException(nameof(element));
		Parent = parent;
		LinkTags = linkTags;
	}

	/// <summary>Gets the element held by this context.</summary>
	public MapCssElement Element { get; }

	/// <summary>Gets the parent context if any (null for the leaf/top-level context).</summary>
	public MapCssContext? Parent { get; }

	/// <summary>Gets optional tags associated with the link to the parent context.</summary>
	public IReadOnlyDictionary<string, string>? LinkTags { get; }
} 

/// <summary>
/// Encapsulates a style matching query: the target context and an optional zoom level.
/// </summary>
/// <remarks>
/// The class provides convenient constructors for queries rooted at a single element or an
/// existing <see cref="MapCssContext"/> chain. The zoom is used for zoom-range based selector matching.
/// </remarks>
public sealed class MapCssQuery
{
	/// <summary>
	/// Create a query for a single element at an optional zoom level.
	/// </summary>
	public MapCssQuery(MapCssElement element, int? zoom = null)
		: this(new MapCssContext(element), zoom)
	{
	}

	/// <summary>
	/// Create a query from a pre-built context and optional zoom level.
	/// </summary>
	public MapCssQuery(MapCssContext context, int? zoom = null)
	{
		Context = context ?? throw new ArgumentNullException(nameof(context));
		Zoom = zoom;
	}

	/// <summary>Gets the root context for the query.</summary>
	public MapCssContext Context { get; }

	/// <summary>Optional zoom level used for zoom range checks.</summary>
	public int? Zoom { get; }
} 

/// <summary>
/// Represents the computed style for a query: a set of named layers and the resulting classes.
/// </summary>
/// <remarks>
/// Layers are named collections of properties that the style engine produces after evaluating
/// matching rules. The <see cref="Classes"/> collection contains classes that should be applied
/// to the target element as a result of the matching process.
/// </remarks>
public sealed class MapCssStyleResult
{
	internal MapCssStyleResult(
		IReadOnlyDictionary<string, MapCssStyleLayer> layers,
		IReadOnlyCollection<string> classes)
	{
		Layers = layers;
		Classes = classes;
	}

	/// <summary>Gets the dictionary of named style layers.</summary>
	public IReadOnlyDictionary<string, MapCssStyleLayer> Layers { get; }

	/// <summary>Gets the collection of classes produced by style evaluation.</summary>
	public IReadOnlyCollection<string> Classes { get; }
} 

/// <summary>
/// A named collection of style properties produced for a matched rule layer.
/// </summary>
/// <remarks>
/// Each layer maps property names to their list of MapCSS values (e.g. color, numeric values,
/// or bare words) and is used as part of the overall <see cref="MapCssStyleResult"/>.
/// </remarks>
public sealed class MapCssStyleLayer
{
	internal MapCssStyleLayer(IReadOnlyDictionary<string, IReadOnlyList<string>> properties)
	{
		Properties = properties;
	}

	/// <summary>Gets the set of properties and their values for the layer.</summary>
	public IReadOnlyDictionary<string, IReadOnlyList<string>> Properties { get; }
}
