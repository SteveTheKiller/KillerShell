param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Shell\ShortcutsOverlay.cs'),
    [string]$StringsDirectory = (Join-Path $PSScriptRoot '..\Strings'),
    [string]$Output = (Join-Path $PSScriptRoot 'shortcuts.generated.js'),
    [string]$HelpPage = (Join-Path $PSScriptRoot 'help.html'),
    [switch]$Check
)

$sourceText = [IO.File]::ReadAllText((Resolve-Path $Source))
$localeFiles = [ordered]@{
    en = 'en-US.xaml'; hu = 'hu-HU.xaml'; pl = 'pl-PL.xaml'; cs = 'cs-CZ.xaml'
    es = 'es.xaml'; de = 'de-DE.xaml'; fr = 'fr-FR.xaml'; tr = 'tr-TR.xaml'
    'zh-cn' = 'zh-CN.xaml'; zh = 'zh-TW.xaml'; bn = 'bn.xaml'; ja = 'ja-JP.xaml'
    it = 'it-IT.xaml'; ru = 'ru-RU.xaml'; kk = 'kk-KZ.xaml'; vi = 'vi-VN.xaml'; uk = 'uk-UA.xaml'; nb = 'nb-NO.xaml'; pt = 'pt-BR.xaml'; sl = 'sl-SI.xaml'
}
$labels = [ordered]@{}
foreach ($locale in $localeFiles.Keys) {
    [xml]$resources = [IO.File]::ReadAllText((Resolve-Path (Join-Path $StringsDirectory $localeFiles[$locale])))
    $dict = @{}
    foreach ($node in $resources.ResourceDictionary.ChildNodes) {
        if ($node.NodeType -ne 'Element') { continue }
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($key) { $dict[$key] = $node.InnerText }
    }
    $labels[$locale] = $dict
}

$pattern = 'new\(KsScope\.(?<scope>\w+),\s*"(?<cat>[^"]+)",\s*"(?<keys>[^"]*)",\s*"(?<label>Str_[^"]+)",\s*KbLayer\.(?<layer>\w+)(?<caps>[^)]*)\)'
$rows = foreach ($match in [regex]::Matches($sourceText, $pattern)) {
    $labelKey = $match.Groups['label'].Value
    $row = [ordered]@{
        scope = $match.Groups['scope'].Value
        category = $match.Groups['cat'].Value
        keys = $match.Groups['keys'].Value
        layer = $match.Groups['layer'].Value.ToLowerInvariant()
        caps = @([regex]::Matches($match.Groups['caps'].Value, '"(?<id>[^"]+)"') | ForEach-Object { $_.Groups['id'].Value })
        label = [ordered]@{}
    }
    foreach ($locale in $localeFiles.Keys) {
        if (!$labels[$locale].ContainsKey($labelKey)) { throw "Missing $locale shortcut label: $labelKey" }
        $row.label[$locale] = $labels[$locale][$labelKey]
    }
    $row
}

$scopeKeys = [ordered]@{ Global='Str_Ks_ScopeGlobal'; Files='Str_Ks_ScopeFiles'; Terminal='Str_Ks_ScopeTerminal'; Editor='Str_Ks_ScopeEditor'; Processes='Str_TabTitle_TaskManager'; Events='Str_TabTitle_EventViewer'; Registry='Str_TabTitle_RegistryEditor'; Storage='Str_TabTitle_Storage' }
$categoryKeys = [ordered]@{ Search='Str_Ks_CatSearch'; Nav='Str_Ks_CatNav'; Tabs='Str_Ks_CatTabs'; View='Str_Ks_CatView'; File='Str_Ks_CatFile'; Edit='Str_Ks_CatEdit'; Help='Str_Ks_CatHelp' }
$meta = [ordered]@{ scopes=[ordered]@{}; categories=[ordered]@{}; views=[ordered]@{} }
foreach ($name in $scopeKeys.Keys) {
    $meta.scopes[$name] = [ordered]@{}
    foreach ($locale in $localeFiles.Keys) { $meta.scopes[$name][$locale] = $labels[$locale][$scopeKeys[$name]] }
}
foreach ($name in $categoryKeys.Keys) {
    $meta.categories[$name] = [ordered]@{}
    foreach ($locale in $localeFiles.Keys) { $meta.categories[$name][$locale] = $labels[$locale][$categoryKeys[$name]] }
}
foreach ($view in ([ordered]@{ List='Str_Ks_ViewList'; Keyboard='Str_Ks_ViewKeyboard' }).GetEnumerator()) {
    $meta.views[$view.Key] = [ordered]@{}
    foreach ($locale in $localeFiles.Keys) { $meta.views[$view.Key][$locale] = $labels[$locale][$view.Value] }
}

