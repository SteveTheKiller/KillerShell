param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Shell\ShortcutsOverlay.cs'),
    [string]$StringsDirectory = (Join-Path $PSScriptRoot '..\Strings'),
    [string]$Output = (Join-Path $PSScriptRoot 'shortcuts.generated.js')
)

$sourceText = [IO.File]::ReadAllText((Resolve-Path $Source))
$localeFiles = [ordered]@{
    en = 'en-US.xaml'; hu = 'hu-HU.xaml'; pl = 'pl-PL.xaml'; cs = 'cs-CZ.xaml'
    es = 'es.xaml'; de = 'de-DE.xaml'; fr = 'fr-FR.xaml'; tr = 'tr-TR.xaml'
    'zh-cn' = 'zh-CN.xaml'; zh = 'zh-TW.xaml'; bn = 'bn.xaml'; ja = 'ja-JP.xaml'
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

$pattern = 'new\(KsScope\.(?<scope>\w+),\s*"(?<cat>[^"]+)",\s*"(?<keys>[^"]*)",\s*"(?<label>Str_[^"]+)"'
$rows = foreach ($match in [regex]::Matches($sourceText, $pattern)) {
    if (-not $match.Groups['keys'].Value) { continue }
    $labelKey = $match.Groups['label'].Value
    $row = [ordered]@{
        scope = $match.Groups['scope'].Value
        category = $match.Groups['cat'].Value
        keys = $match.Groups['keys'].Value
        label = [ordered]@{}
    }
    foreach ($locale in $localeFiles.Keys) {
        $row.label[$locale] = $(if ($labels[$locale].ContainsKey($labelKey)) { $labels[$locale][$labelKey] } else { $labels.en[$labelKey] })
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
[IO.File]::WriteAllText($Output, $content, [Text.UTF8Encoding]::new($false))
Write-Host "Generated $($rows.Count) shortcut rows -> $Output"
