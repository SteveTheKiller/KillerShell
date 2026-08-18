param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Shell\ShortcutsOverlay.cs'),
    [string]$Strings = (Join-Path $PSScriptRoot '..\Strings\en-US.xaml'),
    [string]$Output = (Join-Path $PSScriptRoot 'shortcuts.generated.js')
)

$sourceText = [IO.File]::ReadAllText((Resolve-Path $Source))
[xml]$resources = [IO.File]::ReadAllText((Resolve-Path $Strings))
$labels = @{}
foreach ($node in $resources.ResourceDictionary.ChildNodes) {
    if ($node.NodeType -ne 'Element') { continue }
    $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    if ($key) { $labels[$key] = $node.InnerText }
}

$pattern = 'new\(KsScope\.(?<scope>\w+),\s*"(?<cat>[^"]+)",\s*"(?<keys>[^"]*)",\s*"(?<label>Str_[^"]+)"'
$rows = foreach ($match in [regex]::Matches($sourceText, $pattern)) {
    if (-not $match.Groups['keys'].Value) { continue }
    $labelKey = $match.Groups['label'].Value
    [ordered]@{
        scope = $match.Groups['scope'].Value
        category = $match.Groups['cat'].Value
        keys = $match.Groups['keys'].Value
        label = $(if ($labels.ContainsKey($labelKey)) { $labels[$labelKey] } else { $labelKey })
    }
}

$json = $rows | ConvertTo-Json -Depth 3 -Compress
$content = "/* Generated from Shell/ShortcutsOverlay.cs by generate-shortcuts.ps1. */`nwindow.KS_SHORTCUTS=$json;`n"
[IO.File]::WriteAllText($Output, $content, [Text.UTF8Encoding]::new($false))
Write-Host "Generated $($rows.Count) shortcut rows -> $Output"
