parser grammar MapCssParser;

options { tokenVocab=MapCssLexer; }

// -------------------- Top level --------------------

stylesheet
    : statement* EOF
    ;

statement
    : metaBlock
    | ruleSet
    ;

metaBlock
    : META block
    ;

ruleSet
    : selectorGroup block
    ;

selectorGroup
    : selector (COMMA selector)*
    ;

selector
    : selectorChain
    ;

// Supports:
//   A > B
//   A B                (implicit descendant via adjacency)
//   A >[role=inner] way (member/link selectors)
selectorChain
    : simpleSelector ( (combinator simpleSelector) | simpleSelector )*
    ;

combinator
    : GT
    ;

// Non-empty selector segment.
// Either: element + atoms  OR just atoms (e.g. [role=inner], .class, ::subpart)
simpleSelector
    : element selectorAtom*
    | selectorAtom+
    ;

// Atoms may appear in any order, repeated.
selectorAtom
    : zoomSpec
    | selectorSuffix
    | attributeFilter
    ;

// JOSM element selectors
element
    : NODE
    | WAY
    | RELATION
    | AREA
    | CANVAS
    | STAR          // wildcard selector *
    ;

// Zoom filter: |z15-, |z10-14
zoomSpec
    : PIPE ZOOMRANGE
    ;

// .class, ::subpart, :pseudo(...)
selectorSuffix
    : pseudoClass
    | classSelector
    | subPart
    ;

pseudoClass
    : COLON IDENT (LPAREN exprList? RPAREN)?
    ;

classSelector
    : DOT IDENT
    ;

subPart
    : DCOLON IDENT
    ;

// -------------------- Attribute filters --------------------

attributeFilter
    : LBRACK attributeTest RBRACK
    ;

// Supports:
//   [key] [!key]
//   [key?] [key?!]
//   [key=value] [key!=value] [key~=value] [key!~=value]
//   [key=~/.../] [key!~/.../]
//   [key*=value] [key^=value] [key$=value]
//   ["seamark:type"=foo] [!"seamark:type"]
attributeTest
    : BANG? attrKey attrExistence? (attrOp attrValue)?
    ;

// Existence/boolean tests: key?  and key?!
attrExistence
    : QMARK BANG?
    ;

attrKey
    : IDENT (COLON IDENT)*
    | literalString
    ;

attrOp
    : EQ
    | NEQ
    | MATCH
    | NMATCH
    | RE_MATCH
    | RE_NMATCH
    | CONTAINS
    | PREFIX
    | SUFFIX
    ;

// Attribute value: string/number/color/bool, bareword (incl hyphens), or regex literal
attrValue
    : literal
    | bareWord
    | REGEX
    ;

// hyphenated barewords in attribute values: non-dangerous, calling-in_point, etc.
bareWord
    : IDENT (MINUS IDENT)*
    ;

// -------------------- Blocks / declarations --------------------

block
    : LBRACE declaration* RBRACE
    ;

declaration
    : setStatement
    | propertyDeclaration
    ;

// Allows: set foo;  set .foo;  set foo, .bar;
setStatement
    : SET setItem (COMMA setItem)* SEMI
    ;

setItem
    : DOT? IDENT
    ;

// Property values can be comma-separated lists, not just a single expr.
// e.g. dashes: 12,5;
propertyDeclaration
    : propertyName COLON valueList SEMI
    ;

valueList
    : expr (COMMA expr)*
    ;

// property-name like symbol-stroke-color
propertyName
    : IDENT (MINUS IDENT)*
    ;

// -------------------- Expressions --------------------

exprList
    : expr (COMMA expr)*
    ;

expr
    : ternaryExpr
    ;

// a ? b : c
ternaryExpr
    : logicalOrExpr (QMARK expr COLON expr)?
    ;

logicalOrExpr
    : logicalAndExpr (OR logicalAndExpr)*
    ;

logicalAndExpr
    : equalityExpr (AND equalityExpr)*
    ;

// JOSM MapCSS often uses single '=' as equality inside expressions (e.g. cond(x=0,...))
// so include EQ in addition to == and !=.
equalityExpr
    : relationalExpr ((EQ | EQEQ | NEQ) relationalExpr)*
    ;

relationalExpr
    : additiveExpr ((LT | LTE | GT | GTE) additiveExpr)*
    ;

additiveExpr
    : multiplicativeExpr ((PLUS | MINUS) multiplicativeExpr)*
    ;

multiplicativeExpr
    : unaryExpr ((STAR | SLASH | PERCENT) unaryExpr)*
    ;

unaryExpr
    : (BANG | MINUS | PLUS) unaryExpr
    | primaryExpr
    ;

primaryExpr
    : literal
    | functionCall
    | IDENT
    | LPAREN expr RPAREN
    ;

functionCall
    : IDENT LPAREN exprList? RPAREN
    ;

// -------------------- Literals --------------------

literal
    : literalString
    | NUMBER
    | HEXCOLOR
    | TRUE
    | FALSE
    ;

literalString
    : DSTRING
    | SSTRING
    ;
