<#
.SYNOPSIS
    Gera o executavel do Monitor SVN sempre no mesmo lugar: publish\MonitorSVN.exe

.DESCRIPTION
    1. Roda os testes (aborta se algum falhar).
    2. Publica um unico .exe autocontido (win-x64, nao precisa de .NET instalado no destino).
    3. So entao copia para a pasta de saida, sem tocar em outros arquivos dela (ex.: .rar).
    A versao mostrada em "Sobre" muda a cada execucao (ano.mes.dia.hora+minuto).

.PARAMETER SemTestes
    Pula a execucao dos testes (nao recomendado).

.PARAMETER Fechar
    Se o MonitorSVN.exe da pasta de saida estiver aberto, encerra o processo para poder substitui-lo.
    Sem este parametro o script apenas avisa e para.

.PARAMETER Saida
    Pasta de saida. Padrao: publish (ao lado deste script).

.EXAMPLE
    .\publicar.ps1
    .\publicar.ps1 -Fechar
#>
[CmdletBinding()]
param(
    [switch]$SemTestes,
    [switch]$Fechar,
    [string]$Saida
)

$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
if (-not $Saida) { $Saida = Join-Path $raiz 'publish' }
$projeto = Join-Path $raiz 'MonitorSVN.Wpf\MonitorSVN.Wpf.csproj'
$testes = Join-Path $raiz 'MonitorSVN.Tests'
$exeFinal = Join-Path $Saida 'MonitorSVN.exe'

function Falhar([string]$mensagem) {
    Write-Host ''
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    exit 1
}

function Passo([string]$texto) {
    Write-Host ''
    Write-Host "== $texto" -ForegroundColor Cyan
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Falhar 'dotnet SDK nao encontrado no PATH.' }
if (-not (Test-Path $projeto)) { Falhar "Projeto nao encontrado: $projeto" }

# 1) o exe de saida nao pode estar em uso (o Windows nao deixa sobrescrever um exe em execucao)
$emUso = @(Get-Process -Name 'MonitorSVN' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $exeFinal } catch { $false }
})
if ($emUso.Count -gt 0) {
    $pids = ($emUso | ForEach-Object { $_.Id }) -join ', '
    if ($Fechar) {
        Passo "Encerrando MonitorSVN em execucao (PID $pids)"
        $emUso | Stop-Process -Force
        Start-Sleep -Seconds 1
    }
    else {
        Falhar "O MonitorSVN.exe de '$Saida' esta aberto (PID $pids). Feche-o, ou rode: .\publicar.ps1 -Fechar"
    }
}

# 2) testes
if ($SemTestes) {
    Write-Host 'Testes pulados (-SemTestes).' -ForegroundColor Yellow
}
else {
    Passo 'Rodando os testes'
    & dotnet test $testes --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Falhar 'Testes falharam. Nada foi publicado.' }
}

# 3) publicacao em pasta temporaria (a pasta de saida so e alterada se tudo deu certo)
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('msvn-publish-' + [guid]::NewGuid().ToString('N'))
try {
    Passo 'Gerando o executavel'
    & dotnet publish $projeto -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $staging --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Falhar 'dotnet publish falhou.' }

    $novoExe = Join-Path $staging 'MonitorSVN.exe'
    if (-not (Test-Path $novoExe)) { Falhar 'O publish terminou, mas MonitorSVN.exe nao foi gerado.' }

    # 4) copia para a saida (somente o exe e o pdb; outros arquivos da pasta ficam como estao)
    Passo "Copiando para $Saida"
    New-Item -ItemType Directory -Force -Path $Saida | Out-Null
    Copy-Item $novoExe $exeFinal -Force
    $pdb = Join-Path $staging 'MonitorSVN.pdb'
    if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $Saida 'MonitorSVN.pdb') -Force }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

# 5) resumo
$info = Get-Item $exeFinal
$hash = (Get-FileHash $exeFinal -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'Pronto.' -ForegroundColor Green
Write-Host ('  Arquivo : ' + $info.FullName)
Write-Host ('  Versao  : ' + $info.VersionInfo.ProductVersion + '   (aparece em "Sobre")')
Write-Host ('  Tamanho : {0:N1} MB' -f ($info.Length / 1MB))
Write-Host ('  SHA-256 : ' + $hash)
Write-Host ''
Write-Host 'Para levar ao computador de destino, copie apenas o MonitorSVN.exe (o .pdb e so para depuracao).'
