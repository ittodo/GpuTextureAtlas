param([string]$Destination = (Join-Path $PSScriptRoot '..\Benchmark\Sources'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$sources = @()
foreach ($assetId in @('brick_wall_001', 'wood_floor_worn', 'aerial_rocks_02', 'fabric_pattern_07', 'brown_leather', 'green_metal_rust', 'grass_ground', 'grey_plaster', 'blue_floor_tiles_01')) {
    $metadataPath = Join-Path $Destination "$assetId.json"
    if (-not (Test-Path -LiteralPath $metadataPath)) {
        Invoke-WebRequest -Uri "https://api.polyhaven.com/files/$assetId" -OutFile $metadataPath
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $diffuse = $metadata.PSObject.Properties | Where-Object Name -Match '^(diff|col)' | Select-Object -First 1
    $image = $diffuse.Value.'1k'.png
    $path = Join-Path $Destination "$assetId.png"
    if (-not (Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $image.url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm MD5).Hash.ToLowerInvariant() -ne $image.md5) { throw "Source checksum differs: $assetId" }
    $sources += [ordered]@{ id = $assetId; kind = 'photographic PBR diffuse'; license = 'CC0'; page = "https://polyhaven.com/a/$assetId"; url = $image.url; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

# A code-authored UI atlas provides small text, saturated edges, alpha gradients, and transparent borders.
# This is a controlled UI test source, not a photorealistic material or a normal map.
Add-Type -AssemblyName System.Drawing
$atlas = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($atlas)
$font = [System.Drawing.Font]::new('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255), 2)
try {
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    for ($row = 0; $row -lt 6; $row++) {
        for ($column = 0; $column -lt 6; $column++) {
            $x = 12 + $column * 170; $y = 12 + $row * 170
            $rect = [System.Drawing.Rectangle]::new($x, $y, 144, 138)
            $startColor = [System.Drawing.Color]::FromArgb(64 + $row * 35, 30 + $column * 40, 220 - $row * 30, 45 + $row * 30)
            $endColor = [System.Drawing.Color]::FromArgb(255, 240 - $row * 30, 40 + $row * 25, 240 - $column * 30)
            $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $startColor, $endColor, 30.0)
            try {
                $graphics.FillEllipse($brush, $rect)
                $graphics.DrawEllipse($pen, $rect)
                $graphics.DrawLine($pen, $x + 15, $y + 20, $x + 124, $y + 115)
                $graphics.DrawLine($pen, $x + 124, $y + 20, $x + 15, $y + 115)
                $graphics.DrawString("ITEM $row$column", $font, [System.Drawing.Brushes]::White, $x + 23, $y + 56)
                $graphics.DrawString('HP 123/999', $font, [System.Drawing.Brushes]::Orange, $x + 6, $y + 144)
            } finally { $brush.Dispose() }
        }
    }
    $atlas.Save((Join-Path $Destination 'ui_alpha_atlas.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $pen.Dispose(); $font.Dispose(); $graphics.Dispose(); $atlas.Dispose() }
$sources += [ordered]@{ id = 'ui_alpha_atlas'; kind = 'code-authored controlled RGBA UI atlas'; license = 'project-generated'; generator = 'Tools/Get-AstcBenchmarkTextures.ps1'; sha256 = (Get-FileHash -LiteralPath (Join-Path $Destination 'ui_alpha_atlas.png') -Algorithm SHA256).Hash }

# Pair each photograph with an independent alpha mask. These are controlled RGBA
# material/decal inputs; they are not photographs of naturally transparent objects.
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class AstcAlphaSources
{
    static double Clamp(double v) { return Math.Max(0, Math.Min(1, v)); }
    static double Edge(double distance, double width = .006) { return Clamp(.5 - distance / width); }
    public static void Create(string input, string output, int style)
    {
        using (var source = new Bitmap(input))
        using (var target = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(target)) g.DrawImage(source, 0, 0, 1024, 1024);
            var data = target.LockBits(new Rectangle(0,0,1024,1024), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var pixels = new byte[data.Stride * 1024]; Marshal.Copy(data.Scan0,pixels,0,pixels.Length);
            for(int y=0;y<1024;y++) for(int x=0;x<1024;x++)
            {
                double u=(x+.5)/1024.0,v=(y+.5)/1024.0,dx=u-.5,dy=v-.5;
                double radius=Math.Sqrt(dx*dx+dy*dy),angle=Math.Atan2(dy,dx),a=1;
                switch(style)
                {
                    case 0: // repeated leaf silhouettes with antialiased edges
                        double lx=(u*7)%1-.5,ly=(v*7)%1-.5;
                        a=Edge(Math.Sqrt(Math.Pow((lx+ly)*.707/.46,2)+Math.Pow((ly-lx)*.707/.19,2))-1,.06);break;
                    case 1: a=Edge(radius-(.31+.075*Math.Cos(angle*7)));break;
                    case 2: // a soft smoke-like mask; deterministic waves, no random seed
                        a=Clamp((.7-radius)*2)*Clamp(.55+.25*Math.Sin(u*31+Math.Sin(v*19)*2)+.2*Math.Cos(v*37+u*11));break;
                    case 3: // fine perforated material
                        a=Edge(.30-Math.Max(Math.Abs(Math.Sin(u*110)),Math.Abs(Math.Sin(v*100))),.12);break;
                    case 4: a=Edge(Math.Pow(Math.Abs(dx)/.41,6)+Math.Pow(Math.Abs(dy)/.35,6)-1,.035);break;
                    case 5: a=Clamp((u-.08)/.84)*Clamp(1-Math.Abs(v-.5)*2.4);break;
                    case 6: // thin grass-like strokes
                        a=Edge(Math.Abs(Math.Sin((u+.035*Math.Sin(v*18))*180))-(.07+.30*(1-v)),.14)*Edge(v-.92);break;
                    case 7: a=radius>.48?0:Math.Exp(-radius*radius*23)*Clamp((.48-radius)*24);break;
                    case 8: // small rings and a diagonal hard alpha edge
                        double ring=Math.Abs(Math.Sqrt(Math.Pow((u*9)%1-.5,2)+Math.Pow((v*9)%1-.5,2))-.30);
                        a=Edge(ring-.045,.025)*Edge(u+v-1.55);break;
                }
                int at=y*data.Stride+x*4;
                if(style==9) // opaque UI variant on a fixed dark backdrop
                {
                    double old=pixels[at+3]/255.0;
                    pixels[at]=(byte)Math.Round(pixels[at]*old+32*(1-old));
                    pixels[at+1]=(byte)Math.Round(pixels[at+1]*old+24*(1-old));
                    pixels[at+2]=(byte)Math.Round(pixels[at+2]*old+20*(1-old));
                    a=1;
                }
                pixels[at+3]=(byte)Math.Round(Clamp(a)*255);
            }
            Marshal.Copy(pixels,0,data.Scan0,pixels.Length);target.UnlockBits(data);
            target.Save(output,ImageFormat.Png);
        }
    }
    public static int[] SetGroupAndCount(Bitmap bitmap, bool alpha)
    {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        var bytes=new byte[data.Stride*bitmap.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);
        int zero=0,semi=0,opaque=0;
        for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)
        {
            int at=y*data.Stride+x*4+3;if(!alpha)bytes[at]=255;
            if(bytes[at]==0)zero++;else if(bytes[at]==255)opaque++;else semi++;
        }
        Marshal.Copy(bytes,0,data.Scan0,bytes.Length);bitmap.UnlockBits(data);return new[]{zero,semi,opaque};
    }
}
'@ -ReferencedAssemblies @('System.Drawing.Common','System.Drawing.Primitives','System.Private.Windows.GdiPlus','System.Private.Windows.Core','System.Runtime.InteropServices')
$photoSources = @($sources | Where-Object kind -eq 'photographic PBR diffuse')
$maskNames = @('leaves','lobed_cutout','soft_smoke','perforation','sticker','gradient','grass','glow','rings')
foreach ($i in @(0,2,5,6)) {
    $source=$photoSources[$i]; $id='rgba_'+$maskNames[$i]
    $path=Join-Path $Destination ($id+'.png')
    [AstcAlphaSources]::Create((Join-Path $Destination ($source.id+'.png')), $path, $i)
    $sources += [ordered]@{ id=$id; kind='photographic RGB with code-authored independent alpha mask'; license='CC0 RGB; project-generated alpha'; rgbSource=$source.id; generator='Tools/Get-AstcBenchmarkTextures.ps1'; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
foreach($entry in @(@('Small/LDR-RGBA/ldr-rgba-00.png','rgba_arm_00','Apache-2.0'),@('Small/LDR-RGBA/ldr-rgba-01.png','rgba_arm_01','Apache-2.0'),@('Small/LDR-RGBA/ldr-rgba-02.png','rgba_arm_02','Apache-2.0'),@('Khronos/LDR-RGBA/ldr-rgba-base.png','rgba_khronos_base','MIT'),@('Khronos/LDR-RGBA/ldr-rgba-diffuse.png','rgba_khronos_diffuse','MIT'))) {
    $url='https://raw.githubusercontent.com/ARM-software/astc-encoder/5.7.0/Test/Images/'+$entry[0]
    $path=Join-Path $Destination ($entry[1]+'.png')
    if(-not(Test-Path -LiteralPath $path)){Invoke-WebRequest -Uri $url -OutFile $path}
    $sources += [ordered]@{id=$entry[1];kind='official astcenc 5.7.0 RGBA test texture';license=$entry[2];url=$url;page='https://github.com/ARM-software/astc-encoder/tree/5.7.0/Test/Images';sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
}
$khronosLicense=Join-Path $Destination 'Khronos-LICENSE.txt'
if(-not(Test-Path -LiteralPath $khronosLicense)){Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ARM-software/astc-encoder/5.7.0/Test/Images/Khronos/LICENSE.txt' -OutFile $khronosLicense}
$opaqueUi = Join-Path $Destination 'ui_opaque_atlas.png'
[AstcAlphaSources]::Create((Join-Path $Destination 'ui_alpha_atlas.png'),$opaqueUi,9)
$sources += [ordered]@{id='ui_opaque_atlas';kind='code-authored opaque UI atlas';license='project-generated';generator='Tools/Get-AstcBenchmarkTextures.ps1';sha256=(Get-FileHash -LiteralPath $opaqueUi -Algorithm SHA256).Hash}
foreach($source in $sources) { $source['group']= if($source.id -eq 'ui_alpha_atlas' -or $source.id.StartsWith('rgba_')){'alpha'}else{'opaque'} }
$sources | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'sources.json') -Encoding utf8
$inputRoot = [IO.Path]::GetFullPath((Join-Path $Destination '..\Input'))
$inputs = @()
foreach ($source in $sources) {
    $image = [System.Drawing.Bitmap]::new((Join-Path $Destination ($source.id + '.png')))
    try {
        $cropSide = [Math]::Min($image.Width, $image.Height)
        foreach ($resolution in @(256, 512, 1024)) {
            $folder = Join-Path $inputRoot $resolution
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            $outputPath = Join-Path $folder ($source.id + '.png')
            $resized = [System.Drawing.Bitmap]::new($resolution, $resolution, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $canvas = [System.Drawing.Graphics]::FromImage($resized)
            try {
                $canvas.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $canvas.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $canvas.DrawImage($image, [System.Drawing.Rectangle]::new(0, 0, $resolution, $resolution),
                    [float](($image.Width - $cropSide) / 2), [float](($image.Height - $cropSide) / 2),
                    [float]$cropSide, [float]$cropSide, [System.Drawing.GraphicsUnit]::Pixel)
                $counts=[AstcAlphaSources]::SetGroupAndCount($resized,($source.group -eq 'alpha'))
                if($source.group -eq 'alpha' -and ($counts[0] -eq 0 -or $counts[1] -eq 0)){throw "RGBA input lacks transparent or semitransparent pixels: $($source.id)"}
                $resized.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $canvas.Dispose(); $resized.Dispose() }
            $inputs += [ordered]@{ texture=$source.id; group=$source.group; resolution=$resolution; sourceWidth=$image.Width; sourceHeight=$image.Height; transparentPixels=$counts[0]; semitransparentPixels=$counts[1]; opaquePixels=$counts[2]; sha256=(Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash }
        }
    } finally { $image.Dispose() }
}
$inputs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $inputRoot 'manifest.json') -Encoding utf8
Write-Output "Prepared $($sources.Count) benchmark textures."
