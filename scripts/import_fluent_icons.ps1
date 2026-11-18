param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$commit = '41089846ad80c5749df505aef35a68712c1561b9'
$repository = 'https://github.com/microsoft/fluentui-system-icons'
$rawBase = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/$commit"
$workRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = Join-Path $workRoot 'll_launcher'
$assetDirectory = Join-Path $projectRoot 'Assets/Icons'
$resourceDirectory = Join-Path $projectRoot 'Resources'
$null = New-Item -ItemType Directory -Force -Path $assetDirectory, $resourceDirectory

# Key, upstream directory, file name base, variant. All assets have 24px viewports.
$selection = @(
    @('grid', 'Grid', 'grid', 'regular'),
    @('star', 'Star', 'star', 'regular'),
    @('starFilled', 'Star', 'star', 'filled'),
    @('code', 'Code', 'code', 'regular'),
    @('briefcase', 'Briefcase', 'briefcase', 'regular'),
    @('wrench', 'Wrench', 'wrench', 'regular'),
    @('paint', 'Paint Brush', 'paint_brush', 'regular'),
    @('games', 'Games', 'games', 'regular'),
    @('folder', 'Folder', 'folder', 'regular'),
    @('book', 'Book', 'book', 'regular'),
    @('music', 'Music Note 2', 'music_note_2', 'regular'),
    @('camera', 'Camera', 'camera', 'regular'),
    @('video', 'Video', 'video', 'regular'),
    @('chat', 'Chat', 'chat', 'regular'),
    @('mail', 'Mail', 'mail', 'regular'),
    @('cloud', 'Cloud', 'cloud', 'regular'),
    @('image', 'Image', 'image', 'regular'),
    @('calculator', 'Calculator', 'calculator', 'regular'),
    @('add', 'Add', 'add', 'regular'),
    @('sidebar', 'Panel Left', 'panel_left', 'regular'),
    @('pin', 'Pin', 'pin', 'regular'),
    @('pinFilled', 'Pin', 'pin', 'filled'),
    @('search', 'Search', 'search', 'regular'),
    @('list', 'Text Bullet List', 'text_bullet_list', 'regular'),
    @('close', 'Dismiss', 'dismiss', 'regular'),
    @('minimize', 'Subtract', 'subtract', 'regular'),
    @('flash', 'Flash', 'flash', 'regular'),
    @('location', 'Location', 'location', 'filled'),
    @('more', 'More Horizontal', 'more_horizontal', 'regular'),
    @('link', 'Link', 'link', 'regular'),
    @('file', 'Document', 'document', 'regular'),
    @('edit', 'Edit', 'edit', 'regular'),
    @('delete', 'Delete', 'delete', 'regular'),
    @('arrow', 'Chevron Right', 'chevron_right', 'regular'),
    @('check', 'Checkmark', 'checkmark', 'regular')
)

$jobs = for ($index = 0; $index -lt $selection.Count; $index++) {
    $item = $selection[$index]
    $filename = 'ic_fluent_{0}_24_{1}.svg' -f $item[2], $item[3]
    $escapedDirectory = [Uri]::EscapeDataString($item[1])
    [pscustomobject]@{
        Index = $index
        Key = 'Icon.' + $item[0]
        Filename = $filename
        Url = "$rawBase/assets/$escapedDirectory/SVG/$filename"
        Target = Join-Path $assetDirectory $filename
    }
}

