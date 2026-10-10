# Volum (antigo LeveMixer)

Mixer de volume por aplicativo para Windows. Fica na bandeja do sistema e só aparece ao clicar no ícone.
Projeto pessoal do Esdras (designer, conhece HTML/CSS; a lógica em C# foi construída em conversa com o Claude).

## Objetivo e restrições
- Mostrar todos os apps que usam áudio (em primeiro e segundo plano) com volume, mute e medidor de nível.
- **Precisa ser leve**: o app fica aberto o tempo todo. Parado na bandeja, consumo de CPU ~0% e pouca RAM.
- Visual personalizável em HTML/CSS (esse foi o motivo da arquitetura atual).
- Abrir junto com o Windows (registrado na primeira execução).
- Português (pt-BR) na interface e nos comentários.

## Arquitetura
C# (WinForms + WebView2) cuida da lógica e da janela. A interface é HTML/CSS/JS na pasta `wwwroot/`.

| Arquivo | Papel |
|---|---|
| `Program.cs` | Ponto de entrada, instância única (Mutex), primeira execução (auto-início), flag `--dev` |
| `TrayController.cs` | Ícone da bandeja, menu (iniciar com o Windows / sair), abre e fecha o mixer |
| `MixerForm.cs` | Janela sem borda com WebView2; animações de abrir/fechar; ponte C# ⇄ JS |
| `AudioService.cs` | Toda a lógica de áudio (NAudio / Core Audio). Não conhece a interface |
| `Settings.cs` | `AppSettings` (tema, primeira execução, modo leve, idioma) salvo em `%AppData%\Volum\settings.json`; se não existir, lê o antigo `%AppData%\LeveMixer` |
| `Helpers.cs` | `AutoStart` (registro do Windows), `MemoryTrim`, `TickSound` (som do slider), `Strings` (textos do C# em 3 idiomas), `IconFactory` (ícone da bandeja por código) |
| `wwwroot/index.html` | Estrutura da interface |
| `wwwroot/style.css` | Visual: temas por variáveis CSS, animações |
| `wwwroot/app.js` | Comportamento da interface e mensagens com o C# |

### Mensagens (JSON) entre C# e JS
- **C# → JS:** `settings` (tema, auto-início, `glass` = Windows suporta vidro, `lite` = modo leve, `language`), `state` (volumes, microfone e lista de apps, a cada 1 s), `peaks` (medidores, incluindo o do microfone, a cada 60 ms), `open`, `close`
- **JS → C#:** `ready`, `resize` (altura do conteúdo), `setVolume`, `setMute`, `setTheme`, `setAutostart`, `setLightMode`, `setLanguage`, `setMicDevice` (escolher o microfone), `openSite` (link "Site do Volum" nas configurações; o C# abre no navegador e fecha o mixer), `hide` (botão minimizar), `tick` (som ao mudar volume)
- IDs: `__master` (volume geral), `__mic` (microfone), `__system` (sons do sistema), demais = nome do processo em minúsculas (apps com vários processos, como o Chrome, viram uma linha só).

## Decisões importantes (não desfazer sem motivo)
- **Janela redimensionada pelo conteúdo**: o JS mede a altura do `#card` e manda `resize`; o C# ajusta a janela ancorada no canto inferior direito. A largura é fixa (360 px) e precisa bater entre `MixerForm.cs` (`WidthDip`) e `style.css` (`.card { width }`).
- **Sem `Form.Opacity`**: o WebView2 pode não renderizar em janelas com opacidade. A animação de entrada é só CSS (conteúdo com fade e zoom, `.enter`/`.leave`); a janela **não desliza** ao abrir (mover a janela com o WebView a cada 10 ms engasgava). Ao fechar ainda desce 6 px (`Tween`).
- **Abertura instantânea**: no clique, `ShowShell()` mostra a janela vazia (vidro/fundo sólido) na hora, na última altura conhecida (`s_heightDip`); o conteúdo entra com a animação quando a interface responde. A frio (WebView2 descartado) o WebView2 leva ~1 s para iniciar e ocupa ~280 MB enquanto vivo, por isso ele não fica carregado o tempo todo. Um clique na bandeja só fecha depois que o conteúdo apareceu (`CanCloseByClick`).
- **Nome dos apps** (`PickName` em AudioService.cs): entre o nome da sessão de áudio, a descrição e o produto do .exe, fica o **mais curto**; limpa ®/™/©, ignora `@...` e nomes genéricos (Electron etc.); sem nenhum, usa o nome do processo.
- **Microfone**: é o microfone padrão do Windows (`DataFlow.Capture`, `Role.Console`); o slider é o ganho (`AudioEndpointVolume`, o mesmo das configurações de som do Windows). Fica no painel de cima, abaixo do volume geral, e some se não houver microfone. O Windows só mede o nível do microfone enquanto algum app o ouve; por isso o `AudioService` abre um `WasapiCapture` (áudio descartado) **só com a janela aberta e fora do modo leve** — efeito colateral: o Windows mostra "microfone em uso" pelo Volum nesse período. No JS, o medidor do microfone tem portão de ruído (`MIC_GATE`) e curva de raiz para a voz aparecer. Abaixo do ganho, o nome do microfone atual abre a lista dos conectados (`state.mic.devices`; o JS só remonta a lista quando ela muda). Escolher um manda `setMicDevice` e o C# o torna o **padrão do Windows** em todos os papéis (Console, Multimedia, Communications) via `IPolicyConfig` (interface interna do Windows, a mesma do EarTrumpet/SoundSwitch; classe `DefaultDevice` em AudioService.cs).
- **Idiomas** (pt, en, es; **português é sempre o padrão**): textos da interface no objeto `I18N` do `app.js`, aplicados por `data-i18n` / `data-i18n-title` / `data-i18n-aria` no HTML. Nomes das linhas do sistema (`__master`, `__mic`, `__system`) vêm da tradução, não do C#. Textos do C# (menu da bandeja, aviso do WebView2) ficam em `Strings` (Helpers.cs); o menu da bandeja relê o idioma toda vez que abre. Os nomes dos idiomas no seletor ficam sempre no próprio idioma.
- **`DevLog`** (Helpers.cs): só no `--dev`, anota os tempos da abertura em `%TEMP%\Volum-dev.log`.
- **Economia de recursos e abertura rápida** (decidido em 10/10/2026, opção "rápido por padrão"): timers e objetos COM de áudio só existem com a janela visível (`AudioService` é criado ao abrir e descartado ao fechar). **Fora do modo leve**, o WebView2 é pré-aquecido 4 s depois de o app iniciar (`Prewarm`) e, 45 s depois de fechar, fica "dormindo" (`TrySuspendAsync` + `MemoryUsageTargetLevel.Low`, controle invisível) em vez de ser descartado: abre na hora, custa ~110 MB a mais (medido: ~125 MB no total; GPU 35, navegador 32, página 21, serviços 21). **No modo leve**, o `MixerForm` é descartado após 45 s (~15 MB parado, abertura ~0,9–1,3 s). Também para abrir rápido: sem as chamadas de cache do DevTools fora do `--dev`; ícones (`IconCache`) e nomes de microfone (`MicNameCache`) guardados enquanto o app roda; a 1ª leitura de áudio não lista os microfones (chega no estado seguinte).
- **Glassmorphism**: no Windows 11 22H2+ (build 22621) a janela usa o acrílico nativo do DWM (`DWMWA_SYSTEMBACKDROP_TYPE`, frame estendido em toda a janela, `BackColor` preto e WebView2 transparente). O tom claro/escuro do acrílico segue o tema (`DWMWA_USE_IMMERSIVE_DARK_MODE`). **Cores**: laranja do site (`--accent: #ed5a23`) e neutros quentes (não azulados); o ícone da bandeja (`IconFactory`) e o `Volum.ico` usam o mesmo degradê laranja. O C# manda `glass: true` em `settings` e o JS põe a classe `.glass` no `<html>`; o CSS usa cores `rgba` por cima. O JS resolve o tema efetivo em `data-scheme="dark|light"` (o CSS usa só ele). **Tema "Sistema"**: quem decide é o C# (`WindowsIsDark()`, lê `SystemUsesLightTheme`, o modo da barra de tarefas) e manda `systemDark` no `settings`; se o tema do Windows mudar com o mixer aberto, manda `systemTheme` (via `SystemEvents.UserPreferenceChanged`). O `prefers-color-scheme` só é usado sem C# (demo do site).
- **Modo leve** (`AppSettings.LightMode`, chave nas configurações): `ApplyBackdrop()` troca na hora para fundo sólido (sem acrílico); sem o slide da janela (C#); classe `.lite` desliga todas as animações/transições, brilhos e granulado (CSS); medidores a cada 150 ms em vez de 60 ms.
- **Slider desenhado em HTML**: o `<input type="range">` fica invisível por cima e recebe o mouse; trilho, preenchimento e bolinha seguem `--fill` (0–100, registrado com `@property` para poder ter transição). A bolinha tem o mesmo tamanho (`--thumb-size`) no input e no desenho, senão o clique não bate. Antes do 22H2, visual sólido. `backdrop-filter` não serve: o WebView não enxerga a área de trabalho. O acrílico fica opaco quando a janela perde o foco (comportamento do Windows).
- **Som de tick**: gerado por código em `TickSound` (Helpers.cs) e tocado pelo C#, não pelo WebView, para a sessão de áudio ser do próprio processo; `AudioService` esconde a sessão do próprio PID. O JS manda `tick` a cada 5% de mudança, no máximo um a cada 45 ms.
- **Cantos e sombra**: DWM (`DWMWA_WINDOW_CORNER_PREFERENCE` e `DwmExtendFrameIntoClientArea`). No Windows 10 os cantos ficam retos.
- **`wwwroot` fica ao lado do .exe** (mapeado em `https://app.volum/`), com cache desligado, para editar o visual sem recompilar.
- **Navegação restrita** a `https://app.volum/`; novas janelas bloqueadas.
- **`--dev`**: `Volum.exe --dev` liga F12 (DevTools), abre o mixer ao iniciar e impede o fechamento ao perder o foco. Também usa a `wwwroot` **do projeto** (não a cópia em `bin`) e recarrega a interface sozinho ao salvar qualquer arquivo dela (`FileSystemWatcher`, espera 200 ms).
- **Nada pesado a cada segundo**: o `state` roda a cada 1 s na thread da interface. Ler nomes de dispositivos custa ~130 ms, então a lista de microfones fica guardada e só é refeita quando o Windows avisa de mudança (`IMMNotificationClient`, classe `DeviceWatcher`). Ligar/desligar a escuta do microfone (~0,5 s) roda em segundo plano. Use o `DevLog` para medir antes de colocar algo novo no `Snapshot()`.
- **Instalador e atualização (Velopack)**: `VelopackApp.Build().Run()` é a primeira linha do `Main`; ao desinstalar, tira o auto-início. Instala em `%LocalAppData%\Volum\current\`. `Updater.cs` procura versão nova no GitHub Releases 1 min depois de abrir e a cada 6 h, baixa em segundo plano e aplica **em silêncio** (`WaitExitThenApplyUpdates(silent: true)`, sem a janela de progresso do Velopack; reinicia sozinho em ~2 s) **só quando o mixer não está aberto**. Nada disso roda na cópia de desenvolvimento (pasta `bin`) nem no `--dev`. A cópia de desenvolvimento guarda o cache do WebView2 em `%LocalAppData%\VolumDev` (a instalada usa `%LocalAppData%\Volum`): se usasse a mesma pasta, o instalador acharia que o Volum já está instalado. **Instalar sempre fora do Claude** (dois cliques no Setup.exe): processos abertos pelo app do Claude têm `AppData` e registro desviados para a área privada dele, e a instalação fica invisível para o Windows. A versão aparece no topo do menu da bandeja. Ícone do .exe/instalador: `Volum.ico` (o da bandeja continua sendo desenhado por `IconFactory`).
- **Auto-início**: chave `HKCU\...\Run` com o nome `Volum`. Registro na primeira execução e correção do caminho **só na versão instalada** (`Updater.IsInstalled`), para a cópia da pasta `bin` não roubar o auto-início da instalada; o botão nas configurações funciona sempre. Troca a chave antiga `LeveMixer`, se existir.
- **Sem `InvariantGlobalization`**: com essa opção ligada, o WinForms quebra ao pedir a cultura do teclado do usuário (ex.: erro "1046 is an invalid culture identifier" com teclado português). Não religar.
- **Nome**: o app se chama **Volum** (exe, namespace, projeto `Volum.csproj`, host `app.volum`). Repositório no GitHub: `esdrasstm/Volum` (antes `LeveMixer-Web`; o GitHub redireciona o endereço antigo). As pastas no computador ainda se chamam LeveMixer.

## Como compilar e rodar
Requisitos: Windows 10/11, .NET 8 SDK, WebView2 Runtime (já vem no Windows 11).

```
dotnet run -c Release
dotnet run -c Release -- --dev
```
### Publicar uma versão nova (jeito principal: botão no GitHub)
1. Fazer commit + push das mudanças no código.
2. GitHub → aba **Actions** → **Publicar versão** → **Run workflow** → digitar o número (ex.: `0.1.3`, sempre maior que a última).
3. Em ~5 min o GitHub compila, gera instalador + atualização e publica no Releases (`.github/workflows/publicar.yml`, na raiz do repositório). Usa a permissão própria do repositório (`GITHUB_TOKEN`), **sem token pessoal**. O número digitado substitui o `<Version>` do `Volum.csproj` (que vale só para as builds locais).

### Publicar pelo terminal (alternativa manual)
1. Subir `<Version>` no `Volum.csproj` (ex.: 0.1.0 → 0.1.1).
2. Gerar o pacote (inclui o .NET, funciona sem instalar nada) e o instalador:
```
dotnet publish -c Release -r win-x64 --self-contained true -o publish
vpk pack -u Volum -v 0.1.1 -p publish -e Volum.exe --packTitle Volum --packAuthors Esdras -i Volum.ico -o Releases
```
3. Publicar no GitHub Releases (o **token fica fora do repositório**; quem roda é o Esdras, no terminal dele):
```
vpk upload github -o Releases --repoUrl https://github.com/esdrasstm/Volum --publish --releaseName "Volum 0.1.1" --tag v0.1.1 --token SEU_TOKEN
```
`Releases\Volum-win-Setup.exe` é o instalador para mandar para quem ainda não tem. Quem já tem recebe sozinho. `publish/` e `Releases/` estão no `.gitignore`. A ferramenta `vpk` é instalada com `dotnet tool install -g vpk` (mesma versão do pacote Velopack).

## Estado atual
- Uma versão anterior em WPF foi compilada e testada pelo Esdras com sucesso (tema Sistema/Escuro/Claro, animação, auto-início).
- A versão atual (WinForms + WebView2) foi escrita **sem ser compilada no ambiente do chat**: o primeiro `dotnet build` pode mostrar erros pequenos. Corrigir e validar: abrir/fechar, volume por app, mute, medidor, temas, auto-início depois de reiniciar o PC.
- Pontos a observar nos testes: consumo de RAM com a janela aberta e fechada, tempo da primeira abertura, apps que não aparecem na lista.

## Próximos passos planejados
1. Instalador + atualização automática com **Velopack**: feito. 0.1.0 e 0.1.1 publicadas; botão "Publicar versão" no GitHub Actions criado. Falta ver uma atualização de verdade acontecer (0.1.1 instalada → 0.1.2 publicada pelo botão).
2. README do repositório com print, o que o app faz e como instalar (avisar sobre o SmartScreen: "Mais informações" → "Executar assim mesmo").
3. Funcionalidades, por prioridade: escolher dispositivo de saída/microfone; lembrar o volume de cada app; perfis salvos ("Jogo", "Trabalho", "Noite"); atalhos globais; ocultar/fixar apps; roda do mouse no ícone da bandeja.
4. Futuramente: instalador com auto-update (**Velopack**) e, se for distribuir de verdade, assinatura de código.

## Combinados de trabalho
- Mudanças pequenas e pontuais; não reescrever arquivos inteiros sem necessidade.
- Pedir confirmação antes de `git commit`, `git push` e de publicar releases.
- Mensagens de commit **sem** linha de coautoria (`Co-Authored-By: ...`) e sem menção ao Claude/Anthropic: só o título e a lista de mudanças.
- Nunca colocar senhas, tokens ou chaves no repositório.
- Manter o app leve: evitar timers e polling quando a janela está fechada, evitar dependências pesadas.
- Visual: mexer em `wwwroot/` (HTML/CSS/JS) sempre que possível; manter a lógica de áudio no C#.
