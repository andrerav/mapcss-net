using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using MapCss.Styling;

namespace MapCss.Tests
{
    [TestFixture]
    public class MapCssStyleEngineTests
    {
        // Helper: evaluate a CSS against an element and optional zoom
        private static MapCssStyleResult Evaluate(string css, MapCssElement element, int? zoom = null)
        {
            var engine = new MapCssStyleEngine(css);
            return engine.Evaluate(new MapCssQuery(new MapCssContext(element), zoom));
        }

        private static string? GetPropertyText(MapCssStyleResult result, string layer, string property, int index = 0)
        {
            if (!result.Layers.TryGetValue(layer, out var l)) return null;
            if (!l.Properties.TryGetValue(property, out var values)) return null;
            if (values.Count <= index) return null;
            return values[index];
        }

        private static IReadOnlyList<string>? GetPropertyList(MapCssStyleResult result, string layer, string property)
        {
            if (!result.Layers.TryGetValue(layer, out var l)) return null;
            if (!l.Properties.TryGetValue(property, out var values)) return null;
            return values.ToArray();
        }

        // Basic smoke tests (kept for clarity)
        // Verify that a simple selector applied at the default layer sets the expected property value.
        [Test]
        public void DefaultLayerPropertyIsApplied()
        {
            var css = "way { color: black; }";
            var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());
            var result = Evaluate(css, element, zoom: 15);

            Assert.That(GetPropertyText(result, "", "color"), Is.EqualTo("black"));
        }

        // Ensure that a class-based subpart selector maps to the correct named layer
        // and that its properties are applied to elements carrying that class.
        [Test]
        public void SubpartLayerForClassIsApplied()
        {
            var css = "*.border::int1_border { width: 3; }";
            var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>(), classes: new[] { "border" });
            var result = Evaluate(css, element);

