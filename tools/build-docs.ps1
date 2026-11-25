<#
  Builds the SP COBOL language reference (GitHub Pages site in docs\) from docs-src\.

  Usage:   powershell -ExecutionPolicy Bypass -File tools\build-docs.ps1

  docs-src\nav.txt          page order: "# Section" lines, then one page name per line
  docs-src\pages\NAME.html  one page: "key: value" lines (title, summary, keywords, badge),
                            a line "---", then the page body as HTML.
                            <!-- example X --> inserts docs-src\examples\X.cbl and the output
                            tools\check-doc-examples.ps1 saved for it.
                            In <pre class="syntax">, lowercase words become <var> (the parts
                            you supply); UPPERCASE words are COBOL words.
  docs-src\assets\*         copied to docs\assets\
  Writes docs\*.html, docs\examples\*.cbl (downloadable) and docs\assets\search-index.js.
  Run tools\check-doc-examples.ps1 first when an example changed.
#>
$ErrorActionPreference = 'Stop'
$proj   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $proj 'docs-src'
$outDir = Join-Path $proj 'docs'
$exDir  = Join-Path $src 'examples'
$utf8   = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$p) { [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8) }
function Write-Text([string]$p, [string]$t) { [IO.File]::WriteAllText($p, $t.Replace("`r`n", "`n"), $utf8) }
function Enc([string]$s) { [Net.WebUtility]::HtmlEncode($s) }

New-Item -ItemType Directory -Force $outDir, (Join-Path $outDir 'assets'), (Join-Path $outDir 'examples') | Out-Null

# ---- page order -------------------------------------------------------------------------
$nav = New-Object System.Collections.ArrayList   # @{Section; Name}
$section = ''
foreach ($line in (Read-Text (Join-Path $src 'nav.txt')) -split "`r?`n") {
    $l = $line.Trim()
    if (-not $l) { continue }
    if ($l.StartsWith('#')) { $section = $l.TrimStart('#').Trim(); continue }
    [void]$nav.Add(@{ Section = $section; Name = $l })
}

# ---- read pages --------------------------------------------------------------------------
$pages = @{}
$missing = @($nav | Where-Object { -not (Test-Path (Join-Path $src "pages\$($_.Name).html")) })
foreach ($item in $missing) { Write-Warning "nav.txt names $($item.Name), but pages\$($item.Name).html is missing (left out)"; [void]$nav.Remove($item) }
foreach ($item in $nav) {
    $f = Join-Path $src "pages\$($item.Name).html"
    $text = (Read-Text $f).Replace("`r`n", "`n")
    $cut = $text.IndexOf("`n---`n")
    if ($cut -lt 0) { throw "$f has no '---' line after its header" }
    $meta = @{ title = $item.Name; summary = ''; keywords = ''; badge = '' }
    foreach ($h in $text.Substring(0, $cut) -split "`n") {
        if ($h -match '^(\w+):\s*(.*)$') { $meta[$Matches[1]] = $Matches[2].Trim() }
    }
    $pages[$item.Name] = @{ Meta = $meta; Body = $text.Substring($cut + 5); Section = $item.Section }
}
foreach ($f in Get-ChildItem (Join-Path $src 'pages') -Filter *.html) {
    if (-not $pages.ContainsKey($f.BaseName)) { Write-Warning "$($f.Name) is not listed in nav.txt" }
}

# ---- COBOL source to highlighted HTML ---------------------------------------------------
$kwList = @'
ACCEPT ADD ALLOCATE ALTER CALL CANCEL CLOSE COMPUTE CONTINUE COPY DELETE DISPLAY DIVIDE ENTRY
EVALUATE EXIT FREE GENERATE GO GOBACK IF ELSE INITIALIZE INITIATE INSPECT JSON MERGE MOVE MULTIPLY
OPEN PERFORM READ RELEASE REPLACE RETURN REWRITE SEARCH SET SORT START STOP STRING SUBTRACT
SUPPRESS TERMINATE UNLOCK UNSTRING USE WRITE XML WHEN OTHER THEN
END-ACCEPT END-ADD END-CALL END-COMPUTE END-DELETE END-DISPLAY END-DIVIDE END-EVALUATE END-IF
END-MULTIPLY END-PERFORM END-READ END-RETURN END-REWRITE END-SEARCH END-START END-STRING
END-SUBTRACT END-UNSTRING END-WRITE END-XML END-JSON END-PROGRAM
IDENTIFICATION ENVIRONMENT DATA PROCEDURE DIVISION SECTION PROGRAM-ID CONFIGURATION
SPECIAL-NAMES INPUT-OUTPUT FILE-CONTROL I-O-CONTROL WORKING-STORAGE LOCAL-STORAGE LINKAGE FILE
SCREEN REPORT FD SD RD SELECT DECLARATIVES END RUN
'@ -split '\s+' | Where-Object { $_ }
$kw = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($k in $kwList) { [void]$kw.Add($k) }

