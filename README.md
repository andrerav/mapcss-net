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
var color = result.Layers[""].Properties["color"][0].Text;
var width = result.Layers[""].Properties["width"][0].Text;
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
var riverColor = result.Layers[""].Properties["color"][0].Text;

// Subpart layer
var outlineLayer = result.Layers["outline"];
var casingColor = outlineLayer.Properties["casing-color"][0].Text;
```

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
var harbourIcon = harbourResult.Layers["int1_harbour"].Properties["icon-image"][0].Text;

// Fishing harbour category (uses set harbour -> class for the follow-up rule)
var fishingHarbour = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "harbour",
        ["seamark:harbour:category"] = "fishing"
    });

var fishingResult = engine.Evaluate(new MapCssQuery(fishingHarbour));
var fishingIcon = fishingResult.Layers["int1_harbour"].Properties["icon-image"][0].Text;

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
var mooringShape = mooringLayer.Properties["symbol-shape"][0].Text;
var mooringSize = mooringLayer.Properties["symbol-size"][0].Text;

// Landmark seamark (church)
var landmark = new MapCssElement(
    MapCssElementType.Node,
    new Dictionary<string, string>
    {
        ["seamark:type"] = "landmark",
        ["seamark:landmark:function"] = "church"
    });

var landmarkResult = engine.Evaluate(new MapCssQuery(landmark));
var landmarkIcon = landmarkResult.Layers["int1_landmark"].Properties["icon-image"][0].Text;
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
var width = result.Layers[""].Properties["width"][0].Text;
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
var borderColor = result.Layers["int1_border"].Properties["color"][0].Text;
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

var text = result.Layers[""].Properties["text"][0].Text;       // "Depth: 12"
var color = result.Layers[""].Properties["text-color"][0].Text; // "red"
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
- Property values are evaluated to strings by the expression evaluator; if you need typed values,
  you can parse `MapCssValue.Text` in your application.
- The engine is deterministic and does not load external resources (icons, images, etc.).
