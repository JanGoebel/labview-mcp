<#
.SYNOPSIS
Regenerates src\LabVIEWMCP\Infra\IconFont.cs, the glyph table lvai_set_vi_icon draws text with,
from the Windows raster font "Small Fonts" at 7 px.

.DESCRIPTION
LabVIEW's icon editor draws icon text in "Small Fonts" at 7 px, so practically every hand-made
LabVIEW icon uses it. IconImage draws with no imaging dependency, so the glyphs are embedded as a
table, and this script is where that table comes from: it asks GDI for the font, draws every
character on its own with TextOut and records the character cell - advance width by 8 rows -
exactly as GDI drew it.

GDI, not GDI+. System.Drawing.Font cannot use a raster font and silently substitutes Microsoft
Sans Serif for "Small Fonts" (measured 2026-10-20, Windows 11 24H2). CreateFontW with height -7,
weight 400 and the face name does select it, and GetTextFaceW confirms the face; the script
refuses to write a table from any other face. The P/Invoke declarations need CharSet.Unicode,
otherwise GetTextFace returns only "S" and the check cannot work.

Run it with Windows PowerShell 5.1 on Windows, from anywhere:

    powershell -ExecutionPolicy Bypass -File scripts\Export-IconFont.ps1          # rewrite
    powershell -ExecutionPolicy Bypass -File scripts\Export-IconFont.ps1 -Check   # compare only

-Check exits 1 when the committed table differs from what this machine's font draws.
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\LabVIEWMCP\Infra\IconFont.cs'),
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

# Printable ASCII. Each glyph's ink stays inside rows 2..7 of the 8-row cell (checked below), which
# is what lets IconImage stack three body lines 6 rows apart. Latin-1 is left out on purpose:
# accented capitals reach into rows 0..1 and would collide with the line above.
$alphabet = -join (32..126 | ForEach-Object { [char]$_ })

Add-Type -AssemblyName System.Drawing
if (-not ('IconFontExport' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Runtime.InteropServices; using System.Text;
public static class IconFontExport {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TEXTMETRICW {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth,
                   tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int e, int o, int wt, uint i, uint u, uint s, uint cs, uint op, uint cp, uint q, uint pf, string face);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(IntPtr dc, int x, int y, string s, int n);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int m);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetTextFaceW(IntPtr dc, int n, StringBuilder sb);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetCharWidth32W(IntPtr dc, uint first, uint last, int[] w);

    // Margin around the cell, so ink outside it would be seen rather than clipped.
    const int M = 8;

    static IntPtr Font() { return CreateFontW(-7, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 3, 0, "Small Fonts"); }

    // Face name, cell height and ascent of the font GDI actually selected.
    public static string Metrics() {
        using (var b = new Bitmap(4, 4)) using (var g = Graphics.FromImage(b)) {
            IntPtr dc = g.GetHdc(); IntPtr f = Font(); IntPtr old = SelectObject(dc, f);
            try {
                var face = new StringBuilder(64); GetTextFaceW(dc, 64, face);
                TEXTMETRICW tm; GetTextMetricsW(dc, out tm);
                return face + "|" + tm.tmHeight + "|" + tm.tmAscent;
            } finally { SelectObject(dc, old); DeleteObject(f); g.ReleaseHdc(dc); }
        }
    }

    // One character's cell as rows of '#' and '.', advance-width wide; "!" + reason if it has
    // ink outside the cell.
    public static string[] Cell(char c, int height) {
        using (var b = new Bitmap(2 * M + 16, 2 * M + height)) {
            int adv;
            using (var g = Graphics.FromImage(b)) {
                g.Clear(Color.White);
                IntPtr dc = g.GetHdc(); IntPtr f = Font(); IntPtr old = SelectObject(dc, f);
                try {
                    SetBkMode(dc, 1);
                    var w = new int[1]; GetCharWidth32W(dc, c, c, w); adv = w[0];
                    TextOutW(dc, M, M, c.ToString(), 1);
                } finally { SelectObject(dc, old); DeleteObject(f); g.ReleaseHdc(dc); }
            }
            for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) {
                bool inside = x >= M && x < M + adv && y >= M && y < M + height;
                if (!inside && b.GetPixel(x, y).R < 128) return new[] { "!ink outside the cell" };
            }
            var rows = new string[height];
            for (int y = 0; y < height; y++) {
                var l = new StringBuilder();
                for (int x = 0; x < adv; x++) l.Append(b.GetPixel(M + x, M + y).R < 128 ? '#' : '.');
                rows[y] = l.ToString();
            }
            return rows;
        }
    }
}
'@
}

$face, $height, $ascent = [IconFontExport]::Metrics().Split('|')
if ($face -ne 'Small Fonts') { throw "GDI selected '$face', not 'Small Fonts'. Refusing to write a table from it." }
if ($height -ne '8' -or $ascent -ne '7') {
    throw "Small Fonts at -7 has a cell of $height rows with ascent $ascent here; IconImage assumes 8 and 7."
}

$entries = foreach ($c in $alphabet.ToCharArray()) {
    $rows = [IconFontExport]::Cell($c, 8)
    if ($rows[0].StartsWith('!')) { throw "'$c': $($rows[0].Substring(1))" }
    if (($rows[0] + $rows[1]) -match '#') { throw "'$c' has ink above the cap height (rows 0..1)." }
    $literal = switch ($c) { "'" { "'\''" } '\' { "'\\'" } default { "'$c'" } }
    # Rows 2..7 only: cap height 5 plus one descender row. Rows 0..1 were just checked empty.
    '        [{0}] = "{1}",' -f $literal, ($rows[2..7] -join '|')
}

$header = @"
// <auto-generated>
// Written by scripts\Export-IconFont.ps1 - do not edit by hand; rerun the script instead.
// </auto-generated>
namespace LabVIEWMcp.Infra;

/// <summary>
/// The glyphs of the Windows raster font "Small Fonts" at 7 px (GDI CreateFontW height -7, weight
/// 400), the font LabVIEW's icon editor writes icon text in. Drawn one character at a time by GDI
/// TextOut and recorded as the character cell: the string is the cell's rows 2..7 joined by '|',
/// each row as wide as the character's advance, '#' ink and '.' paper. Rows 0..1 of the 8-row cell
/// are empty for every character here (the generator checks it), row 2 is the cap height and row 6
/// the baseline, so row 7 is the one descender row.
///
/// Derived from Microsoft's "Small Fonts" (SMALLE.FON, shipped with Windows); the .fon file itself
/// is not part of this repository.
/// </summary>
internal static class IconFont
{
    /// <summary>Rows per glyph: cap height 5 plus one descender row.</summary>
    internal const int Rows = 6;

    internal static readonly IReadOnlyDictionary<char, string> Glyphs = new Dictionary<char, string>
    {
"@

$text = ($header -replace "`r`n", "`n") + "`n" + ($entries -join "`n") + "`n    };`n}`n"

if ($Check) {
    $current = if (Test-Path $OutFile) { [IO.File]::ReadAllText($OutFile) -replace "`r`n", "`n" } else { '' }
    if ($current -ne $text) { Write-Output "IconFont.cs differs from what Small Fonts draws on this machine."; exit 1 }
    Write-Output "IconFont.cs matches ($($alphabet.Length) glyphs)."
    exit 0
}

[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $text, (New-Object Text.UTF8Encoding $false))
Write-Output "Wrote $($alphabet.Length) glyphs to $([IO.Path]::GetFullPath($OutFile))"