function Format-Words([string]$code) {
    # $code holds no strings or comments: mark the verbs and division words
    $sb = New-Object Text.StringBuilder
    foreach ($m in [regex]::Matches($code, '[A-Za-z0-9][A-Za-z0-9-]*|[^A-Za-z0-9]+')) {
        $t = $m.Value
        if ($kw.Contains($t.ToUpper()) -and $t -cmatch '^[A-Z]') { [void]$sb.Append("<span class=""kw"">$(Enc $t)</span>") }
        else { [void]$sb.Append((Enc $t)) }
    }
    $sb.ToString()
}

function Format-Cobol([string]$source) {
    $lines = $source.Replace("`r`n", "`n").TrimEnd("`n") -split "`n"
    $html = foreach ($line in $lines) {
        if ($line.Length -lt 7) { "<span class=""seq"">$(Enc $line)</span>"; continue }
        $seq = $line.Substring(0, 6); $ind = $line.Substring(6, 1); $rest = $line.Substring(7)
        $h = "<span class=""seq"">$(Enc $seq)</span>"
        if ($ind -eq '*' -or $ind -eq '/') { "$h<span class=""com"">$(Enc ($ind + $rest))</span>"; continue }
        $h += Enc $ind
        # split the rest into strings, a floating comment, and code
        $i = 0; $sb = New-Object Text.StringBuilder; $code = New-Object Text.StringBuilder
        while ($i -lt $rest.Length) {
            $c = $rest[$i]
            if ($c -eq '"' -or $c -eq "'") {
                $j = $rest.IndexOf($c, $i + 1)
                while ($j -ge 0 -and $j + 1 -lt $rest.Length -and $rest[$j + 1] -eq $c) { $j = $rest.IndexOf($c, $j + 2) }
                if ($j -lt 0) { $j = $rest.Length - 1 }
                [void]$sb.Append((Format-Words $code.ToString())); [void]$code.Clear()
                [void]$sb.Append("<span class=""str"">$(Enc $rest.Substring($i, $j - $i + 1))</span>")
                $i = $j + 1; continue
            }
            if ($c -eq '*' -and $i + 1 -lt $rest.Length -and $rest[$i + 1] -eq '>') {
                [void]$sb.Append((Format-Words $code.ToString())); [void]$code.Clear()
                [void]$sb.Append("<span class=""com"">$(Enc $rest.Substring($i))</span>")
                $i = $rest.Length; continue
            }
            [void]$code.Append($c); $i++
        }
        [void]$sb.Append((Format-Words $code.ToString()))
        $h + $sb.ToString()
    }
    $html -join "`n"
}

function Format-Syntax([string]$inner) {
    # lowercase words are the parts the programmer supplies; a line starting "Format" is a label
    $lines = foreach ($l in $inner -split "`n") {
        if ($l -match '^Format\b') { "<span class=""fmt"">$l</span>" }
        else { [regex]::Replace($l, '(?<![\w&#-])([a-z][a-z0-9]*(?:-[a-z0-9]+)*)(?![\w;-])', '<var>$1</var>') }
    }
    $lines -join "`n"
}

