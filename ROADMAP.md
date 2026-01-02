# Roadmap

This roadmap outlines planned work for the parser and styling engine. It is organized by phases,
with each phase listing goals, key tasks, and expected outcomes. Dates are intentionally omitted
so the plan can be prioritized by impact and effort.

## North Star

Provide a robust, production-ready MapCSS parser and styling engine with predictable evaluation,
good performance, and clear extension points. The primary consumer entry point remains the `MapCssStyleEngine` class.

## Phase 1: Stabilize Core API and Behavior

Goal: make the public API stable and the current behavior consistent and documented.

- Lock the public surface area (types and method names) and document versioning expectations.
- Improve error messages for lexing/parsing errors to include rule name and surrounding text.
- Add a public `TryEvaluate` or structured diagnostics output for non-fatal parse failures.

## Phase 2: Expression Engine Completeness

Goal: bring expression evaluation in line with the grammar.

- Replace the hand-rolled evaluator with an AST-based evaluator driven by the ANTLR `expr` tree.
- Implement operator precedence and associativity for:
  - `||`, `&&`, `==`, `!=`, `=`, `<`, `<=`, `>`, `>=`,
  - `+`, `-`, `*`, `/`, `%`,
  - unary `!`, `+`, `-`,
  - ternary `a ? b : c`.
- Introduce a typed value model (string, number, boolean, color, regex, list).
- Expand function support with a structured registry:
  - string helpers, numeric helpers, tag lookups,
  - array/list helpers beyond `split`, `get`, `count`.
- Ensure evaluation works for both property values and selector pseudo-classes.

Deliverables:
- Expression visitor implementation, unit tests aligned with grammar.
- Backward compatibility for existing property value behaviors.

## Phase 3: Selector and Matching Semantics

Goal: improve correctness and MapCSS compatibility for selector matching.

- Implement selector specificity and conflict resolution order.
- Confirm and document link filter semantics and multi-segment chains.
- Support additional pseudo-classes used by JOSM MapCSS (if present in samples).
- Optional: add a compatibility mode for JOSM quirks (e.g., equality rules).

Deliverables:
- Specificity tests with expected overrides.
- Expanded sample coverage with real MapCSS files.

## Phase 4: Typed Style Output

Goal: provide richer output for consumers.

- Add a typed style model and optional parsing helpers:
  - numeric values with units,
  - colors as RGBA,
  - boolean values,
  - URI values and local resource references.
- Provide a typed view on top of `MapCssValue.Text` without breaking existing API.
- Add a `StyleSnapshot` result that includes ordered rules and evaluation context.

Deliverables:
- New typed value helpers and opt-in conversion APIs.
- Clear migration path (no breaking change required).

## Phase 5: Performance and Scale

Goal: handle large stylesheets and frequent queries efficiently.

- Cache parsed ASTs and compiled selectors.
- Optimize selector matching by pre-indexing rules by element type, class, and tag keys.
- Add optional memoization for repeated queries (by key set + element type + zoom).
- Add microbenchmarks and performance regression tests.

Deliverables:
- Baseline performance suite.
- Documented tuning knobs for consumers.

## Phase 6: Packaging, Tooling, and Docs

Goal: make the project easy to consume and contribute to.

- Add contribution guidelines and a release checklist.
- Provide a small CLI tool to evaluate a stylesheet against a JSON input.
- Automate code coverage reporting for pull requests

Deliverables:
- CLI project, documentation, and release process.
