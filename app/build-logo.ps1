# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the WPC Solutions logos shown at the end of Settings,
# app/src/ArkPar.App/Assets/wpc-logo-*.svg, from the exports in docs/assets:
#   "Logo WPC Horizontal Preto.svg"  -> wpc-logo-light-background.svg
#   "Logo WPC Horizontal BRANCO.svg" -> wpc-logo-dark-background.svg
#
# The exports keep their colours in a <style> block of CSS classes, which WinUI
# ignores: it draws SVG through Direct2D, which reads only presentation
# attributes. So each class becomes fill/stroke attributes on the shapes that
# use it, and the <style> block and the class names go. "isolation" has no
# attribute form and changes nothing here (no blend modes), so it is dropped.
# The exports stay as they are. Run this after replacing either one, and commit
# the two copies: the build ships them, it does not make them.
#
# Usage: pwsh -File app/build-logo.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = Join-Path $repoRoot 'docs/assets'
$assets = Join-Path $PSScriptRoot 'src/ArkPar.App/Assets'

$logos = [ordered]@{
    'Logo WPC Horizontal Preto.svg'  = 'wpc-logo-light-background.svg'
    'Logo WPC Horizontal BRANCO.svg' = 'wpc-logo-dark-background.svg'
}

# The CSS properties that have a presentation attribute of the same name.
$convertible = @('fill', 'stroke', 'stroke-width', 'stroke-miterlimit')
$dropped = @('isolation')

function Read-Classes([string]$css) {
    # Rules like ".st2, .st3 { stroke: #fff; }". A class gathers its properties from every rule naming it; a later
    # rule wins over an earlier one, as in CSS (all are single-class selectors, of the same specificity).
    $classes = @{}
    foreach ($rule in [regex]::Matches($css, '([^{}]+)\{([^{}]*)\}')) {
        $selectors = $rule.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() }
        foreach ($selector in $selectors) {
            if ($selector -notmatch '^\.([\w-]+)$') {
                throw "Unexpected CSS selector '$selector': only single classes are handled."
            }
        }
        foreach ($declaration in $rule.Groups[2].Value -split ';') {
            if ($declaration.Trim() -eq '') {
                continue
            }
            $name, $value = $declaration -split ':', 2 | ForEach-Object { $_.Trim() }
            if ($name -in $dropped) {
                continue
            }
            if ($name -notin $convertible) {
                throw "Unexpected CSS property '$name': add it to `$convertible or `$dropped."
            }
            # Direct2D reads a bare number; browsers accept both.
            if ($name -eq 'stroke-width') {
                $value = $value -replace 'px$', ''
            }
            foreach ($selector in $selectors) {
                $class = $selector.Substring(1)
                if (-not $classes.ContainsKey($class)) {
                    $classes[$class] = [ordered]@{}
                }
                $classes[$class][$name] = $value
            }
        }
    }
    $classes
}

foreach ($source in $logos.Keys) {
    $document = [System.Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.Load((Join-Path $sources $source))

    $namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('svg', 'http://www.w3.org/2000/svg')

    $styles = $document.SelectNodes('//svg:style', $namespaces)
    if ($styles.Count -ne 1) {
        throw "'$source' has $($styles.Count) <style> blocks, expected 1."
    }
    $classes = Read-Classes $styles[0].InnerText

    foreach ($element in @($document.SelectNodes('//*[@class]'))) {
        foreach ($class in $element.GetAttribute('class') -split '\s+') {
            if (-not $classes.ContainsKey($class)) {
                # A class with only dropped properties (isolation) has no rule left.
                continue
            }
            foreach ($property in $classes[$class].GetEnumerator()) {
                $element.SetAttribute($property.Key, $property.Value)
            }
        }
        $element.RemoveAttribute('class')
    }

    # <defs> held only the <style> block: remove it with the line it sat on.
    $defs = $styles[0].ParentNode
    if ($defs.LocalName -ne 'defs' -or @($defs.ChildNodes | Where-Object NodeType -ne 'Whitespace').Count -ne 1) {
        throw "'$source' has more than the <style> block inside <defs>."
    }
    if ($defs.PreviousSibling.NodeType -eq 'Whitespace') {
        [void]$defs.ParentNode.RemoveChild($defs.PreviousSibling)
    }
    [void]$defs.ParentNode.RemoveChild($defs)

    if ($document.SelectNodes('//svg:text', $namespaces).Count -ne 0) {
        throw "'$source' has text, which Direct2D draws in whatever font the PC has: export it with text as outlines."
    }

    $target = Join-Path $assets $logos[$source]
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.NewLineChars = "`n"
    $writer = [System.Xml.XmlWriter]::Create($target, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
    # The exports end without a line break; files git keeps as text end with one.
    [System.IO.File]::AppendAllText($target, "`n")

    Write-Host "Logo built: $target"
}