$json = $rows | ConvertTo-Json -Depth 3 -Compress
$metaJson = $meta | ConvertTo-Json -Depth 4 -Compress
$content = "/* Generated from Shell/ShortcutsOverlay.cs and every Strings dictionary. */`nwindow.KS_SHORTCUTS=$json;`nwindow.KS_SHORTCUT_META=$metaJson;`n"
$content = $content.Replace("'", '\u0027').Replace('<', '\u003c').Replace('>', '\u003e').Replace('&', '\u0026')
if ($content.IndexOf([char]0x2013) -ge 0 -or $content.IndexOf([char]0x2014) -ge 0) { throw 'Prohibited dash in generated content' }
# The help page's no-script fallback: the same English list help.html's script draws, so a reader
# without JavaScript (or a crawler) sees every current binding instead of a hand-kept copy.
function HtmlText([string]$text) { [Net.WebUtility]::HtmlEncode($text) }
$scopeOrder = 'Global','Files','Terminal','Editor','Processes','Events','Registry','Storage'
$categoryOrder = 'Search','Nav','Tabs','View','File','Edit','Help'
$fallback = [Text.StringBuilder]::new()
foreach ($scope in $scopeOrder) {
    $scoped = @($rows | Where-Object { $_.scope -eq $scope -and $_.keys })
    if (!$scoped.Count) { continue }
    [void]$fallback.Append("`n<h3 class=`"kbd-scope`">$(HtmlText $meta.scopes[$scope].en)</h3>`n<div class=`"kbd-cols`">`n<div class=`"kbd-col`">`n")
    foreach ($category in $categoryOrder) {
        $grouped = @($scoped | Where-Object { $_.category -eq $category })
        if (!$grouped.Count) { continue }
        [void]$fallback.Append("`n<div class=`"kbd-group`"><h4>$(HtmlText $meta.categories[$category].en.ToUpperInvariant())</h4>`n")
        foreach ($binding in $grouped) {
            [void]$fallback.Append("<div class=`"kbd-row`"><span class=`"k`">$(HtmlText $binding.keys)</span><span class=`"d`">$(HtmlText $binding.label.en)</span></div>`n")
        }
        [void]$fallback.Append("</div>`n")
    }
    [void]$fallback.Append("`n</div>`n</div>`n")
}
$helpText = [IO.File]::ReadAllText((Resolve-Path $HelpPage))
$markers = [regex]'(?s)(<!-- shortcuts:begin[^>]*-->)(.*?)(<!-- shortcuts:end -->)'
if (!$markers.IsMatch($helpText)) { throw "The shortcut markers are missing from $HelpPage" }
$newline = if ($helpText.Contains("`r`n")) { "`r`n" } else { "`n" }
$block = ($fallback.ToString() + "`n").Replace("`n", $newline)
$helpUpdated = $markers.Replace($helpText, { param($m) $m.Groups[1].Value + $block + $m.Groups[3].Value }, 1)
if ($helpUpdated.IndexOf([char]0x2013) -ge 0 -or $helpUpdated.IndexOf([char]0x2014) -ge 0) { throw 'Prohibited dash in the help page' }

if ($Check) {
    if (!(Test-Path -LiteralPath $Output) -or [IO.File]::ReadAllText($Output) -cne $content) { throw 'Generated shortcuts are stale. Run generate-shortcuts.ps1' }
    if ($helpUpdated -cne $helpText) { throw 'The help page shortcut list is stale. Run generate-shortcuts.ps1' }
    Write-Host "Generated shortcuts are current ($($rows.Count) rows, $($localeFiles.Count) locales, help fallback included)"
} else {
    [IO.File]::WriteAllText($Output, $content, [Text.UTF8Encoding]::new($false))
    if ($helpUpdated -cne $helpText) { [IO.File]::WriteAllText((Resolve-Path $HelpPage), $helpUpdated, [Text.UTF8Encoding]::new($false)) }
    Write-Host "Generated $($rows.Count) shortcut rows -> $Output and the help page fallback"
}
