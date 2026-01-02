lexer grammar MapCssLexer;

// ---------- Keywords (must come before IDENT) ----------
META        : 'meta';
SET         : 'set';
NODE        : 'node';
WAY         : 'way';
RELATION    : 'relation';
AREA        : 'area';
CANVAS      : 'canvas';
TRUE        : 'true';
FALSE       : 'false';

// ---------- Zoom token (must come before IDENT) ----------
// Matches: z15, z15-, z10-14
ZOOMRANGE
    : 'z' [0-9]+ ( '-' [0-9]* )?
    ;

// ---------- Attribute operators / comparisons (longer first) ----------
// Regex operators used by JOSM MapCSS: key=~/.../ and key!~/.../
RE_NMATCH   : '!~';
RE_MATCH    : '=~';

// Substring/prefix/suffix match operators: key*=x, key^=x, key$=x
CONTAINS    : '*=';
PREFIX      : '^=';
SUFFIX      : '$=';

// Legacy-ish variants sometimes seen in MapCSS:
NMATCH      : '!~=';
MATCH       : '~=';

// Comparisons / booleans
NEQ         : '!=';
EQEQ        : '==';
GTE         : '>=';
LTE         : '<=';
OR          : '||';
AND         : '&&';

// Selector subpart token must come before COLON
DCOLON      : '::';

// ---------- Ternary / existence ----------
QMARK       : '?';

// ---------- Literals ----------

// HTML/CSS color formats: #RGB, #RGBA, #RRGGBB, #RRGGBBAA
HEXCOLOR
    : '#' HEX3
    | '#' HEX4
    | '#' HEX6
    | '#' HEX8
    ;

fragment HEX3 : HEXDIG HEXDIG HEXDIG;
fragment HEX4 : HEXDIG HEXDIG HEXDIG HEXDIG;
fragment HEX6 : HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG;
fragment HEX8 : HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG HEXDIG;
fragment HEXDIG : [0-9a-fA-F];

// One numeric token handles both ints and decimals
NUMBER
    : [0-9]+ ('.' [0-9]+)?
    ;

// Double-quoted string
DSTRING
    : '"' ( '\\' . | ~["\\\r\n] )* '"'
    ;

// Single-quoted string
SSTRING
    : '\'' ( '\\' . | ~['\\\r\n] )* '\''
    ;

LINE_COMMENT
    : '//' ~[\r\n]* -> skip
    ;

BLOCK_COMMENT
    : '/*' .*? '*/' -> skip
    ;

// Regex literal /.../ (used with =~ and !~)
// Put before SLASH so it wins when a closing / exists.
REGEX
    : '/' ( '\\/' | '\\\\' . | ~[/\r\n] )* '/'
    ;

// ---------- Punctuation ----------
LBRACE      : '{';
RBRACE      : '}';
LBRACK      : '[';
RBRACK      : ']';
LPAREN      : '(';
RPAREN      : ')';
SEMI        : ';';
COLON       : ':';
COMMA       : ',';
DOT         : '.';
PIPE        : '|';

// ---------- Single-char operators ----------
EQ          : '=';
LT          : '<';
GT          : '>';
PLUS        : '+';
MINUS       : '-';
STAR        : '*';
SLASH       : '/';
PERCENT     : '%';
BANG        : '!';

// ---------- Identifiers ----------
// Hyphens are handled at the parser level as IDENT (MINUS IDENT)* where needed.
IDENT
    : [a-zA-Z_] [a-zA-Z0-9_]*
    ;

// ---------- Whitespace + comments ----------
WS
    : [ \t\r\n]+ -> skip
    ;

// Handle UTF-8 BOM if present at start of file
BOM
    : '\uFEFF' -> skip
    ;
