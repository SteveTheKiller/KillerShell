param([string]$Path = (Join-Path $PSScriptRoot 'ks-i18n.js'))

function Fold([string]$word) {
    $normal = $word.Normalize([Text.NormalizationForm]::FormD)
    -join ($normal.ToCharArray() | Where-Object {
        [Globalization.CharUnicodeInfo]::GetUnicodeCategory($_) -ne
        [Globalization.UnicodeCategory]::NonSpacingMark
    })
}

function Add-Word([hashtable]$map, [string]$word) {
    if ($word -notmatch '[^\x00-\x7F]') { return }
    $folded = (Fold $word).ToLowerInvariant()
    if ($folded -notmatch '^[a-z]{4,}$') { return }
    if (-not $map.ContainsKey($folded)) { $map[$folded] = New-Object 'System.Collections.Generic.HashSet[string]' }
    [void]$map[$folded].Add($word.ToLowerInvariant())
}

function Dictionary-Map([string[]]$dictionaries, [string[]]$corpora) {
    $map = @{}
    foreach ($dictionary in $dictionaries) {
        foreach ($line in [IO.File]::ReadLines($dictionary) | Select-Object -Skip 1) {
            Add-Word $map (($line -split '[/\s]')[0])
        }
    }
    foreach ($corpus in $corpora) {
        if (-not (Test-Path -LiteralPath $corpus)) { continue }
        $body = [IO.File]::ReadAllText($corpus)
        foreach ($match in [regex]::Matches($body, '[\p{L}]{4,}')) { Add-Word $map $match.Value }
    }
    return $map
}

function Restore-Block([string]$body, [string]$lang, [hashtable]$map) {
    $pattern = '(?s)( "' + [regex]::Escape($lang) + '": \{)(.*?)(?=\n \},?\n "|\n \}\n\};)'
    return [regex]::Replace($body, $pattern, {
        param($block)
        $prefix = $block.Groups[1].Value
        $content = [regex]::Replace($block.Groups[2].Value, '\b[A-Za-z]{4,}\b', {
            param($token)
            $key = $token.Value.ToLowerInvariant()
            if (-not $map.ContainsKey($key) -or $map[$key].Count -ne 1) { return $token.Value }
            $replacement = @($map[$key])[0]
            if ($token.Value -cmatch '^[A-Z]') {
                $replacement = $replacement.Substring(0,1).ToUpperInvariant() + $replacement.Substring(1)
            }
            return $replacement
        })
        return $prefix + $content
    }, 1)
}

$lo = 'C:\Program Files\LibreOffice\share\extensions'
$code = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sharedSpanish = @(Join-Path $code 'KillerNotes\notes-landing\kn-i18n.js')
$sharedFrench  = $sharedSpanish
$sharedTurkish = @(
    (Join-Path $code 'KillerNotes\notes-landing\kn-i18n.js'),
    (Join-Path $code 'KillerShell\Strings\tr-TR.xaml'),
    (Join-Path $code 'KillerNotes\Strings\tr-TR.xaml'),
    (Join-Path $code 'KillerPDF\Strings\tr-TR.xaml'),
    (Join-Path $code 'KillerScan\Strings\tr-TR.xaml')
)

$maps = @{
    es = Dictionary-Map @((Join-Path $lo 'dict-es\es_ES.dic')) $sharedSpanish
    fr = Dictionary-Map @((Join-Path $lo 'dict-fr\fr.dic')) $sharedFrench
    tr = Dictionary-Map @() $sharedTurkish
}

$text = [IO.File]::ReadAllText($Path)
foreach ($lang in @('es','fr','tr')) { $text = Restore-Block $text $lang $maps[$lang] }
[IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
Write-Host 'Restored unambiguous diacritics in es, fr, and tr website copy.'
