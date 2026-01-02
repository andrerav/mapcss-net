[CmdletBinding()]
param(
    # Output directory for generated C# sources (relative to this script by default)
    [Parameter()]
    [string]$OutputDir = "./../src/MapCss.Parser/Generated",

    # ANTLR tool version to use
    [Parameter()]
    [string]$AntlrVersion = "4.13.2",

    # Optional C# namespace/package for generated code
    [Parameter()]
    [string]$PackageName = "MapCss.Parser",

    # If set, deletes the output directory before generating
    [Parameter()]
    [switch]$Clean,

    # If set, re-downloads the ANTLR jar even if it exists
    [Parameter()]
    [switch]$ForceDownload,

    # Generate parse-tree visitor classes
    [Parameter()]
    [switch]$Visitor = $true,

    # Generate parse-tree listener classes
    [Parameter()]
    [switch]$Listener = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Make downloads work on older Windows PowerShell where TLS 1.2 may not be default.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
} catch {
    # Ignore if not supported; Invoke-WebRequest may still work.
}

function Get-GrammarName {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [ValidateSet('lexer','parser','combined')]
        [string]$Kind
    )

    $text = Get-Content -LiteralPath $FilePath -Raw
    $pattern = "(?m)^\s*" + [Regex]::Escape($Kind) + "\s+grammar\s+([A-Za-z_][A-Za-z0-9_]*)\s*;"
    $m = [regex]::Match($text, $pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$lexerGrammar = Join-Path $scriptRoot 'Lexer.g4'
$parserGrammar = Join-Path $scriptRoot 'Parser.g4'

if (-not (Test-Path -LiteralPath $lexerGrammar)) {
    throw "Missing grammar file: $lexerGrammar"
}
if (-not (Test-Path -LiteralPath $parserGrammar)) {
    throw "Missing grammar file: $parserGrammar"
}

$java = Get-Command java -ErrorAction SilentlyContinue
if (-not $java) {
    throw "Java was not found on PATH. Install a JDK (e.g., Temurin) and ensure 'java' is available."
}

$toolsDir = Join-Path $scriptRoot './../tools'
$antlrDir = Join-Path $toolsDir (Join-Path 'antlr' $AntlrVersion)
$antlrJar = Join-Path $antlrDir "antlr-$AntlrVersion-complete.jar"

if ($ForceDownload -or -not (Test-Path -LiteralPath $antlrJar)) {
    New-Item -ItemType Directory -Path $antlrDir -Force | Out-Null

    $antlrUrl = "https://www.antlr.org/download/antlr-$AntlrVersion-complete.jar"
    Write-Host "Downloading ANTLR $AntlrVersion -> $antlrJar" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $antlrUrl -OutFile $antlrJar
}

$outPath = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $scriptRoot $OutputDir }

if ($Clean -and (Test-Path -LiteralPath $outPath)) {
    Write-Host "Cleaning output directory: $outPath" -ForegroundColor Yellow
    Remove-Item -LiteralPath $outPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outPath -Force | Out-Null

$lexerName = Get-GrammarName -FilePath $lexerGrammar -Kind 'lexer'
$parserName = Get-GrammarName -FilePath $parserGrammar -Kind 'parser'

$lexerLabel = if ($null -ne $lexerName -and $lexerName -ne '') { $lexerName } else { Split-Path $lexerGrammar -Leaf }
$parserLabel = if ($null -ne $parserName -and $parserName -ne '') { $parserName } else { Split-Path $parserGrammar -Leaf }

# ANTLR requires the grammar name and filename to match.
# Your repo keeps them as Lexer.g4 / Parser.g4, so we generate from temporary copies
# named after the declared grammar (e.g., MapCssLexer.g4).
$effectiveLexerGrammar = $lexerGrammar
$effectiveParserGrammar = $parserGrammar

$tempGrammarDir = $null
if ($lexerName -and ((Split-Path $lexerGrammar -Leaf) -ne ("$lexerName.g4")) -or
    $parserName -and ((Split-Path $parserGrammar -Leaf) -ne ("$parserName.g4"))) {
    $tempGrammarDir = Join-Path $toolsDir (Join-Path 'tmp' ("antlr-" + [Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $tempGrammarDir -Force | Out-Null

    if ($lexerName) {
        $effectiveLexerGrammar = Join-Path $tempGrammarDir ("$lexerName.g4")
        Copy-Item -LiteralPath $lexerGrammar -Destination $effectiveLexerGrammar -Force
    }
    if ($parserName) {
        $effectiveParserGrammar = Join-Path $tempGrammarDir ("$parserName.g4")
        Copy-Item -LiteralPath $parserGrammar -Destination $effectiveParserGrammar -Force
    }
}

Write-Host "Generating C# from grammars:" -ForegroundColor Green
Write-Host "  Lexer : $lexerLabel" 
Write-Host "  Parser: $parserLabel" 
Write-Host "  Output: $outPath" 
Write-Host "  Package/namespace: $PackageName" 

$commonArgs = @(
    '-Dlanguage=CSharp',
    '-encoding', 'UTF-8',
    '-o', $outPath,
    '-Xexact-output-dir',
    '-package', $PackageName
)

if ($Visitor) { $commonArgs += '-visitor' }
if ($Listener) { $commonArgs += '-listener' }

# Important: generate lexer first so the parser can pick up tokenVocab (.tokens)
Write-Host "Running ANTLR (lexer)..." -ForegroundColor Cyan
& $java.Source -jar $antlrJar @commonArgs $effectiveLexerGrammar
if ($LASTEXITCODE -ne 0) {
    throw "ANTLR lexer generation failed (exit code $LASTEXITCODE)."
}

Write-Host "Running ANTLR (parser)..." -ForegroundColor Cyan
$parserArgs = @()
$parserArgs += $commonArgs
$parserArgs += @('-lib', $outPath, $effectiveParserGrammar)
& $java.Source -jar $antlrJar @parserArgs
if ($LASTEXITCODE -ne 0) {
    throw "ANTLR parser generation failed (exit code $LASTEXITCODE)."
}

Write-Host "Done. Generated C# sources are in: $outPath" -ForegroundColor Green
Write-Host "Note: to compile/run the parser, add the Antlr4 runtime package (e.g. Antlr4.Runtime.Standard) to your .NET project." -ForegroundColor DarkGray