function Get-Example([string]$name) {
    $cbl = Join-Path $exDir "$name.cbl"
    if (-not (Test-Path $cbl)) { throw "example $name is missing ($cbl)" }
    Copy-Item $cbl (Join-Path $outDir "examples\$name.cbl") -Force
    $expect = ''
    $ef = Join-Path $exDir "$name.expect"
    if (Test-Path $ef) { $expect = (Read-Text $ef).Trim().ToLower() }
    $parts = @("<figure class=""example""><figcaption><span>Example</span> <a href=""examples/$name.cbl"" download>$name.cbl</a></figcaption>")
    $parts += "<pre class=""cobol""><code>$(Format-Cobol (Read-Text $cbl))</code></pre>"
    $subDir = Join-Path $exDir "$name.sub"
    if (Test-Path $subDir) {
        foreach ($s in Get-ChildItem $subDir -Filter *.cbl) {
            Copy-Item $s.FullName (Join-Path $outDir "examples\$($s.Name)") -Force
            $parts += "<div class=""out-label"">The called program, <a href=""examples/$($s.Name)"" download>$($s.Name)</a></div>"
            $parts += "<pre class=""cobol""><code>$(Format-Cobol (Read-Text $s.FullName))</code></pre>"
        }
    }
    $inF = Join-Path $exDir "$name.in"
    if (Test-Path $inF) {
        $parts += "<div class=""out-label"">What the user types</div><pre class=""input"">$(Enc (Read-Text $inF).TrimEnd())</pre>"
    }
    $keysF = Join-Path $exDir "$name.keys"
    if (Test-Path $keysF) {
        $parts += "<div class=""out-label"">Keys pressed (for the test run)</div><pre class=""input"">$(Enc (Read-Text $keysF).TrimEnd())</pre>"
    }
    $outF = Join-Path $exDir "output\$name.out"
    if (-not (Test-Path $outF)) { throw "example $name has no output yet: run tools\check-doc-examples.ps1 $name" }
    $label = if ($expect -eq 'compile-error') { 'What the compiler says' }
             elseif (Test-Path $keysF) { 'The screens, as the test renderer prints them' }
             else { 'Output' }
    $parts += "<div class=""out-label"">$label</div><pre class=""output"">$(Enc (Read-Text $outF).TrimEnd())</pre>"
    $parts += '</figure>'
    $parts -join "`n"
}

# ---- page template -----------------------------------------------------------------------
$themeInit = "<script>(function(){try{var t=localStorage.getItem('spcobol-theme');if(t==='light'||t==='dark')document.documentElement.setAttribute('data-theme',t);}catch(e){}})();</script>"

function Build-Sidebar([string]$current) {
    $sb = New-Object Text.StringBuilder
    $sec = $null
    foreach ($item in $nav) {
        if ($item.Name -eq 'index') { continue }
        if ($item.Section -ne $sec) {
            if ($sec -ne $null) { [void]$sb.Append("</ul></details>`n") }
            $sec = $item.Section
            $open = ($pages[$current].Section -eq $sec -or $current -eq 'index')
            $o = if ($open) { ' open' } else { '' }
            [void]$sb.Append("<details$o><summary>$(Enc $sec)</summary><ul>`n")
        }
        $t = Enc $pages[$item.Name].Meta.title
        $cls = if ($item.Name -eq $current) { ' class="current" aria-current="page"' } else { '' }
        [void]$sb.Append("<li><a href=""$($item.Name).html""$cls>$t</a></li>`n")
    }
    [void]$sb.Append("</ul></details>`n")
    $sb.ToString()
}

function Build-PageList {
    # <!-- page-list -->: every page as a card, by section
    $sb = New-Object Text.StringBuilder
    $sec = $null
    foreach ($item in $nav) {
        if ($item.Name -eq 'index') { continue }
        if ($item.Section -ne $sec) {
            if ($sec -ne $null) { [void]$sb.Append("</ul>`n") }
            $sec = $item.Section
            $id = ($sec.ToLower() -replace '[^a-z0-9]+', '-')
            [void]$sb.Append("<h2 id=""$id"">$(Enc $sec)</h2>`n<ul class=""cards"">`n")
        }
        $mm = $pages[$item.Name].Meta
        [void]$sb.Append("<li><a href=""$($item.Name).html""><b>$(Enc $mm.title)</b><small>$(Enc $mm.summary)</small></a></li>`n")
    }
    [void]$sb.Append("</ul>`n")
    $sb.ToString()
}

