# Runs tests with coverage collection and generates reports (HTML + text summary)
$proj = "src\MapCss.Styling.Tests\MapCss.Styling.Tests.csproj"
$resultsDir = "tools\tmp\TestResults"
$reportDir = "tools\tmp\coverage-report"

Write-Host "Running tests with coverage..."
# Remove previous coverage files to ensure we pick up the newly generated file
Get-ChildItem -Path $resultsDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
# Run tests with XPlat coverage collector (produces coverage.cobertura.xml)
dotnet test $proj --collect:"XPlat Code Coverage" --results-directory $resultsDir -v minimal | Write-Host

Write-Host "Finding coverage file..."
$coverage = Get-ChildItem -Path $resultsDir -Recurse -Filter "coverage.cobertura.xml" | Select-Object -First 1
if (-not $coverage) {
    Write-Error "Coverage file not found. Check test output for failures."
    exit 1
}

# Remove classes from generated files to avoid counting generated parser code in coverage
$xml = [xml](Get-Content $coverage.FullName)
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace("c", $xml.DocumentElement.NamespaceURI)

foreach ($classNode in $xml.SelectNodes('//class')) {
    $filename = $classNode.GetAttribute('filename')
    if ($filename -like '*Generated\*' -or $filename -like '*Generated/*') {
        $parent = $classNode.ParentNode
        $parent.RemoveChild($classNode) | Out-Null
    }
}

$modified = Join-Path $resultsDir "coverage.filtered.cobertura.xml"
$xml.Save($modified)
$coverage = Get-Item $modified
Write-Host "Filtered coverage written to $modified"

Write-Host "Generating report into $reportDir ..."
# Use Start-Process to invoke ReportGenerator to avoid PowerShell parsing quirks and capture the text summary output.
$htmlArgs = @("tool", "run", "reportgenerator", "--", "-reports:$($coverage.FullName)", "-targetdir:$reportDir", "-reporttypes:Html")
$procHtml = Start-Process -FilePath "dotnet" -ArgumentList $htmlArgs -NoNewWindow -Wait -PassThru
if ($procHtml.ExitCode -ne 0) { Write-Error "ReportGenerator (Html) failed with exit code $($procHtml.ExitCode)" }

Write-Host "Generating text summary..."
$summaryFile = Join-Path $env:TEMP "coverage_summary.txt"
if (Test-Path $summaryFile) { Remove-Item $summaryFile -Force }
$txtArgs = @("tool", "run", "reportgenerator", "--", "-reports:$($coverage.FullName)", "-targetdir:$reportDir", "-reporttypes:TextSummary")
$proc = Start-Process -FilePath "dotnet" -ArgumentList $txtArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $summaryFile
if ($proc.ExitCode -eq 0 -and (Test-Path $summaryFile)) {
    $summary = Get-Content $summaryFile -Raw
    Write-Host "---- Coverage Summary ----"
    Write-Host $summary
} else {
    Write-Error "ReportGenerator (TextSummary) failed with exit code $($proc.ExitCode)"
}

Write-Host "Report generated: $reportDir\index.html"
