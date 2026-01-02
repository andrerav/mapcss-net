using System.Collections.ObjectModel;

namespace MapCss.Styling;

/// <summary>
/// The main entry point for evaluating MapCSS styles against a query.
/// </summary>
/// <remarks>
/// Construct this engine with the raw MapCSS source. It builds an internal AST and exposes
/// the <see cref="Evaluate"/> method to compute styles for a given <see cref="MapCssQuery"/>.
/// The engine processes rules in source order; set statements can add classes that affect
/// later matching and property expressions are evaluated in the context of the provided query.
/// </remarks>
public sealed class MapCssStyleEngine
{
	private readonly MapCssStylesheet _stylesheet;

	/// <summary>
	/// Create a style engine by parsing the MapCSS source text.
	/// </summary>
	/// <param name="mapCss">A string containing MapCSS stylesheet source (must not be null).</param>
	public MapCssStyleEngine(string mapCss)
	{
		if (mapCss is null)
		{
			throw new ArgumentNullException(nameof(mapCss));
		}

		_stylesheet = MapCssParserFacade.Parse(mapCss);
	}

	/// <summary>
	/// Evaluate the stylesheet against the provided query and produce a <see cref="MapCssStyleResult"/>.
	/// </summary>
	/// <remarks>
	/// The evaluation iterates rules in source order, determines which selectors match the
	/// provided query (including subpart matching), applies any <c>set</c> declarations (which
	/// add classes to the matching state), and applies property declarations to named layers.
	/// Property values are evaluated as expressions when possible; expression evaluation failures
	/// gracefully fall back to the original token text.
	/// </remarks>
	/// <param name="query">The target query to evaluate (must not be null).</param>
	/// <returns>A <see cref="MapCssStyleResult"/> containing computed layers and classes.</returns>
	public MapCssStyleResult Evaluate(MapCssQuery query)
	{
		if (query is null)
		{
			throw new ArgumentNullException(nameof(query));
		}

		var state = new MapCssEvaluationState(query);

		foreach (var rule in _stylesheet.Rules)
		{
			var matchedSubparts = state.MatchRule(rule);
			if (matchedSubparts.Count == 0)
			{
				continue;
			}

			foreach (var declaration in rule.Declarations)
			{
				switch (declaration)
				{
					case MapCssSetDeclaration setDeclaration:
						state.ApplySet(setDeclaration);
						break;
					case MapCssPropertyDeclaration propertyDeclaration:
						state.ApplyProperty(propertyDeclaration, matchedSubparts);
						break;
				}
			}
		}

		return state.ToResult();
	}
}

/// <summary>
/// Internal evaluation state used by <see cref="MapCssStyleEngine"/> while processing rules.
/// </summary>
/// <remarks>
/// This state object tracks the set of classes that are active (including ones added by
/// <c>set</c> declarations during evaluation) and accumulates named layers of properties.
/// The layers dictionary maps subpart identifiers (or empty string for non-subpart rules)
/// to a property map. Property values are evaluated through the expression evaluator with
/// a best-effort strategy (exceptions during evaluation fall back to the original token text).
/// </remarks>
internal sealed class MapCssEvaluationState
{
	private readonly MapCssQuery _query;
	private readonly HashSet<string> _classes;
	private readonly Dictionary<string, Dictionary<string, IReadOnlyList<MapCssValue>>> _layers;

	/// <summary>
	/// Create a new evaluation state for the specified query.
	/// </summary>
	/// <param name="query">The query to evaluate against (must not be null).</param>
	public MapCssEvaluationState(MapCssQuery query)
	{
		_query = query;
		_classes = new HashSet<string>(_query.Context.Element.Classes, StringComparer.Ordinal);
		_layers = new Dictionary<string, Dictionary<string, IReadOnlyList<MapCssValue>>>(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The current set of classes active in this evaluation state.
	/// </summary>
	public IReadOnlyCollection<string> Classes => _classes;

	/// <summary>
	/// Determine which subparts of a ruleset match the query given the current state.
	/// </summary>
	/// <remarks>
	/// Iterates all selectors in the ruleset and returns the set of subpart identifiers
	/// (the empty string for rules that do not target a subpart) for selectors that match.
	/// </remarks>
	/// <param name="ruleSet">Ruleset whose selectors should be matched.</param>
	/// <returns>A set of subpart identifiers that matched.</returns>
	public HashSet<string> MatchRule(MapCssRuleSet ruleSet)
	{
		var matchedSubparts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var selector in ruleSet.Selectors)
		{
			if (MapCssSelectorMatcher.Matches(selector, _query, _classes))
			{
				matchedSubparts.Add(selector.Subpart ?? string.Empty);
			}
		}

		return matchedSubparts;
	}

	/// <summary>
	/// Apply a <c>set</c> declaration by adding classes into the current state.
	/// </summary>
	/// <param name="setDeclaration">The declaration containing classes to be added.</param>
	public void ApplySet(MapCssSetDeclaration setDeclaration)
	{
		foreach (var cls in setDeclaration.Classes)
		{
			_classes.Add(cls);
		}
	}

	/// <summary>
	/// Apply a property declaration to one or more subparts, evaluating expressions as needed.
	/// </summary>
	/// <remarks>
	/// For each affected subpart the method ensures a property map exists, evaluates expressions
	/// in the property's value list using <c>ExpressionEvaluator</c>, and stores the resulting
	/// values. If expression evaluation throws, the original token text is kept for that value.
	/// </remarks>
	/// <param name="propertyDeclaration">The property declaration to apply.</param>
	/// <param name="subparts">Set of subpart identifiers to which the property applies.</param>
	public void ApplyProperty(MapCssPropertyDeclaration propertyDeclaration, HashSet<string> subparts)
	{
		foreach (var subpart in subparts)
		{
			if (!_layers.TryGetValue(subpart, out var properties))
			{
				properties = new Dictionary<string, IReadOnlyList<MapCssValue>>(StringComparer.OrdinalIgnoreCase);
				_layers[subpart] = properties;
			}

			// Evaluate expressions in property values using the current query context
			var evaluated = new List<MapCssValue>();
			foreach (var v in propertyDeclaration.Values)
			{
				try
				{
					var text = ExpressionEvaluator.Evaluate(v.Text, _query);
					evaluated.Add(new MapCssValue(text));
				}
				catch
				{
					// on error fall back to original token text
					evaluated.Add(v);
				}
			}

			properties[propertyDeclaration.Name] = evaluated;
		}
	}

	/// <summary>
	/// Produce a <see cref="MapCssStyleResult"/> from the accumulated state.
	/// </summary>
	/// <remarks>
	/// Converts internal mutable dictionaries into read-only collections and returns the
	/// final set of classes and layers for the query evaluation.
	/// </remarks>
	/// <returns>The computed <see cref="MapCssStyleResult"/>.</returns>
	public MapCssStyleResult ToResult()
	{
		var layers = new Dictionary<string, MapCssStyleLayer>(StringComparer.OrdinalIgnoreCase);
		foreach (var (subpart, props) in _layers)
		{
			layers[subpart] = new MapCssStyleLayer(new ReadOnlyDictionary<string, IReadOnlyList<MapCssValue>>(props));
		}

		return new MapCssStyleResult(
			new ReadOnlyDictionary<string, MapCssStyleLayer>(layers),
			_classes.ToArray());
	}
}