$index = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $nav.Count; $i++) {
    $name = $nav[$i].Name
    $p = $pages[$name]; $m = $p.Meta
    $body = $p.Body.Replace('<!-- page-list -->', (Build-PageList))
    $body = [regex]::Replace($body, '<!--\s*example\s+(\S+)\s*-->', { param($x) Get-Example $x.Groups[1].Value })
    $body = [regex]::Replace($body, '(?s)(<pre class="syntax">)(.*?)(</pre>)', { param($x) $x.Groups[1].Value + (Format-Syntax $x.Groups[2].Value) + $x.Groups[3].Value })

    $prev = if ($i -gt 0) { $nav[$i - 1].Name } else { $null }
    $next = if ($i + 1 -lt $nav.Count) { $nav[$i + 1].Name } else { $null }
    $pn = '<nav class="prevnext" aria-label="Previous and next page">'
    if ($prev) { $pn += "<a class=""prev"" href=""$prev.html"">&larr; $(Enc $pages[$prev].Meta.title)</a>" } else { $pn += '<span></span>' }
    if ($next) { $pn += "<a class=""next"" href=""$next.html"">$(Enc $pages[$next].Meta.title) &rarr;</a>" }
    $pn += '</nav>'

    $title = if ($name -eq 'index') { 'SP COBOL Language Reference' } else { "$($m.title) - SP COBOL Reference" }
    $badge = switch ($m.badge) {
        'not-supported' { '<span class="badge bad">Not supported</span>' }
        'differs'       { '<span class="badge warn">Differs from GnuCOBOL</span>' }
        'obsolete'      { '<span class="badge warn">Obsolete</span>' }
        default         { '' }
    }
    $crumb = if ($name -eq 'index') { '' } else { "<p class=""crumb"">$(Enc $p.Section)</p>" }
    $h1 = if ($name -eq 'index') { '' } else { "<h1>$(Enc $m.title) $badge</h1>`n" + $(if ($m.summary) { "<p class=""lede"">$(Enc $m.summary)</p>" } else { '' }) }

    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Enc $title)</title>
<meta name="description" content="$(Enc $m.summary)">
$themeInit
<link rel="stylesheet" href="assets/site.css">
</head>
<body>
<a class="skip" href="#main">Skip to the page</a>
<header class="top">
  <button class="menu-btn" type="button" aria-label="Show the page list" aria-expanded="false">&#9776;</button>
  <a class="brand" href="index.html">SP COBOL <span>Language Reference</span></a>
  <div class="search">
    <input type="search" id="q" placeholder="Search statements, clauses..." autocomplete="off" aria-label="Search the reference">
    <ul id="results" hidden></ul>
  </div>
  <button class="theme-btn" type="button" aria-label="Switch between light and dark"><span class="sun">&#9728;</span><span class="moon">&#9790;</span></button>
</header>
<div class="layout">
<nav class="side" aria-label="All pages">
<a class="home" href="index.html">Overview</a>
$(Build-Sidebar $name)
</nav>
<main id="main">
$crumb
$h1
$body
$pn
<footer><p>SP COBOL is free software: <a href="https://github.com/JohnSnape/sp-cobol">github.com/JohnSnape/sp-cobol</a>. Every example on these pages is compiled and run by <code>tools/check-doc-examples.ps1</code>, and its output is compared with GnuCOBOL 3.2.</p></footer>
</main>
</div>
<script src="assets/site.js"></script>
</body>
</html>
"@
    Write-Text (Join-Path $outDir "$name.html") $html

    # search entry: plain text of the page
    $plain = [regex]::Replace($p.Body, '<!--.*?-->', ' ')
    $plain = [regex]::Replace($plain, '<[^>]+>', ' ')
    $plain = [Net.WebUtility]::HtmlDecode($plain)
    $plain = [regex]::Replace($plain, '\s+', ' ').Trim()
    [void]$index.Add([ordered]@{ t = $m.title; u = "$name.html"; s = $m.summary; k = $m.keywords; c = $p.Section; x = $plain })
}

Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 50MB
Write-Text (Join-Path $outDir 'assets\search-index.js') ("window.SPCOBOL_INDEX=" + $ser.Serialize($index) + ";`n")
Get-ChildItem (Join-Path $src 'assets') -File | Copy-Item -Destination (Join-Path $outDir 'assets') -Force
Write-Text (Join-Path $outDir '.nojekyll') ''
"wrote $($nav.Count) pages to docs\"
