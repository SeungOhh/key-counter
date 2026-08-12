# 32x32 sprite sheet. Only the background CONNECTED TO THE OUTER EDGE is made transparent,
# so cream-coloured areas inside the cat (chest, paws) survive.
# Last frame is the sitting pose, used when nothing is being typed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$helper = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Knock {
    // Flood fill inward from every border pixel that matches the corner colour.
    // Anything the fill cannot reach - e.g. the cat's cream chest - stays opaque.
    public static Bitmap Outside(string path, int tol) {
        Bitmap src = new Bitmap(path);
        Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b)) {
            g.DrawImage(src, 0, 0, src.Width, src.Height);
        }
        src.Dispose();

        int w = b.Width, h = b.Height;
        BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] px = new byte[stride * h];
        Marshal.Copy(d.Scan0, px, 0, px.Length);

        byte bR = px[2], bG = px[1], bB = px[0];
        bool[] seen = new bool[w * h];
        Stack<int> st = new Stack<int>();

        for (int x = 0; x < w; x++) { Seed(px, stride, seen, st, w, x, 0, bR, bG, bB, tol); Seed(px, stride, seen, st, w, x, h - 1, bR, bG, bB, tol); }
        for (int y = 0; y < h; y++) { Seed(px, stride, seen, st, w, 0, y, bR, bG, bB, tol); Seed(px, stride, seen, st, w, w - 1, y, bR, bG, bB, tol); }

        while (st.Count > 0) {
            int p = st.Pop();
            int x = p % w, y = p / w;
            if (x > 0)     Seed(px, stride, seen, st, w, x - 1, y, bR, bG, bB, tol);
            if (x < w - 1) Seed(px, stride, seen, st, w, x + 1, y, bR, bG, bB, tol);
            if (y > 0)     Seed(px, stride, seen, st, w, x, y - 1, bR, bG, bB, tol);
            if (y < h - 1) Seed(px, stride, seen, st, w, x, y + 1, bR, bG, bB, tol);
        }

        // clear alpha but keep RGB, so the later downsample blends edges against the
        // original cream instead of against black
        for (int i = 0; i < seen.Length; i++) {
            if (!seen[i]) continue;
            int o = (i / w) * stride + (i % w) * 4;
            px[o + 3] = 0;
        }

        Marshal.Copy(px, 0, d.Scan0, px.Length);
        b.UnlockBits(d);
        return b;
    }

    static void Seed(byte[] px, int stride, bool[] seen, Stack<int> st, int w,
                     int x, int y, byte bR, byte bG, byte bB, int tol) {
        int i = y * w + x;
        if (seen[i]) return;
        int o = y * stride + x * 4;
        int diff = Math.Abs(px[o + 2] - bR) + Math.Abs(px[o + 1] - bG) + Math.Abs(px[o] - bB);
        if (diff > tol) return;
        seen[i] = true;
        st.Push(i);
    }
}
"@
Add-Type -TypeDefinition $helper -ReferencedAssemblies System.Drawing

$srcDir = 'C:\Users\osy04\Desktop\html\keyboard_counter\src'
$work   = 'C:\Users\osy04\AppData\Local\Temp\claude\c--Users-osy04-Desktop-html-keyboard-counter\27084091-1e08-4568-b6e8-5f55997701e1\scratchpad'
$FW = 32; $FH = 32
$TOL = 60

# numbered files are the run cycle, in numeric order; sitting and sleep go last
$run = @(Get-ChildItem $srcDir -File -Filter *.png |
         Where-Object { $_.BaseName -match '^\d+$' } |
         Sort-Object { [int]$_.BaseName })
$sit = Get-ChildItem $srcDir -File -Filter 'sitting.png'
$slp = Get-ChildItem $srcDir -File -Filter 'sleep.png'
if ($null -eq $sit) { throw "sitting.png not found" }
if ($null -eq $slp) { throw "sleep.png not found" }
$all = @($run) + @($sit) + @($slp)
Write-Host ("{0} run frames + sitting + sleep" -f $run.Count)

$sheet = New-Object System.Drawing.Bitmap ($FW * $all.Count), $FH, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::Transparent)
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$i = 0
foreach ($f in $all) {
    $cut = [Knock]::Outside($f.FullName, $TOL)
    $sg.DrawImage($cut, (New-Object System.Drawing.Rectangle ($i * $FW), 0, $FW, $FH),
                  0, 0, $cut.Width, $cut.Height, ([System.Drawing.GraphicsUnit]::Pixel))
    Write-Host ("  [{0}] {1}" -f $i, $f.Name)
    $cut.Dispose()
    $i++
}
$sg.Dispose()

$png = Join-Path $work 'cat32c.png'
$sheet.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)

# preview on both a dark and a light backdrop so leftover halos are obvious
$prev = New-Object System.Drawing.Bitmap ($sheet.Width * 5), ($FH * 5 * 2 + 60)
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 12, 14, 16))
$pg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$pg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$lab = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$fnt = New-Object System.Drawing.Font 'Consolas', 13, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$pg.DrawString('on dark panel (x5)  - last two tiles: sitting, sleep', $fnt, $lab, 6, 4)
$pg.DrawImage($sheet, 0, 22, $sheet.Width * 5, $FH * 5)
$y2 = $FH * 5 + 32
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,120,130,150))), 0, ($y2 + 18), ($sheet.Width * 5), ($FH * 5))
$pg.DrawString('on grey (checks for halos)', $fnt, $lab, 6, $y2)
$pg.DrawImage($sheet, 0, ($y2 + 18), $sheet.Width * 5, $FH * 5)
$pg.Dispose()
$prev.Save((Join-Path $work 'cat32c_preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$prev.Dispose()
$sheet.Dispose()

$bytes = [System.IO.File]::ReadAllBytes($png)
$b64 = [System.Convert]::ToBase64String($bytes)
Write-Host ("sheet {0} bytes -> base64 {1} chars, {2} frames of {3}x{4}" -f $bytes.Length, $b64.Length, $all.Count, $FW, $FH)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("        const int SPRITE_W = $FW;")
[void]$sb.AppendLine("        const int SPRITE_H = $FH;")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("        static readonly string SPRITE_PNG =")
for ($p = 0; $p -lt $b64.Length; $p += 110) {
    $chunk = $b64.Substring($p, [math]::Min(110, $b64.Length - $p))
    $term = if (($p + 110) -ge $b64.Length) { ';' } else { ' +' }
    [void]$sb.AppendLine('            "' + $chunk + '"' + $term)
}
Set-Content -Path (Join-Path $work 'sprite32c.cs.txt') -Value $sb.ToString() -Encoding utf8
Write-Host ("run = 0..{0}, sitting = {1}, sleep = {2}" -f ($run.Count - 1), $run.Count, ($run.Count + 1))

