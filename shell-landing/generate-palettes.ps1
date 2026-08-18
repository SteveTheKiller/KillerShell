param(
    [string]$Themes = (Join-Path $PSScriptRoot '..\Themes'),
    [string]$Output = (Join-Path $PSScriptRoot 'palettes.generated.css')
)

$themeFiles = @('Dark','Light','Black','Blood','Greed','Cyanotic','Ectoplasm','Decay','Malaise','Sepulchre','Delirium','Mourning')
$css = New-Object Text.StringBuilder
[void]$css.AppendLine('/* Generated from Themes/*.xaml by generate-palettes.ps1. 98SE is intentionally excluded. */')

foreach ($name in $themeFiles) {
    [xml]$doc = [IO.File]::ReadAllText((Join-Path $Themes "$name.xaml"))
    $ns = New-Object Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    $ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')

    function Value([string]$key) {
        $node = $doc.SelectSingleNode("//*[@x:Key='$key']", $ns)
        if ($null -eq $node) { throw "$name.xaml is missing $key" }
        if ($node.LocalName -eq 'SolidColorBrush') { return $node.GetAttribute('Color') }
        if ($node.LocalName -eq 'LinearGradientBrush') {
            $stops = @($node.ChildNodes | Where-Object LocalName -eq 'GradientStop')
            return 'linear-gradient(90deg,' + (($stops | ForEach-Object { $_.GetAttribute('Color') + ' ' + ([double]$_.GetAttribute('Offset') * 100) + '%' }) -join ',') + ')'
        }
        if ($node.LocalName -eq 'Double') { return $node.InnerText }
        throw "Unsupported $($node.LocalName) for $key in $name.xaml"
    }

    $accent = Value 'PrimaryBrush'
    if ($name -in @('Dark','Light','Black')) {
        [xml]$accentDoc = [IO.File]::ReadAllText((Join-Path $Themes "Accents\$name\Blue.xaml"))
        $accentNode = $accentDoc.SelectSingleNode("//*[@x:Key='PrimaryBrush']", $ns)
        if ($null -eq $accentNode) { throw "The $name Blue accent is missing PrimaryBrush" }
        $accent = $accentNode.GetAttribute('Color')
    }

    $key = if ($name -eq 'Black') { 'hc' } else { $name.ToLowerInvariant() }
    $line = "html[data-theme=`"$key`"] { " +
        "--bg:$(Value 'PaneBrush'); --surface:$(Value 'SurfaceBrush'); --panel:$(Value 'PaneBrush'); " +
        "--sidebar:$(Value 'BackgroundBrush'); --canvas:$(Value 'PaneBrush'); " +
        "--border:$(Value 'CardBorderBrush'); --pane-border:$(Value 'PaneBorderBrush'); " +
        "--accent:$accent; --accent-text:$(Value 'OnPrimaryBrush'); " +
        "--text:$(Value 'TextBrush'); --text2:$(Value 'MutedTextBrush'); --muted:$(Value 'DimTextBrush'); " +
        "--modal:$(Value 'MenuBackgroundBrush'); --btn:$accent; --btn-text:$(Value 'OnPrimaryBrush'); " +
        "--grain:$(Value 'GrainOpacity'); }"
    [void]$css.AppendLine($line)
}

[IO.File]::WriteAllText($Output, $css.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Generated $($themeFiles.Count) web palettes -> $Output (98SE excluded)"
