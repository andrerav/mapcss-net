# MapCSS.NET

This repo provides a [MapCSS](https://wiki.openstreetmap.org/wiki/MapCSS/0.2) parser and a small style evaluation engine aimed at JOSM-style MapCSS.
The main entry point for consumers is `MapCssStyleEngine`, which parses a stylesheet string and
returns the properties that apply to a given element (node, way, relation, area, canvas).

The parser is generated from the ANTLR4 grammars in `tools/Parser.g4` and
`tools/Lexer.g4`. The grammars themselves have been reverse engineered from existing samples and documentation. For that reason, correctness is not guaranteed. There exists 15 year old [a BNF definition](https://wiki.openstreetmap.org/wiki/MapCSS/0.2/BNF) for MapCSS that appears to be incomplete, that why the lexer and grammar for this parser was reverse engineered instead.

The styling engine builds a lightweight AST and performs
selector matching (including parent/child combinators, attribute filters, classes, pseudo-classes,
and zoom ranges), then applies properties to subpart layers.

## Quick Start (C#)

Install the styling package from NuGet:

```powershell
dotnet package add MapCss.NET
```

Evaluate a simple stylesheet against a way:

```csharp
using MapCss.Styling;

var css = @"
way[highway=primary] { color: #ff5500; width: 2; }
";

var element = new MapCssElement(
    MapCssElementType.Way,
    new Dictionary<string, string>
    {
        ["highway"] = "primary"
    });

var engine = new MapCssStyleEngine(css);
var result = engine.Evaluate(new MapCssQuery(element, zoom: 14));

// Default layer uses empty string.
var color = result.Layers[""].Properties["color"][0];
var width = result.Layers[""].Properties["width"][0];
```

## Using Subparts (layers)

Subparts (e.g., `::outline`) are exposed as layers in the result. Each layer has its own set of
properties.

```csharp
var css = @"
way[waterway=river]::outline { casing-color: #2244aa; casing-width: 3; }
way[waterway=river] { color: #88bbff; width: 2; }
";

var element = new MapCssElement(
    MapCssElementType.Way,
    new Dictionary<string, string> { ["waterway"] = "river" });

var engine = new MapCssStyleEngine(css);
var result = engine.Evaluate(new MapCssQuery(element, zoom: 12));

// Default layer
var riverColor = result.Layers[""].Properties["color"][0];

// Subpart layer
var outlineLayer = result.Layers["outline"];
var casingColor = outlineLayer.Properties["casing-color"][0];
```

## MapLibre Style Conversion

If you want MapLibre-friendly paint/layout objects, use the extension method in `MapCss.Extensions`.
It converts a `MapCssStyleResult` into a `MapLibreStyleResult` with layers grouped by subpart.

```csharp
using MapCss.Extensions;
using MapCss.Styling;

var element = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string> { ["seamark:name"] = "Alpha" });

var engine = new MapCssStyleEngine("node { text: \"seamark:name\"; symbol-size: 4; color: red; }");
var result = engine.Evaluate(new MapCssQuery(element, zoom: 15));

// Choose geometry explicitly
var maplibre = result.ToMapLibreStyle(MapLibreGeometryType.Point);

// Or infer geometry from the query element type
var maplibre2 = result.ToMapLibreStyle(new MapCssQuery(element));

var symbolLayer = maplibre.Layers[""].Single(x => x.LayerType == MapLibreLayerType.Symbol);
var textField = symbolLayer.Layout["text-field"]; // ["get", "seamark:name"]
```

Notes:
- Icon URLs are kept as-is. MapLibre requires you to register images via sprite or `addImage`.
- `MapLibreStyleOptions.IconBaseSize` converts pixel widths/heights into MapLibre `icon-size` scale.
- Text values are treated as tag lookups when they look like tag keys (`seamark:name`, `depth`, `name`, etc.).
  Override with `MapLibreStyleOptions.TextValueIsTag` if needed.
- Unsupported properties are reported as warnings on the produced layers.

## MapLibre Style Translation (AST -> Style JSON)

If you want a full MapLibre style document (layers only), use `MapCssToMapLibreTranslator`.
It compiles MapCSS AST into MapLibre layer definitions in MapCSS rule order, so you can attach
your own sources and source-layers in the frontend.

```csharp
using MapCss.Extensions;

var css = File.ReadAllText("samples/INT1_MapCSS.mapcss");
var translator = new MapCssToMapLibreTranslator();

var result = translator.Translate(css, new MapLibreTranslationOptions
{
    StylesheetId = "int1",
    LayerIdPrefix = "mapcss_",
    IconBaseSize = 16
});

var style = result.Style;     // MapLibreStyleDocument (version 8 + layers)
var warnings = result.Warnings;
```

Notes:
- One layer is emitted per MapCSS rule. Extra layers are created only for casing and repeat-image.
- `set` classes are not evaluated at translation time. Precompute `class:*` boolean properties in your tiles,
  or provide `ClassPropertyResolver` to map class names to your schema.
- Regex selectors are dropped with warnings.
- Geometry is inferred from selectors and properties; override with `GeometryResolver` when needed.
- Unsupported properties or expressions fall back to literals with warnings (or throw in strict mode).

## Example: INT1_MapCSS.mapcss (seamarks)

The `samples/INT1_MapCSS.mapcss` file contains rules for common seamarks. You can load the file
once and evaluate multiple elements against it:

```csharp
using MapCss.Styling;
using System.IO;

var css = File.ReadAllText(Path.Combine("INT1_MapCSS.mapcss"));
var engine = new MapCssStyleEngine(css);

// Harbour seamark
var harbour = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "harbour",
        ["seamark:name"] = "Port Alpha"
    });

var harbourResult = engine.Evaluate(new MapCssQuery(harbour, zoom: 15));
var harbourIcon = harbourResult.Layers["int1_harbour"].Properties["icon-image"][0];

// Fishing harbour category (uses set harbour -> class for the follow-up rule)
var fishingHarbour = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "harbour",
        ["seamark:harbour:category"] = "fishing"
    });

var fishingResult = engine.Evaluate(new MapCssQuery(fishingHarbour));
var fishingIcon = fishingResult.Layers["int1_harbour"].Properties["icon-image"][0];

// Mooring seamark (non-buoy)
var mooring = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "mooring",
        ["seamark:mooring:category"] = "pile"
    });

var mooringResult = engine.Evaluate(new MapCssQuery(mooring));
var mooringLayer = mooringResult.Layers["int1_mooring"];
var mooringShape = mooringLayer.Properties["symbol-shape"][0];
var mooringSize = mooringLayer.Properties["symbol-size"][0];

// Landmark seamark (church)
var landmark = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "landmark",
        ["seamark:landmark:function"] = "church"
    });

var landmarkResult = engine.Evaluate(new MapCssQuery(landmark));
var landmarkIcon = landmarkResult.Layers["int1_landmark"].Properties["icon-image"][0];
```

## Parent/Child (combinators) and Link Tags

Child and descendant combinators use the `MapCssContext` chain. For member link filters like
`relation >[role=inner] way`, pass `linkTags` on the child context.

```csharp
var css = @"
relation[type=multipolygon] >[role=inner] way { width: 2; }
";

var relation = new MapCssElement(
    MapCssElementType.Relation,
    new Dictionary<string, string> { ["type"] = "multipolygon" });

var way = new MapCssElement(
    MapCssElementType.Way,
    new Dictionary<string, string>());

var relationCtx = new MapCssContext(relation);
var wayCtx = new MapCssContext(
    way,
    parent: relationCtx,
    linkTags: new Dictionary<string, string> { ["role"] = "inner" });

var engine = new MapCssStyleEngine(css);
var result = engine.Evaluate(new MapCssQuery(wayCtx));
var width = result.Layers[""].Properties["width"][0];
```

## Classes, Pseudo-classes, and `set`

Classes and pseudos can be provided on `MapCssElement`. The `set` declaration adds classes that
are visible to later rules during evaluation.

```csharp
var css = @"
way[boundary=administrative][admin_level=2] { set border; }
*.border::int1_border { color: yellow; width: 3; }
";

var element = new MapCssElement(
    MapCssElementType.Way,
    new Dictionary<string, string>
    {
        ["boundary"] = "administrative",
        ["admin_level"] = "2"
    });

var engine = new MapCssStyleEngine(css);
var result = engine.Evaluate(new MapCssQuery(element));

// "border" is added via `set`
var classes = result.Classes; // contains "border"
var borderColor = result.Layers["int1_border"].Properties["color"][0];
```

## Expression Evaluation in Property Values

The styling engine attempts to evaluate a subset of MapCSS expressions in property values.
Supported functions include: `tag`, `concat`, `any`, `split`, `get`, `count`, `cond`, `eval`,
and `has_tag_key`. If evaluation fails, the original token text is kept.

```csharp
var css = @"
node {
    text: concat(""Depth: "", tag(""depth""));
    text-color: cond(tag(""depth"") > 10, ""red"", ""black"");
}
";

var element = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string> { ["depth"] = "12" });

var engine = new MapCssStyleEngine(css);
var result = engine.Evaluate(new MapCssQuery(element));

var text = result.Layers[""].Properties["text"][0];       // "Depth: 12"
var color = result.Layers[""].Properties["text-color"][0]; // "red"
```

The goal over time is to fully support the expression evaluation semantics in MapCss. See [ROADMAP.md](ROADMAP.md) for details. 

## Generating the Parser

The parser is generated from `Lexer.g4` and `Parser.g4` using ANTLR.
Run the generator script from the repo root:

```powershell
pwsh -File tools\generate-parser.ps1
```

Notes:
- Requires Java on PATH.
- Downloads the ANTLR jar into `tools/tmp/antlr/4.13.2` if missing.
- Outputs C# sources to `src/MapCss.Parser/Generated` (use `-Clean` to wipe first).

## Test Coverage Report

Coverage is generated via the test project with the XPlat collector and ReportGenerator.
From the repo root:

```powershell
dotnet tool restore
pwsh -File tools\run-coverage.ps1
```

The HTML report is written to `tools/tmp/coverage-report/index.html`. The script filters out generated
parser sources so coverage focuses on handwritten code.

## Notes

- The parser and selector matcher aim to be compatible with common JOSM MapCSS constructs.
- The engine is deterministic and does not load external resources (icons, images, etc.).