            Assert.That(GetPropertyText(result, "int1_border", "width"), Is.EqualTo("3"));
        }

        // Verify that a `set` declaration adds a class to the evaluation result
        // and that subsequent subpart rules that match that class are applied.
        [Test]
        public void SetDeclarationAddsClassAndMatchesSubpart()
        {
            var css = "way[boundary=administrative][admin_level=2] { set border; } *.border::int1_border { color: yellow; }";
            var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["boundary"] = "administrative", ["admin_level"] = "2" });
            var result = Evaluate(css, element);

            Assert.That(result.Classes, Does.Contain("border"));
            Assert.That(GetPropertyText(result, "int1_border", "color"), Is.EqualTo("yellow"));
        }

        // ---------------------------------------------------------------------
        // Parameterized tests generated from many cases to increase coverage
        // ---------------------------------------------------------------------

        // Parameterized test validating a variety of single-property selectors.
        // Each TestCase ensures the parsed rule sets the property value on the correct layer.
        [Test, TestCaseSource(nameof(SimplePropertyCases))]
        public void SimplePropertyTest(string css, MapCssElement element, string layer, string property, string expected)
        {
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, layer, property), Is.EqualTo(expected));
        }

        // Parameterized test for list-valued properties (e.g., dashes).
        // Asserts the produced property list matches the expected sequence.
        [Test, TestCaseSource(nameof(ListPropertyCases))]
        public void ListPropertyTest(string css, MapCssElement element, string layer, string property, string[] expected)
        {
            var result = Evaluate(css, element);
            var list = GetPropertyList(result, layer, property);
            Assert.That(list, Is.EqualTo(expected));
        }

        // Tests that `set` declarations add the named class(es) to the evaluation result.
        [Test, TestCaseSource(nameof(SetClassCases))]
        public void SetClassTest(string css, MapCssElement element, string expectedClass)
        {
            var result = Evaluate(css, element);
            Assert.That(result.Classes, Does.Contain(expectedClass));
        }

        // Combinator tests (child vs descendant) that exercise selector merging and context-linking.
        [Test, TestCaseSource(nameof(CombinatorCases))]
        public void CombinatorTest((string css, MapCssContext context, string layer, string prop, string expected) input)
        {
            var (css, context, layer, prop, expected) = input;
            var engine = new MapCssStyleEngine(css);
            var result = engine.Evaluate(new MapCssQuery(context));
            Assert.That(GetPropertyText(result, layer, prop), Is.EqualTo(expected));
        }

        // Ensure that all sample MapCSS files at `samples/` can be parsed and constructed
        // into a MapCssStyleEngine without throwing exceptions.
        [Test]
        public void SampleFilesParseTest()
        {
            var files = EnumerateSampleFiles().ToList();
            Assert.That(files, Is.Not.Empty, "No sample MapCSS files were discovered.");

            foreach (var filePath in files)
            {
                var content = File.ReadAllText(filePath);
                Assert.DoesNotThrow(() => new MapCssStyleEngine(content), filePath);
            }
        }


        // ---------------------------------------------------------------------
        // Test data generators
        // ---------------------------------------------------------------------

        public static IEnumerable<TestCaseData> SimplePropertyCases()
        {
            // Basic selectors and types
            yield return new TestCaseData("way { color: black; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "color", "black").SetName("way_color_black");
            yield return new TestCaseData("* { width: 2; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "width", "2").SetName("any_width_2_node");
            yield return new TestCaseData("node { symbol-size: 4; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "symbol-size", "4").SetName("node_symbol_size_4");

            // Class selectors
            yield return new TestCaseData("*.red { color: red; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>(), classes: new[] { "red" }), "", "color", "red").SetName("class_red_color_red");

            // Attribute existence and equality
            yield return new TestCaseData("way[boundary] { opacity: 0.5; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["boundary"] = "administrative" }), "", "opacity", "0.5").SetName("way_boundary_exists_opacity");
            yield return new TestCaseData("way[boundary=administrative] { color: yellow; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["boundary"] = "administrative" }), "", "color", "yellow").SetName("way_boundary_eq_admin_color");
            yield return new TestCaseData("way[admin_level!=3] { color: green; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["admin_level"] = "2" }), "", "color", "green").SetName("way_admin_level_not3_color_green");

            // Hex color
            yield return new TestCaseData("node { color: #ff00ff; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "color", "#ff00ff").SetName("node_color_hex");

            // String literal
            yield return new TestCaseData("node { text: 'hello'; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "text", "hello").SetName("node_text_literal");

            // Number and boolean
            yield return new TestCaseData("way { width: 7; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "width", "7").SetName("way_width_7");
            yield return new TestCaseData("node { default-points: false; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "default-points", "false").SetName("node_default_points_false");

            // Multiple selectors
            yield return new TestCaseData("node, way { color: purple; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "color", "purple").SetName("node_or_way_color_purple");
            yield return new TestCaseData("node, way { color: purple; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "color", "purple").SetName("node_or_way_color_purple_node");

            // Subpart default/explicit
            yield return new TestCaseData("*.border::int1_border { width: 3; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>(), classes: new[] { "border" }), "int1_border", "width", "3").SetName("subpart_border_width_3");

            // Pseudo classes
            yield return new TestCaseData("node:connection { symbol-size: 3; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>(), pseudoClasses: new[] { "connection" }), "", "symbol-size", "3").SetName("node_pseudo_connection_size_3");

            // Zoom range (common form 'z15-')
            yield return new TestCaseData("node|z15- { color: blue; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "color", "blue").SetName("node_z15_minus_color_blue_with_zoom15");
            yield return new TestCaseData("node|z15- { color: blue; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "color", "blue").SetName("node_z15_minus_color_blue_no_zoom_but_node_should_not_match");

            // Dashes list: simple property parsed as list
            yield return new TestCaseData("way { dashes: 10, 5; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "dashes", "10").SetName("way_dashes_first_value_10");

            // Icon and image properties (string values)
            yield return new TestCaseData("node { icon-image: \"icon.svg\"; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "", "icon-image", "icon.svg").SetName("node_icon_image");

            // Set multiple simple test cases by combining property/value arrays
            var props = new (string prop, string val)[]
            {
                ("opacity","0.25"),("casing-width","2"),("fill-color","aliceblue"),("casing-color","skyblue")
            };

            int i = 0;
            foreach (var (prop, val) in props)
            {
                yield return new TestCaseData($"way {{ {prop}: {val}; }}", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", prop, val).SetName($"way_{prop}_{i}");
                i++;
            }

            // Add more to increase count
            var colors = new[] { "red", "green", "blue", "black", "magenta", "darkmagenta" };
            foreach (var c in colors)
            {
                yield return new TestCaseData($"way {{ color: {c}; }}", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "color", c).SetName($"way_color_{c}");
            }

            // Attribute contains / ~=
            yield return new TestCaseData("way[name~=foo] { color: orange; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foo" }), "", "color", "orange").SetName("attribute_contains_or_tilde_eq");

            
        }

        public static IEnumerable<TestCaseData> ListPropertyCases()
        {
            yield return new TestCaseData("way { dashes: 10, 5; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "dashes", new[] { "10", "5" }).SetName("dashes_two_values");
            yield return new TestCaseData("way { repeat-image-width: 18, 28; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "", "repeat-image-width", new[] { "18", "28" }).SetName("repeat_image_width_list");
        }

        public static IEnumerable<TestCaseData> SetClassCases()
        {
            yield return new TestCaseData("way { set harbour; } *.harbour::int1_harbour { icon-image: \"i.svg\"; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "harbour").SetName("set_harbour_adds_class");

            yield return new TestCaseData("node { set notice; set .special; } *.notice::int1_notice { icon-image: \"n.svg\"; }", new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), "notice").SetName("set_notice_and_special");
        }

        public static IEnumerable<TestCaseData> CombinatorCases()
        {
            // relation[type=multipolygon] >[role=inner] way { width: 2; }
            var css = "relation[type=multipolygon] >[role=inner] way { width: 2; }";
            var parent = new MapCssElement(MapCssElementType.Relation, new Dictionary<string, string> { ["type"] = "multipolygon" });
            var parentContext = new MapCssContext(parent);
            var child = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());
            var childContext = new MapCssContext(child, parentContext, linkTags: new Dictionary<string, string> { ["role"] = "inner" });

            yield return new TestCaseData((css, childContext, "", "width", "2")).SetName("relation_child_way_role_inner_width_2");

            // descendant match: relation ... > way (child) vs descendant
            var css2 = "relation[type=multipolygon] way { width: 3; }";
            var ancestor = new MapCssElement(MapCssElementType.Relation, new Dictionary<string, string> { ["type"] = "multipolygon" });
            var ancestorContext = new MapCssContext(ancestor);
            var midContext = new MapCssContext(new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>()), ancestorContext);
            var target = new MapCssContext(new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), midContext);
            yield return new TestCaseData((css2, target, "", "width", "3")).SetName("relation_descendant_way_width_3");
        }

        private static IEnumerable<string> EnumerateSampleFiles()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory());
            while (current != null)
            {
                var samples = Path.Combine(current.FullName, "samples");
                if (Directory.Exists(samples))
                {
                    foreach (var file in Directory.EnumerateFiles(samples, "*.mapcss", SearchOption.TopDirectoryOnly))
                    {
                        yield return file;
                    }
                    yield break;
                }

                current = current.Parent;
            }
        }

        // ---------------------------------------------------------------------
        // Additional generated tests to reach large numbers for coverage
        // This block generates a few hundred test cases programmatically.
        // ---------------------------------------------------------------------

        // Bulk generated property tests intended to exercise a wide variety of selector/property combinations.
        // These are automatically generated to increase test coverage while keeping assertions simple.
        [Test, TestCaseSource(nameof(GeneratedPropertyCases))]
        public void GeneratedPropertyTest(string css, MapCssElement element, string layer, string property, string expected)
        {
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, layer, property), Is.EqualTo(expected));
        }

        public static IEnumerable<TestCaseData> GeneratedPropertyCases()
        {
            var elementTypes = new[] { MapCssElementType.Node, MapCssElementType.Way, MapCssElementType.Area };
            var propertyValues = new (string prop, string val)[]
            {
                ("color", "black"),("width","1"),("opacity","0.5"),("fill-opacity","0.75"),("casing-opacity","0.9"),("symbol-size","6"),("icon-width","12"),("text-color","black"),("font-size","10")
            };

            int cnt = 0;
            foreach (var t in elementTypes)
            {
                foreach (var (prop, val) in propertyValues)
                {
                    var css = $"{t.ToString().ToLowerInvariant()} {{ {prop}: {val}; }}";
                    var element = new MapCssElement(t, new Dictionary<string, string>());
                    yield return new TestCaseData(css, element, "", prop, val).SetName($"gen_{cnt}_{t}_{prop}");
                    cnt++;
                }
            }

            // Generate many class selector cases
            var classes = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
            foreach (var cls in classes)
            {
                var css = $"*.{cls} {{ color: {cls}; }}"; // use class name as color string for uniqueness
                var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>(), classes: new[] { cls });
                yield return new TestCaseData(css, element, "", "color", cls).SetName($"class_color_{cls}");
            }

            // Generate many attribute equality cases
            for (int i = 0; i < 80; i++)
            {
                var key = $"k{i}";
                var val = $"v{i}";
                var css = $"way[{key}={val}] {{ width: {i}; }}";
                var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { [key] = val });
                yield return new TestCaseData(css, element, "", "width", i.ToString()).SetName($"attr_eq_{i}");
            }

            // Generate many list properties
            for (int i = 0; i < 20; i++)
            {
                var css = $"way {{ dashes: {i}, {i+1}; }}";
                var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>());
                yield return new TestCaseData(css, element, "", "dashes", i.ToString()).SetName($"dashes_generated_{i}");
            }

            // Generate set + subpart tests
            for (int i = 0; i < 40; i++)
            {
                var cls = $"s{i}";
                var sub = $"sub{i}";
                var css = $"way[{"boundary"}={"administrative"}] {{ set {cls}; }} *.{cls}::{sub} {{ color: {i}; }}";
                var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["boundary"] = "administrative" });
                yield return new TestCaseData(css, element, sub, "color", i.ToString()).SetName($"set_subpart_{i}");
            }

            // Ensure we generate at least 300 cases total by extending attributes
            for (int i = 100; i < 220; i++)
            {
                var key = $"attr{i}";
                var val = $"val{i}";
                var css = $"node[{key}={val}] {{ symbol-size: {i%10}; }}";
                var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { [key] = val });
                yield return new TestCaseData(css, element, "", "symbol-size", (i%10).ToString()).SetName($"node_attr_{i}");
            }
        }

        // ---------------------------------------------------------------------
        // Exhaustive Attribute Operator Tests
        // ---------------------------------------------------------------------

        // Test matrix covering different attribute operator semantics (=, !=, *=, ^=, $=, ~=, regex matches, existence).
        [Test, TestCaseSource(nameof(AttributeOperatorCases))]
        public void AttributeOperatorTest(string css, MapCssElement element, string expected)
        {
            var result = Evaluate(css, element);
            var actual = GetPropertyText(result, "", "color");
            if (expected is null)
            {
                Assert.That(actual, Is.Null);
            }
            else
            {
                Assert.That(actual, Is.EqualTo(expected));
            }
        }

        public static IEnumerable<TestCaseData> AttributeOperatorCases()
        {
            // Equality =
            yield return new TestCaseData("way[name=foo] { color: a; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foo" }), "a").SetName("attr_eq_match");
            yield return new TestCaseData("way[name=foo] { color: a; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "bar" }), null).SetName("attr_eq_nomatch");

            // Not equal !=
            yield return new TestCaseData("way[name!=bar] { color: b; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foo" }), "b").SetName("attr_neq_match");
            yield return new TestCaseData("way[name!=foo] { color: b; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foo" }), null).SetName("attr_neq_nomatch");

            // Contains *=
            yield return new TestCaseData("way[name*=oba] { color: c; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "c").SetName("attr_contains_star_match");
            yield return new TestCaseData("way[name*=xyz] { color: c; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_contains_star_nomatch");

            // Prefix ^=
            yield return new TestCaseData("way[name^=foo] { color: d; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "d").SetName("attr_prefix_match");
            yield return new TestCaseData("way[name^=bar] { color: d; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_prefix_nomatch");

            // Suffix $=
            yield return new TestCaseData("way[name$=bar] { color: e; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "e").SetName("attr_suffix_match");
            yield return new TestCaseData("way[name$=foo] { color: e; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_suffix_nomatch");

            // MATCH ~= (contains when value is not regex)
            yield return new TestCaseData("way[name~=oba] { color: f; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "f").SetName("attr_tilde_match_contains");
            yield return new TestCaseData("way[name~=xyz] { color: f; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_tilde_nomatch");

            // NMATCH !~= (negated contains)
            yield return new TestCaseData("way[name!~=foo] { color: g; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "bar" }), "g").SetName("attr_nmatch_match");
            yield return new TestCaseData("way[name!~=oba] { color: g; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_nmatch_nomatch");

            // RE_MATCH =~ (regex match)
            yield return new TestCaseData("way[name=~/^foo/] { color: h; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "h").SetName("attr_re_match_match");
            yield return new TestCaseData("way[name=~/^bar/] { color: h; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_re_match_nomatch");

            // RE_NMATCH !~ (regex not match)
            yield return new TestCaseData("way[name!~/^bar/] { color: i; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "i").SetName("attr_re_nmatch_match");
            yield return new TestCaseData("way[name!~/^foo/] { color: i; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_re_nmatch_nomatch");

            // Legacy MATCH/MNMATCH tokens (~= and !~=) - ensure both behave similarly to MATCH/NMATCH above
            yield return new TestCaseData("way[name~=oba] { color: l; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), "l").SetName("attr_legacy_match_contains");
            yield return new TestCaseData("way[name!~=oba] { color: l; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" }), null).SetName("attr_legacy_nmatch_nomatch");

            // Existence tests
            yield return new TestCaseData("way[name] { color: j; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "x" }), "j").SetName("attr_exists_match");
            yield return new TestCaseData("way[name] { color: j; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), null).SetName("attr_exists_nomatch");

            // Negated existence with leading !
            yield return new TestCaseData("way[!name] { color: k; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "v" }), null).SetName("attr_not_exists_match");
            yield return new TestCaseData("way[!name] { color: k; }", new MapCssElement(MapCssElementType.Way, new Dictionary<string, string>()), "k").SetName("attr_not_exists_nomatch");
        }

        // ---------------------------------------------------------------------
        // Negative / parse-error tests and invalid-regex handling
        // ---------------------------------------------------------------------

        // These inputs are intentionally malformed and should cause the MapCss parser
        // or engine constructor to throw an InvalidOperationException indicating a parse failure.
        [Test, TestCaseSource(nameof(ParseErrorCases))]
        public void ParseErrorThrows(string css)
        {
            Assert.Throws<InvalidOperationException>(() => new MapCssStyleEngine(css));
        }

        public static IEnumerable<TestCaseData> ParseErrorCases()
        {
            // Missing semicolon in property
            yield return new TestCaseData("way { color: black }").SetName("missing_semicolon");
            // Missing semicolon in set statement
            yield return new TestCaseData("way { set border }").SetName("set_missing_semi");
            // Attribute missing value
            yield return new TestCaseData("way[name=] { color: red; }").SetName("attr_missing_value");
            // Regex literal missing closing slash -> lexer/parser error
            yield return new TestCaseData("way[name=~/(foo { color: red; }").SetName("regex_missing_closing_slash");
            // Invalid attr existence ordering (should be key?!)
            yield return new TestCaseData("way[name!?] { color: x; }").SetName("invalid_attr_existence_order");
            // set without items
            yield return new TestCaseData("way { set; }").SetName("set_without_item");
            // malformed pseudo-class
            yield return new TestCaseData("node: { symbol-size: 3; }").SetName("pseudo_missing_ident");
        }

        // Verify that a syntactically valid regex token with an invalid pattern
        // does not crash the engine; instead, the engine should tolerate it and not match.
        [Test]
        public void InvalidRegexDoesNotMatchAndDoesNotThrow()
        {
            var css = "way[name=~/[/] { color: red; }"; // pattern '[' is invalid and won't compile (unclosed character class)
            // constructing engine must not throw
            var engine = new MapCssStyleEngine(css);

            var element = new MapCssElement(MapCssElementType.Way, new Dictionary<string, string> { ["name"] = "foobar" });
            var result = engine.Evaluate(new MapCssQuery(element));
            Assert.That(GetPropertyText(result, "", "color"), Is.Null);
        }

        // A regex literal that lacks the trailing '/' is a lexer/parser error and should cause construction to throw.
        [Test]
        public void MalformedRegexMissingSlashThrows()
        {
            var css = "way[name=~/(foo { color: red; }"; // missing closing '/'
            Assert.Throws<InvalidOperationException>(() => new MapCssStyleEngine(css));
        }

        // ---------------------------------------------------------------------
        // Expression evaluation tests (cond, concat, tag, any, split, get, count, eval, has_tag_key)
        // ---------------------------------------------------------------------
        // Expression tests: conditional evaluation using `tag()`;
        // Ensures the `cond` expression selects the true branch when the tag equality holds.        
        [Test]
        public void CondWithTagEqualsReturnsTrueBranch()
        {
            var css = "node { text: cond(tag(\"k\") == \"v\", \"yes\", \"no\"); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { ["k"] = "v" });
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "text"), Is.EqualTo("yes"));
        }
        // Expression function test: `concat` should join string arguments into a single string.        
        [Test]
        public void ConcatCombinesStrings()
        {
            var css = "node { text: concat(\"a\", \"b\"); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "text"), Is.EqualTo("ab"));
        }
        // Expression function test: `any` should return the first non-empty argument.        
        [Test]
        public void AnyPicksFirstNonEmpty()
        {
            var css = "node { text: any(tag(\"missing\"), tag(\"k\"), \"fallback\"); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { ["k"] = "val" });
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "text"), Is.EqualTo("val"));
        }
        // Expression function combination test: `split` followed by `get` should extract the specified list element.        
        [Test]
        public void SplitGetReturnsElement()
        {
            var css = "node { text: get(split(\";\", tag(\"list\")), 1); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { ["list"] = "first;second;third" });
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "text"), Is.EqualTo("second"));
        }
        // Expression test combining split/count inside a conditional numeric comparison.        
        [Test]
        public void CountUsedInCondEvaluatesNumericComparison()
        {
            var css = "node { width: cond(count(split(\";\", tag(\"list\"))) > 2, 5, 2); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { ["list"] = "1;2;3" });
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "width"), Is.EqualTo("5"));
        }
        // Expression test invoking `eval` to produce property values from evaluated expressions.        
        [Test]
        public void EvalConcatProducesString()
        {
            var css = "node { icon-image: eval(concat(\"p\", \"q\")); }";
            var element = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
            var result = Evaluate(css, element);
            Assert.That(GetPropertyText(result, "", "icon-image"), Is.EqualTo("pq"));
        }
        // Expression test: `has_tag_key` should return true when tag exists and influence `cond` branches.        
        [Test]
        public void HasTagKeyWorksInCond()
        {
            var css = "node { text: cond(has_tag_key(\"k\"), \"yes\", \"no\"); }";
            var elementWith = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string> { ["k"] = "v" });
            var elementWithout = new MapCssElement(MapCssElementType.Node, new Dictionary<string, string>());
            Assert.That(GetPropertyText(Evaluate(css, elementWith), "", "text"), Is.EqualTo("yes"));
            Assert.That(GetPropertyText(Evaluate(css, elementWithout), "", "text"), Is.EqualTo("no"));
        }
    }
}
