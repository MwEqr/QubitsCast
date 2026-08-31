# Fotografa uma janela pelo titulo, SEM traze-la para frente.
# Usa PrintWindow com PW_RENDERFULLCONTENT, que pede a propria janela para se desenhar
# num bitmap - funciona mesmo com ela coberta ou em segundo plano, e assim nao rouba
# a tela de quem esta usando o computador.
#
#   powershell -ExecutionPolicy Bypass -File ativos\fotografar-janela.ps1 QubitsCast saida.png

param(
    [Parameter(Mandatory = $true)][string]$Titulo,
    [Parameter(Mandatory = $true)][string]$Destino,
    [int]$Indice = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Janelas {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT valor, int tam);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder texto, int max);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(Proc proc, IntPtr dados);
    private delegate bool Proc(IntPtr hwnd, IntPtr dados);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // MainWindowHandle costuma apontar para uma janela auxiliar em programas WPF,
    // entao a busca e por todas as janelas visiveis com titulo.
    public static List<IntPtr> Visiveis() {
        var achadas = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) achadas.Add(h); return true; }, IntPtr.Zero);
        return achadas;
    }
}
'@

$candidatas = @(foreach ($h in [Janelas]::Visiveis()) {
    $sb = New-Object System.Text.StringBuilder 512
    [void][Janelas]::GetWindowTextW($h, $sb, 512)
    $texto = $sb.ToString()
    if ($texto -notlike "*$Titulo*") { continue }

    # A borda real vem do DWM; GetWindowRect inclui a sombra invisivel. Mas com a janela
    # em segundo plano o DWM as vezes devolve uma medida minuscula, e ai o GetWindowRect
    # e quem esta certo - por isso o maior dos dois vale.
    $medida = New-Object Janelas+RECT
    $doDwm = ([Janelas]::DwmGetWindowAttribute($h, 9, [ref]$medida, 16) -eq 0)
    $l = $medida.Right - $medida.Left
    $a = $medida.Bottom - $medida.Top

    if (-not $doDwm -or $l -lt 200 -or $a -lt 200) {
        $bruta = New-Object Janelas+RECT
        if ([Janelas]::GetWindowRect($h, [ref]$bruta)) {
            $lb = $bruta.Right - $bruta.Left
            $ab = $bruta.Bottom - $bruta.Top
            if ($lb * $ab -gt $l * $a) { $medida = $bruta; $l = $lb; $a = $ab }
        }
    }
    if ($l -le 0 -or $a -le 0) { continue }

    $pid_ = 0
    [void][Janelas]::GetWindowThreadProcessId($h, [ref]$pid_)
    [pscustomobject]@{ Handle = $h; Titulo = $texto; Largura = $l; Altura = $a; Area = $l * $a; Pid = $pid_ }
})

$candidatas = @($candidatas | Sort-Object Area -Descending)
if ($candidatas.Count -eq 0) { Write-Error "nenhuma janela visivel com titulo parecido com '$Titulo'"; exit 1 }
if ($Indice -ge $candidatas.Count) { Write-Error "so ha $($candidatas.Count) janela(s)"; exit 1 }

$alvo = $candidatas[$Indice]
$hwnd = $alvo.Handle
Write-Host "janela: '$($alvo.Titulo)' (pid $($alvo.Pid), $($alvo.Largura)x$($alvo.Altura))"

# A borda real da janela vem do DWM; GetWindowRect inclui a sombra invisivel.
$r = New-Object Janelas+RECT
if ([Janelas]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) -ne 0) {
    [void][Janelas]::GetWindowRect($hwnd, [ref]$r)
}
$largura = $r.Right - $r.Left
$altura = $r.Bottom - $r.Top
if ($largura -le 0 -or $altura -le 0) { Write-Error "janela sem tamanho"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($largura, $altura)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# 2 = PW_RENDERFULLCONTENT, necessario para janelas desenhadas por composicao (WPF).
$ok = [Janelas]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

if (-not $ok) { Write-Warning "PrintWindow recusou; a imagem pode sair vazia" }

$pasta = Split-Path -Parent $Destino
if ($pasta -and -not (Test-Path $pasta)) { New-Item -ItemType Directory -Force $pasta | Out-Null }
$bmp.Save($Destino, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "gravado: $Destino ($largura x $altura)"
