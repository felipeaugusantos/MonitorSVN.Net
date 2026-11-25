# Monitor SVN

Acompanha os commits do SVN, dispara a geração das versões no Jenkins e cuida do que vem depois: **fechamento**
(compactação dos arquivos pelo BAT) e **cópia** da versão para a rede, tanto **Local** quanto **Cloud**.

Reescrita do Monitor SVN (Delphi) em C# + WPF, que também incorporou as funções de fechamento e cópia do
VarreduraSistema (Python). Os dois sistemas continuam compatíveis entre si (veja [Convivência com o VarreduraSistema](#convivência-com-o-varredurasistema)).

- [Primeira configuração](#primeira-configuração)
- [Uso diário](#uso-diário)
- [Fechar uma versão](#fechar-uma-versão)
- [Copiar uma versão](#copiar-uma-versão)
- [Fechamento automático](#fechamento-automático)
- [Vários operadores no mesmo servidor](#vários-operadores-no-mesmo-servidor)
- [Avisos, Modelos e Resumo](#avisos-modelos-e-resumo)
- [Problemas comuns](#problemas-comuns)
- [Onde ficam os dados e os logs](#onde-ficam-os-dados-e-os-logs)
- [Para quem desenvolve](#para-quem-desenvolve)

---

## Primeira configuração

Faça uma vez por operador (cada usuário do Windows tem a sua própria configuração).

**Requisitos:** `svn.exe` no PATH (ou o caminho completo em `SvnExecutable` no `settings.json`) e o Jenkins acessível,
com usuário e **senha**. Não precisa instalar .NET nem Java.

1. Copie o `MonitorSVN.exe` para o computador (ou servidor) e abra. É um arquivo único, sem instalador.
2. No painel **Monitorar** (à esquerda), preencha:

   | Seção | Campo | O que colocar |
   |---|---|---|
   | REPOSITÓRIO | URL do SVN | O endereço do repositório. |
   | REPOSITÓRIO | Lista de diretórios | Os diretórios monitorados. Use o botão verde **+** para incluir e o vermelho para remover. |
   | JENKINS | URL raiz do Jenkins | Endereço do Jenkins. |
   | JENKINS | URL de disparo do build | Modelo da URL de disparo (já vem preenchido). |
   | JENKINS | Usuário / Senha | Suas credenciais. Marque **Lembrar** para guardar a senha, protegida pelo Windows (só o seu usuário consegue ler). |
   | GERAÇÃO | Pasta raiz de geração | A pasta onde as versões são geradas e fechadas, por exemplo `C:\VERSOES_FECHADAS`. É a mesma pasta em que o Jenkins escreve e onde roda o fechamento. |
   | GERAÇÃO | Avisar se um build passar de | Minutos em andamento a partir dos quais o app avisa que está demorando. |
   | GERAÇÃO | Pasta de controle (vários operadores) | Opcional. Pasta de rede compartilhada entre os operadores. Veja [Vários operadores](#vários-operadores-no-mesmo-servidor). |
   | GERAÇÃO | Destinos de cópia extras | Opcional. Outras pastas de rede onde a cópia procura a pasta da versão, além das duas padrão. |
   | GERAÇÃO | Fechar a versão automaticamente ao gerar | Opcional. Veja [Fechamento automático](#fechamento-automático). |
   | — | Ignorar commits de | Usuários cujos commits não devem gerar versão (por exemplo, o usuário do próprio Jenkins). |

3. As configurações são salvas sozinhas. Clique em **Forçar monitoramento** para fazer a primeira verificação.
4. Se vai usar o app em mais de um operador, siga também [Vários operadores no mesmo servidor](#vários-operadores-no-mesmo-servidor).

> O botão com a lua/sol, no topo, alterna entre o tema claro e o escuro.

---

## Uso diário

### O ciclo de uma versão

```
commit no SVN  →  Pendente  →  Em andamento (build no Jenkins)  →  Gerado  →  Fechamento  →  Cópia
                                                              ↘  Erro
```

1. **Monitoramento.** O app consulta o SVN a cada hora cheia, ou na hora, em **Forçar monitoramento**. Cada versão
   tocada (a primeira pasta abaixo do diretório monitorado) vira uma linha na tabela *Últimos monitoramentos*.
2. **Geração.** O app cria o job no Jenkins a partir do modelo (se ainda não existir) e dispara o build. Se a mesma
   versão teve vários commits no dia, só o mais novo gera build, e os mais antigos ficam como **Ignorado**.
   Se um build antigo da mesma versão ainda está rodando, ele é abortado e o mais novo começa.
3. **Resultado.** A cada 2 minutos o app procura `concluido.txt` (vira **Gerado**) ou `erro.txt` (vira **Erro**, com
   o trecho do erro e o provável responsável) na pasta da versão.
4. **Depois de Gerado**, a versão é fechada e copiada. Veja as duas próximas seções.

### Status

| Status | Significado |
|---|---|
| Pendente | Commit detectado, build ainda não disparado. |
| Em andamento | Build disparado no Jenkins. |
| Gerado | Build concluído com sucesso. Já pode ser fechado e copiado. |
| Erro | Build com erro. Passe o mouse no status para ver o detalhe e o provável responsável. |
| Ignorado | Existe um commit mais novo da mesma versão. |

Uma linha **amarela** indica que o último fechamento gerou **menos ZIPs** do que essa pasta já gerou. Veja
[Fechar uma versão](#fechar-uma-versão).

### Encontrar e organizar

- **Commit feito em**: filtra a tabela por dia (o padrão é hoje).
- **Buscar**: procura por revisão (por exemplo, `507109` ou `r507109`), autor ou versão, em **todos** os dias.
- O botão com a seta em cada linha expande ou recolhe os outros commits da mesma versão.

### Menu do botão direito na linha

| Item | O que faz |
|---|---|
| Abrir no Jenkins | Abre o job da versão no navegador. |
| Copiar revisão | Copia o número da revisão. |
| **Fechar versão (Local/Cloud)** | Inicia o fechamento. Só em linhas **Gerado**. |
| **Cancelar fechamento** | Cancela um fechamento na fila ou em andamento. |
| **Copiar versão (Local/Cloud)** | Abre a janela de cópia. Só em linhas **Gerado**. |
| Reprocessar | Dispara o build de novo (use se o build travou ou se perdeu no Jenkins). |
| Excluir (somente com erro) | Remove a linha com erro da lista. |

O app pode ser minimizado para a bandeja do Windows e continua monitorando. Ao tentar fechar a janela com um
fechamento em andamento, o app pergunta antes, porque sair cancela os fechamentos.

---

## Fechar uma versão

O fechamento roda o `comandosCMD\_FechamentoArquivos.bat` da pasta da versão. Ele compacta cada `.exe`, `.dll` e
`.bpl` em um `.zip` e apaga o original.

**Passo a passo**

1. Na tabela, clique com o botão direito numa linha com status **Gerado**.
2. Escolha **Fechar versão (Local/Cloud)** e confirme.
3. Acompanhe a coluna **Fechamento**:

   | Texto | Significado |
   |---|---|
   | Na fila | Aguardando vaga. Rodam até **2 fechamentos ao mesmo tempo**. |
   | Em andamento 42% | O BAT está compactando. O percentual vem do tamanho dos arquivos que ainda faltam. |
   | Concluído 03:12 | Terminou, no tempo indicado. |
   | Erro / Erro em 80% | Sobraram arquivos sem compactar ou o BAT passou de **1 hora**. O detalhe está no log. |
   | Cancelado | Você cancelou. |

4. Ao terminar, aparece um aviso na bandeja (e uma faixa vermelha, se deu erro).

**Como o app decide se deu certo.** O fechamento só é considerado concluído quando **não sobra nenhum `.exe`, `.dll`
ou `.bpl`** na raiz da pasta. O código de saída do BAT não é usado, porque ele não é confiável.

**Local ou Cloud.** É decidido pelo nome da pasta: se termina com `_CLOUD`, é Cloud. Os dois usam o mesmo BAT.

**O que o app confere antes de iniciar** (se algo falhar, ele mostra o motivo e não inicia):
- a pasta existe e tem a pasta `comandosCMD` com o `_FechamentoArquivos.bat`;
- os nomes dos ZIPs batem com o arquivo que contêm (`autcom.zip` guarda `autcom.exe`);
- ninguém mais está usando a pasta (veja [Vários operadores](#vários-operadores-no-mesmo-servidor));
- não há outro fechamento da mesma versão na fila.

**Cancelar.** Botão direito → **Cancelar fechamento**. Na fila, sai na hora. Em andamento, o BAT e tudo o que ele
abriu são encerrados em até 1 segundo. Os arquivos que ainda não foram compactados ficam na pasta, e a versão precisa
ser fechada de novo.

**Linha amarela (menos ZIPs que o normal).** O app lembra, por pasta, o maior total de ZIPs que ela já gerou. Se um
fechamento gera menos que isso, a linha fica amarela e a coluna mostra, por exemplo, `Concluído 03:12 (12/15 ZIPs)`.
Passe o mouse no status para ver a mensagem e **confira se não falta algum arquivo**. O alerta não some sozinho; se a
versão passou a ter menos ZIPs de propósito, remova a entrada da pasta no `zip_memory.json` (veja
[Onde ficam os dados](#onde-ficam-os-dados-e-os-logs)).

**Padrão de arquivos.** A cada fechamento, antes de rodar o BAT, o app aprende quais arquivos aquela versão deve ter
(por projeto, separando Local e Cloud). Esse padrão é usado depois para validar a cópia.

---

## Copiar uma versão

A cópia envia os `.zip` da versão para a pasta de destino na rede, confere cada arquivo e limpa a origem.

**Passo a passo**

1. Feche a versão (seção anterior) e espere a coluna **Fechamento** mostrar *Concluído*.
2. Clique com o botão direito na linha → **Copiar versão (Local/Cloud)**.
3. A janela procura a pasta de destino com o nome da versão nas pastas de rede. Se achar **mais de uma**, escolha a
   correta na lista. Se **não achar**, ela oferece criar a pasta nova em
   `_VERSOES_INDIVIDUAIS\<versão>\<nome>`, por exemplo `02.30.74-150\379.48.2.30.74.150_LOCAL`.
4. Confira as **validações de segurança** mostradas na janela. O botão **Continuar** só fica ativo se todas estiverem OK:

   | Validação | O que confere |
   |---|---|
   | Tipo do projeto | A ação (Local/Cloud) combina com a pasta. |
   | Fechamento concluído | Não sobrou `.exe`, `.dll` ou `.bpl` e existe pelo menos um ZIP. |
   | Status do projeto | Todos os arquivos do padrão da versão estão presentes e com a versão certa. |
   | Nome do destino | O destino tem o nome esperado (`<nome>` ou, na cópia Local, `<nome>_LOCAL`). |
   | Destino dentro da raiz configurada | O destino está numa das pastas de rede permitidas. |
   | FileVersion / ProductVersion | O Autcom tem as versões que o nome da pasta indica. |
   | Autcom MB | O tamanho do Autcom foi lido. |
   | Limite Local / Limite Cloud | **Local:** Autcom abaixo de 100 MB. **Cloud:** Autcom a partir de 150 MB. |
   | Nome dos ZIPs | Os nomes dos ZIPs batem com o conteúdo. |

   As validações leem o disco na hora, não uma verificação antiga. Um Autcom entre 100 e 150 MB não passa em nenhuma
   das duas cópias.
5. Clique em **Continuar**. A janela mostra o andamento e dá para **Cancelar** (até o último passo, sem alterar nada).
6. No fim, a janela mostra o resultado e **copia o caminho do destino para a área de transferência**.

**O que a cópia faz, por dentro (para você confiar nela)**
- Copia para uma pasta temporária dentro do destino e confere o **SHA-256** de cada arquivo.
- Move o que já existia no destino para um backup, coloca os arquivos novos e, **se algo falhar, desfaz tudo**,
  restaurando o que estava antes.
- Só então apaga da origem **os ZIPs que foram copiados e não mudaram** desde a cópia. A pasta `comandosCMD` é sempre
  preservada. O que não for ZIP continua na origem e é listado na janela (use **Limpar Pasta** no Varredura, se quiser removê-los).
- Se a origem mudar durante a cópia, ela falha e nada é alterado no destino.
- ZIPs que já estavam no destino e não fazem parte desta cópia **não são apagados**; a janela os lista.

---

## Fechamento automático

Marque **Fechar a versão automaticamente ao gerar** (seção GERAÇÃO). Aí, a cada versão que passa para **Gerado**:

1. o fechamento começa sozinho (na instância que está monitorando);
2. ao concluir com sucesso, o app **pergunta** *"Iniciar a cópia agora?"*. Se responder **Sim**, abre a janela de cópia
   com todas as validações de sempre.

A **cópia nunca começa sozinha**, porque ela apaga os ZIPs da origem. Se a janela estiver na bandeja, a pergunta
aguarda e aparece quando você abrir o app.

Só fecham automaticamente as versões que terminaram nos últimos 30 minutos, e cada linha tenta uma única vez. Ligar a
opção não fecha o histórico antigo. Se o fechamento automático for bloqueado (por exemplo, ZIP com nome errado ou
projeto em uso), o app mostra o motivo.

---

## Vários operadores no mesmo servidor

Cada usuário do Windows abre o seu próprio Monitor SVN, com a sua lista. Sem cuidado, os dois consultariam o SVN e
disparariam o **mesmo build** no Jenkins, e um abortaria o do outro. Para evitar isso, só **uma instância por vez
monitora**; as outras ficam em **somente leitura**.

**Configuração (em cada operador, uma vez):**

1. No painel **Monitorar**, seção **GERAÇÃO**, preencha **Pasta de controle (vários operadores)** com a pasta de rede
   compartilhada, por exemplo `\\citel-fileserve\Modem\_CONTROLE_VARREDURA`. Ao sair do campo, o app salva e avisa que a
   mudança só vale depois de reabrir (e alerta se não conseguir acessar a pasta agora).
2. Feche e abra o Monitor SVN de novo.
3. Confira a **senha do Jenkins** no seu usuário (ela é protegida por usuário do Windows, então cada operador precisa
   ter a sua).

Para desligar o recurso, apague o conteúdo do campo e reabra o app.

**Como funciona**
- Quem abrir primeiro passa a **monitorar**. O título da janela fica *Monitor SVN*.
- Os outros ficam em **somente leitura**: o título vira *Monitor SVN — somente leitura*, o texto da barra de status
  mostra quem está monitorando, e *Forçar monitoramento*, *Reprocessar* e a verificação automática ficam bloqueados.
  Eles continuam vendo a lista (atualizada a cada ciclo) e **podem fechar e copiar versões normalmente**.
- Se quem monitora fechar o app, o papel é liberado na hora. Se o programa ou a rede cair, outra instância assume em
  até 3 minutos. Nada é disparado em dobro, porque quem assume continua do histórico que o outro publicou.
- Fechamento e cópia usam uma **trava por pasta** (`.varredura.lock`): se outro operador está fechando ou copiando a
  mesma versão, você vê quem é e desde quando, e a ação é bloqueada. A trava expira sozinha em 3 minutos se o programa
  que a segurava fechar de forma inesperada.
- O padrão de arquivos e a memória de ZIPs ficam nessa mesma pasta e são **compartilhados**: o que um operador
  aprende vale para todos.

Se a pasta de controle estiver indisponível, a instância fica em somente leitura, em vez de arriscar um build duplicado.
Deixar o campo **Pasta de controle** vazio desliga o recurso (a instância sempre monitora, como antes).

Se as versões também ficam em outras pastas de rede, além das duas padrão, inclua-as em **Destinos de cópia extras**
(botão verde **+**; o vermelho remove). Digite o caminho completo, como `\\servidor\pasta`. Esse campo vale na hora, sem
reabrir o app, e cada operador tem a sua lista.

---

## Avisos, Modelos e Resumo

- **Avisos** (botão no topo): manda aviso por **e-mail** e/ou **Google Chat** quando uma versão termina com erro ou
  passa do tempo, com mensagem personalizável. Dá para incluir também as versões geradas com sucesso. Os mesmos avisos
  aparecem no balão da bandeja.
- **Modelos**: lista as libs de cada job-modelo do Jenkins e permite **incluir** ou **remover** uma lib nos modelos.
- **Resumo**: totais, taxa de erro e tempos médios do histórico (guardado por 90 dias).
- **Sobre**: versão do programa (muda a cada publicação).

---

## Problemas comuns

| O que aparece | O que fazer |
|---|---|
| *Fechamento não iniciado: N ZIP(s) com nome incorreto* | Algum `.zip` tem nome diferente do arquivo dentro dele (por exemplo, `autcom_novo.zip` contendo `autcom.exe`). Renomeie o ZIP. |
| *Fechamento não iniciado: BAT não encontrado* | A pasta da versão não tem `comandosCMD\_FechamentoArquivos.bat`. Veja se o build gerou a pasta corretamente. |
| *Projeto em uso: … em andamento por fulano desde …* | Outro operador está fechando ou copiando a mesma versão. Espere terminar. Se o programa dele caiu, a trava expira em 3 minutos. |
| Fechamento terminou em **Erro: N arquivo(s) não compactado(s)** | O BAT não compactou tudo. Abra o log do fechamento (veja abaixo) e rode de novo. |
| **Cancelado** | Os arquivos sobraram na pasta. Feche a versão de novo. |
| *Nenhum destino de rede configurado foi encontrado* | O app não alcança as pastas de rede. Confira a conexão com o servidor de arquivos. |
| *Não foi encontrada pasta de destino…* | Não há pasta com o nome da versão. Se for uma versão normal, a janela oferece criar; cópias específicas de 5 partes não têm essa regra e precisam do destino já existente. |
| Cópia bloqueada em **Status do projeto** | Falta um arquivo do padrão da versão, ou a versão dele está errada. A janela diz qual. |
| Cópia bloqueada em **Limite Local / Cloud** | O tamanho do Autcom não bate com o tipo da cópia (Local abaixo de 100 MB, Cloud a partir de 150 MB). Veja se a cópia certa foi escolhida. |
| Cópia: *Verificação pós-cópia… alterado na origem* | Um ZIP mudou enquanto era copiado (provavelmente um fechamento ainda rodando). Espere e tente de novo. Nada foi alterado no destino. |
| Título *Monitor SVN — somente leitura* | Outra instância está monitorando. Veja a barra de status. Você ainda pode fechar e copiar. |
| Linha **amarela** | Menos ZIPs que o normal. Confira se não falta arquivo. |
| Nada acontece / mensagem estranha | Veja o log do dia (próxima seção). |

---

## Onde ficam os dados e os logs

Tudo fica em `%APPDATA%\MonitorSVN` (ou na pasta indicada pela variável de ambiente `MONITORSVN_DATA`).

| Arquivo | Para quê |
|---|---|
| `settings.json` | Configurações. Senhas (Jenkins, SMTP) e o endereço do webhook do Chat ficam protegidos pelo Windows, nunca em texto puro. |
| `entries.json` | Histórico da tabela (90 dias). |
| `logs\monitorsvn-AAAAMMDD.log` | Log do dia. Registra também cada fechamento e cópia (com usuário, origem, destino e SHA-256). Apagado após 30 dias. |
| `logs\fechamentos_bat\<versão>_<data>.log` | Saída do BAT de cada fechamento. Apagado após 90 dias, ao abrir o app. |

Na **pasta de controle** (quando configurada), ficam os arquivos compartilhados:

| Arquivo | Para quê |
|---|---|
| `monitor_svn.lock` | Quem está monitorando. |
| `monitor_svn_estado.json` | Histórico publicado pelo monitor. |
| `project_pattern.json` | Padrão de arquivos de cada versão. |
| `zip_memory.json` | Maior total de ZIPs de cada pasta. |

Sem pasta de controle, os dois últimos ficam na pasta de dados local. Na primeira abertura, se ainda não existir, o app
**importa o `project_pattern.json` do VarreduraSistema** (de `%LOCALAPPDATA%\VarreduraSistema`).

---

## Convivência com o VarreduraSistema

As funções de fechamento e cópia foram portadas do VarreduraSistema e usam os **mesmos formatos de arquivo**
(`.varredura.lock`, `project_pattern.json`, `zip_memory.json`). Os dois sistemas podem rodar ao mesmo tempo: a trava de
projeto vale para ambos, e o padrão e a memória de ZIPs podem ser compartilhados apontando para a mesma pasta.

O que ainda é exclusivo do VarreduraSistema: o teste de abertura do Autcom, o monitor de scripts do banco modelo e a
auditoria de estrutura SQL.

---

## Para quem desenvolve

Requer o .NET SDK (testado com o 10.0). Não precisa de Visual Studio.

```text
dotnet build MonitorSVN.slnx
dotnet test  MonitorSVN.Tests
dotnet run --project MonitorSVN.Wpf
```

Também abre no Visual Studio 2022/2026 pelo arquivo `MonitorSVN.slnx`.

**Gerar o executável**

```text
publicar.cmd              (duplo clique, ou no terminal)
.\publicar.ps1            (PowerShell)
.\publicar.ps1 -Fechar    (encerra o MonitorSVN aberto em publish\ para poder substituí-lo)
.\publicar.ps1 -SemTestes (pula os testes; não recomendado)
```

Roda os testes (aborta se algum falhar), gera **um único `.exe` autocontido** (win-x64, não exige .NET no destino) e o
coloca sempre em `publish\MonitorSVN.exe`. Outros arquivos da pasta `publish` (ex.: `.rar`) não são alterados. A versão
exibida em **Sobre** muda a cada execução (`1.0.AAAAMMDD.HHmm`). No computador de destino basta copiar o `.exe`
(o `.pdb` serve só para depuração).

**Estrutura**

- `MonitorSVN.Wpf/Services`
  - Monitoramento: `SvnClient`, `SvnLogParser`, `JenkinsClient`, `JobPreparer`, `MonitorService`, `MonitorRole`, `SettingsStore`.
  - Fechamento: `ClosingRunner` (executa o BAT), `ClosingService` (fila, trava, aprendizado do padrão e memória de ZIPs), `AutoChain`.
  - Cópia: `CopyRules` (regras e validações), `DestinationFinder`, `CopyService` (cópia transacional).
  - Compartilhados: `ProjectLock`, `ProjectPattern` e `ProjectAnalyzer` (padrão de arquivos), `ZipMemory`, `ZipNameValidator`, `VersionReader`.
- `MonitorSVN.Wpf/MainWindow.xaml` e `CopyWindow.cs`: telas.
- `MonitorSVN.Tests`: testes (xUnit) das regras de versão, do parser, do fechamento, da cópia, das travas e do padrão.

As regras que escolhem o job-modelo e as versões estão em `Rules`, no `settings.json`.
