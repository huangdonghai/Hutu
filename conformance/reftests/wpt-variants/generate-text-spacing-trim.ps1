# Expands the variant-driven text-spacing-trim reftests into static pages.
#
# Each upstream test carries <meta name="variant" content="?class=halt,htb">, and support/variant-class.js
# turns that query into classes on the root element. This engine runs no script, so every one of those
# tests would render the default state whatever variant it was written for. Here the same attributes are
# written into the page itself: one page per variant, and a reference beside it carrying the same ones.

$src = 'D:\_git0\Hutu\build\wpt\css\css-text\text-spacing-trim'
$dst = 'D:\_git0\Hutu\conformance\reftests\wpt-variants\text-spacing-trim'
$utf8 = [System.Text.UTF8Encoding]::new($false)

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
        # A page that asks for no variant is already static: it is copied as it stands.
        [System.IO.File]::WriteAllText((Join-Path $dst ($stem + '.html')), $testHtml, $utf8)
        [System.IO.File]::WriteAllText((Join-Path $dst $refHref), $refHtml, $utf8)
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

        [System.IO.File]::WriteAllText((Join-Path $dst ($name + '.html')), $body, $utf8)
        [System.IO.File]::WriteAllText((Join-Path $dst ($name + '-ref.html')), $refBody, $utf8)
        $written += 2
    }

    Write-Host ("variants: " + $stem + " -> " + $variants.Count)
}

Write-Host ("wrote " + $written + " files")
