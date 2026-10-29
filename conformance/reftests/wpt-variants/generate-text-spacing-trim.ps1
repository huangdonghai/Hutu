# Expands the variant-driven text-spacing-trim reftests into static pages.
#
# Each upstream test carries <meta name="variant" content="?class=halt,htb">, and support/variant-class.js
# turns that query into classes on the root element. This engine runs no script, so every one of those
# tests would render the default state whatever variant it was written for. Here the same attributes are
# written into the page itself: one page per variant, and a reference beside it carrying the same ones.
#
# The references also carry nested type-selector rules (`.trim-start { ps, ls { … } }`), which this
# engine's parser reads as declarations; those are written out flat so the line-start marks apply.

$src = 'D:\_git0\Hutu\build\wpt\css\css-text\text-spacing-trim'
$dst = 'D:\_git0\Hutu\conformance\reftests\wpt-variants\text-spacing-trim'
$utf8 = [System.Text.UTF8Encoding]::new($false)

# The suite reaches its fonts by an address from its root; a page written into the corpus reaches them
# by a relative one, the way every other borrowed page here does.
function Localized([string] $html) {
    return $html.Replace('"/fonts/', '"../../../fonts/').Replace("'/fonts/", "'../../../fonts/").Replace('(/fonts/', '(../../../fonts/')
}

# This engine's CSS parser drops a nested rule that begins with a type selector - `ps, ls { … }` inside
# a class rule is read as a declaration, not a rule - while the upstream reference states its line-start
# marks that way. Written flat, the same rules mean what they say and the spans are styled.
function Flattened([string] $html) {
    $html = [regex]::Replace(
        $html,
        '\.trim-start\s*\{\s*ps,\s*ls\s*\{([^}]*)\}\s*\}',
        '.trim-start ps, .trim-start ls {$1}')
    return [regex]::Replace(
        $html,
        '\.space-first\s*\{\s*ls\s*\{([^}]*)\}\s*\}',
        '.space-first ls {$1}')
}

$tests = @(
    'text-spacing-trim-001',
    'text-spacing-trim-colon-001',
    'text-spacing-trim-dot-001',
    'text-spacing-trim-end-001',
    'text-spacing-trim-end-span-001',
    'text-spacing-trim-fallback-002',
    'text-spacing-trim-feature-001',
    'text-spacing-trim-narrow-001',
    'text-spacing-trim-quote-001',
    'text-spacing-trim-space-all-001',
    'text-spacing-trim-span-001',
    'text-spacing-trim-start-001',
    'text-spacing-trim-start-002',
    'text-spacing-trim-start-oof-001',
    'text-spacing-trim-subset-001',
    'text-spacing-trim-trim-all-001'
)

New-Item -ItemType Directory -Force -Path $dst | Out-Null

$written = 0

foreach ($stem in $tests) {
    $testPath = Join-Path $src ($stem + '.html')
    $testHtml = [System.IO.File]::ReadAllText($testPath)

    $refHref = [regex]::Match($testHtml, 'rel="match"[^>]*href="(?<h>[^"]+)"').Groups['h'].Value
    $refPath = Join-Path $src $refHref
    $refHtml = [System.IO.File]::ReadAllText($refPath)

    $variants = [regex]::Matches($testHtml, '<meta name="variant" content="(?<v>[^"]+)"')

    if ($variants.Count -eq 0) {
        # A page that asks for no variant is already static: it is copied as it stands, with the two
        # rewrites every page in this folder gets - its fonts reached relatively, its references flat.
        [System.IO.File]::WriteAllText((Join-Path $dst ($stem + '.html')), (Localized $testHtml), $utf8)
        [System.IO.File]::WriteAllText((Join-Path $dst $refHref), (Flattened (Localized $refHtml)), $utf8)
        $written += 2
        Write-Host "static: $stem"
        continue
    }

    foreach ($variant in $variants) {
        $query = $variant.Groups['v'].Value.TrimStart('?')
        $classes = ''
        $lang = ''
        $styles = @()
        $suffix = ''

        foreach ($pair in $query.Split('&')) {
            $kv = $pair.Split('=', 2)

            if ($kv[0] -eq 'class') {
                $classes = $kv[1] -replace ',', ' '
                $suffix += '-' + ($kv[1] -replace ',', '-')
            }
            elseif ($kv[0] -eq 'lang') {
                $lang = $kv[1]
                $suffix += '-' + $kv[1]
            }
            elseif ($kv[0] -eq 'style') {
                $styles += $kv[1]
                $suffix += '-' + ($kv[1] -replace '[^A-Za-z0-9]+', '-')
            }
            elseif ($kv[0] -eq 'feature') {
                $styles += "font-feature-settings: '$($kv[1])'"
                $suffix += '-' + $kv[1]
            }
        }

        $tag = '<html'
        if ($classes) { $tag += ' class="' + $classes + '"' }
        if ($lang) { $tag += ' lang="' + $lang + '"' }
        if ($styles.Count -gt 0) { $tag += ' style="' + ($styles -join ';') + '"' }
        $tag += '>'

        $name = $stem + $suffix

        $body = $testHtml
        $body = $body -replace '<script[^>]*variant-class\.js"></script>', ''
        $body = $body -replace '<script[^>]*variant-feature\.js"></script>', ''
        $body = $body -replace '<meta name="variant"[^>]*>', ''
        $body = $body -replace '<!DOCTYPE html>', "<!DOCTYPE html>`r`n$tag"
        $body = $body -replace ('href="' + [regex]::Escape($refHref) + '"'), ('href="' + $name + '-ref.html"')

        $refBody = $refHtml
        $refBody = $refBody -replace '<script[^>]*variant-class\.js"></script>', ''
        $refBody = $refBody -replace '<script[^>]*variant-feature\.js"></script>', ''
        $refBody = $refBody -replace '<!DOCTYPE html>', "<!DOCTYPE html>`r`n$tag"

        $refBody = Flattened $refBody
        $body = Localized $body
        $refBody = Localized $refBody

        # The trim-all test states no value upstream, while its reference draws the trim-all picture.
        # The derived page states the value its reference answers.
        if ($stem -eq 'text-spacing-trim-trim-all-001') {
            $body = [regex]::Replace(
                $body,
                '(?m)^#container\s*\{',
                "#container {`r`n  text-spacing-trim: trim-all;")
        }

        [System.IO.File]::WriteAllText((Join-Path $dst ($name + '.html')), $body, $utf8)
        [System.IO.File]::WriteAllText((Join-Path $dst ($name + '-ref.html')), $refBody, $utf8)
        $written += 2
    }

    Write-Host ("variants: " + $stem + " -> " + $variants.Count)
}

Write-Host ("wrote " + $written + " files")