if (-not $Offline) {
    $jobs | ForEach-Object -ThrottleLimit 6 -Parallel {
        $ErrorActionPreference = 'Stop'
        Invoke-WebRequest -Uri $_.Url -OutFile $_.Target -TimeoutSec 45 -MaximumRetryCount 2 -RetryIntervalSec 1
    }
    Invoke-WebRequest -Uri "$rawBase/LICENSE" -OutFile (Join-Path $assetDirectory 'LICENSE') -TimeoutSec 45
}
if (-not (Test-Path -LiteralPath (Join-Path $assetDirectory 'LICENSE'))) {
    throw 'The upstream LICENSE file is required.'
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
$lines.Add('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">')
$lines.Add('    <!-- Generated from unmodified Microsoft Fluent UI System Icons SVGs.')
$lines.Add("         Upstream commit: $commit")
$lines.Add('         All source viewports are 24 x 24. Set color on the consuming Path.Fill.')
$lines.Add('         See Assets/Icons/LICENSE and NOTICE.md. Regenerate with scripts/import_fluent_icons.ps1. -->')
$manifestIcons = [Collections.Generic.List[object]]::new()

foreach ($job in $jobs) {
    [xml]$svg = Get-Content -LiteralPath $job.Target -Raw
    if ($svg.DocumentElement.GetAttribute('viewBox') -ne '0 0 24 24') {
        throw "Unexpected viewport in $($job.Filename)"
    }
    $lines.Add('    <GeometryGroup x:Key="{0}" FillRule="Nonzero">' -f $job.Key)
    $pathCount = 0
    foreach ($node in $svg.DocumentElement.ChildNodes) {
        if ($node.NodeType -eq [Xml.XmlNodeType]::Comment) { continue }
        if ($node.LocalName -ne 'path' -or $node.HasAttribute('transform') -or $node.HasAttribute('stroke')) {
            throw "Unsupported SVG element in $($job.Filename): $($node.LocalName)"
        }
        $pathData = $node.GetAttribute('d')
        if (-not $pathData) { throw "Missing path data in $($job.Filename)" }
        $fillRule = $node.GetAttribute('fill-rule')
        if (-not $fillRule) { $fillRule = $svg.DocumentElement.GetAttribute('fill-rule') }
        if (-not $fillRule) { $fillRule = 'nonzero' }
        $wpfRule = switch ($fillRule) {
            'nonzero' { 'Nonzero' }
            'evenodd' { 'EvenOdd' }
            default { throw "Unsupported fill-rule $fillRule" }
        }
        $escapedData = [Security.SecurityElement]::Escape($pathData)
        $lines.Add(('        <PathGeometry FillRule="{0}" Figures="{1}" />' -f $wpfRule, $escapedData))
        $pathCount++
    }
    if ($pathCount -eq 0) { throw "No paths in $($job.Filename)" }
    $lines.Add('    </GeometryGroup>')
    $manifestIcons.Add([ordered]@{
        key = $job.Key
        filename = $job.Filename
        source = $job.Url
        sha256 = (Get-FileHash -LiteralPath $job.Target -Algorithm SHA256).Hash.ToLowerInvariant()
        pathCount = $pathCount
    })
}
$lines.Add('</ResourceDictionary>')
$resourcePath = Join-Path $resourceDirectory 'FluentIcons.xaml'
[IO.File]::WriteAllLines($resourcePath, $lines, [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{
    repository = $repository
    commit = $commit
    viewport = @(0, 0, 24, 24)
    license = 'MIT'
    icons = $manifestIcons
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $assetDirectory 'sources.json') -Encoding utf8NoBOM
$notice = @"
# Fluent UI System Icons attribution

These selected monochrome icons are from Microsoft Fluent UI System Icons:
$repository

- Copyright (c) Microsoft Corporation.
- License: MIT; the complete upstream license is included in LICENSE.
- Pinned upstream commit: $commit
- Source format: unmodified 24 x 24 SVG, Regular or Filled variant.
- Per-file source URLs and SHA-256 checksums: sources.json.
- Local conversion: SVG path data is transferred into WPF PathGeometry resources, preserving each path's fill rule. There is no authored replacement artwork. Colors are supplied by the application.
- Generated resource: Resources/FluentIcons.xaml.
- Reproducible importer: scripts/import_fluent_icons.ps1. Run with -Offline to regenerate from these SVGs without network access.

The license covers these Fluent resources. Icons extracted from a user's installed programs or Windows Shell are separate resources and are not part of this icon bundle.
"@
$notice | Set-Content -LiteralPath (Join-Path $assetDirectory 'NOTICE.md') -Encoding utf8NoBOM
Write-Output "Imported $($manifestIcons.Count) pinned Fluent icons; generated $resourcePath"
$manifestIcons | ForEach-Object { [pscustomobject]$_ } | Select-Object key, pathCount | Format-Table -AutoSize

