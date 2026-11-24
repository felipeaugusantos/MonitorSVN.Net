# Monitor SVN (protótipo WPF / .NET)

Reescrita do Monitor SVN (Delphi) em C# + WPF, sem componentes pagos.

## Compilar e executar

Requer o .NET SDK (testado com o 10.0). Não precisa de Visual Studio.

```
dotnet build MonitorSVN.slnx
dotnet test  MonitorSVN.Tests
dotnet run --project MonitorSVN.Wpf
```

Também abre no Visual Studio 2022/2026 pelo arquivo `MonitorSVN.slnx`.

## Gerar o executável

```text
publicar.cmd              (duplo clique, ou no terminal)
.\publicar.ps1            (PowerShell)
.\publicar.ps1 -Fechar    (encerra o MonitorSVN aberto em publish\ para poder substituí-lo)
.\publicar.ps1 -SemTestes (pula os testes; não recomendado)
```

Roda os testes (aborta se algum falhar), gera **um único `.exe` autocontido** (win-x64, não exige .NET no destino) e o
coloca sempre em `publish\MonitorSVN.exe`. Outros arquivos da pasta `publish` (ex.: `.rar`) não são alterados.
A versão exibida em **Sobre** muda a cada execução (`1.0.AAAAMMDD.HHmm`). No computador de destino basta copiar o `.exe`
(o `.pdb` serve só para depuração).

## Requisitos em tempo de execução

- `svn.exe` no PATH (ou caminho completo em `SvnExecutable`, no `settings.json`).
- Jenkins acessível, com usuário e **senha**. O Java e o `jenkins-cli.jar` não são mais necessários.

## Onde ficam os dados

`%APPDATA%\MonitorSVN\settings.json` e `entries.json`. A senha é gravada protegida pelo DPAPI do Windows
(só o mesmo usuário do Windows consegue ler). A variável de ambiente `MONITORSVN_DATA` aponta outra pasta.

## Como funciona

1. A cada hora cheia (e ao clicar em "Forçar monitoramento"), roda `svn log -v -q --xml --non-interactive` de cada diretório.
2. Cada versão (primeira pasta abaixo do diretório monitorado) tocada hoje vira uma linha. Copias (`copyfrom`) e usuários ignorados são descartados.
3. Se a mesma versão teve vários commits no dia, só o mais novo gera build; os demais ficam "Ignorado".
4. Para as pendentes: cria o job no Jenkins a partir do modelo (se não existir), copia a pasta de geração trocando
   `releaseversion`/`fileversion`/`productversion` e dispara o build por HTTP.
5. A cada 2 minutos procura `concluido.txt` (vira "Gerado") ou `erro.txt` (vira "Erro", com dica) na pasta da versão.

As regras que escolhem o job-modelo e as versões (antes fixas no código) estão em `Rules`, no `settings.json`.

## Estrutura

- `MonitorSVN.Wpf/Services`: `SvnClient`, `SvnLogParser`, `JenkinsClient`, `JobPreparer`, `MonitorService`, `SettingsStore`.
- `MonitorSVN.Wpf/MainWindow.xaml`: tela (visual aprovado no mock).
- `MonitorSVN.Tests`: testes do parser, das regras de versão e da cópia com substituição.
