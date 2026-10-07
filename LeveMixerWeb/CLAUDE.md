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
| `Settings.cs` | `AppSettings` (tema, primeira execução, modo leve) salvo em `%AppData%\Volum\settings.json`; se não existir, lê o antigo `%AppData%\LeveMixer` |
| `Helpers.cs` | `AutoStart` (registro do Windows), `MemoryTrim`, `TickSound` (som do slider), `IconFactory` (ícone da bandeja por código) |
| `wwwroot/index.html` | Estrutura da interface |
| `wwwroot/style.css` | Visual: temas por variáveis CSS, animações |
| `wwwroot/app.js` | Comportamento da interface e mensagens com o C# |

### Mensagens (JSON) entre C# e JS
- **C# → JS:** `settings` (tema, auto-início, `glass` = Windows suporta vidro, `lite` = modo leve), `state` (volumes e lista de apps, a cada 1 s), `peaks` (medidores, a cada 60 ms), `open`, `close`
- **JS → C#:** `ready`, `resize` (altura do conteúdo), `setVolume`, `setMute`, `setTheme`, `setAutostart`, `setLightMode`, `hide` (botão minimizar), `tick` (som ao mudar volume)
- IDs: `__master` (volume geral), `__system` (sons do sistema), demais = nome do processo em minúsculas (apps com vários processos, como o Chrome, viram uma linha só).

## Decisões importantes (não desfazer sem motivo)
- **Janela redimensionada pelo conteúdo**: o JS mede a altura do `#card` e manda `resize`; o C# ajusta a janela ancorada no canto inferior direito. A largura é fixa (360 px) e precisa bater entre `MixerForm.cs` (`WidthDip`) e `style.css` (`.card { width }`).
- **Sem `Form.Opacity`**: o WebView2 pode não renderizar em janelas com opacidade. A animação de entrada é só CSS (conteúdo com fade e zoom, `.enter`/`.leave`); a janela **não desliza** ao abrir (mover a janela com o WebView a cada 10 ms engasgava). Ao fechar ainda desce 6 px (`Tween`).
- **Abertura instantânea**: no clique, `ShowShell()` mostra a janela vazia (vidro/fundo sólido) na hora, na última altura conhecida (`s_heightDip`); o conteúdo entra com a animação quando a interface responde. A frio (WebView2 descartado) o WebView2 leva ~1 s para iniciar e ocupa ~280 MB enquanto vivo, por isso ele não fica carregado o tempo todo. Um clique na bandeja só fecha depois que o conteúdo apareceu (`CanCloseByClick`).
- **Nome dos apps** (`PickName` em AudioService.cs): entre o nome da sessão de áudio, a descrição e o produto do .exe, fica o **mais curto**; limpa ®/™/©, ignora `@...` e nomes genéricos (Electron etc.); sem nenhum, usa o nome do processo.
- **`DevLog`** (Helpers.cs): só no `--dev`, anota os tempos da abertura em `%TEMP%\Volum-dev.log`.
- **Economia de recursos**: timers e objetos COM de áudio só existem com a janela visível (`AudioService` é criado ao abrir e descartado ao fechar). O WebView fica vivo 45 s depois de fechar (reabrir rápido) e então o `MixerForm` é descartado.
- **Glassmorphism**: no Windows 11 22H2+ (build 22621) a janela usa o acrílico nativo do DWM (`DWMWA_SYSTEMBACKDROP_TYPE`, frame estendido em toda a janela, `BackColor` preto e WebView2 transparente). O tom claro/escuro do acrílico segue o tema (`DWMWA_USE_IMMERSIVE_DARK_MODE`). O C# manda `glass: true` em `settings` e o JS põe a classe `.glass` no `<html>`; o CSS usa cores `rgba` por cima. O JS também resolve o tema efetivo em `data-scheme="dark|light"` (o CSS usa só ele).
- **Modo leve** (`AppSettings.LightMode`, chave nas configurações): `ApplyBackdrop()` troca na hora para fundo sólido (sem acrílico); sem o slide da janela (C#); classe `.lite` desliga todas as animações/transições, brilhos e granulado (CSS); medidores a cada 150 ms em vez de 60 ms.
- **Slider desenhado em HTML**: o `<input type="range">` fica invisível por cima e recebe o mouse; trilho, preenchimento e bolinha seguem `--fill` (0–100, registrado com `@property` para poder ter transição). A bolinha tem o mesmo tamanho (`--thumb-size`) no input e no desenho, senão o clique não bate. Antes do 22H2, visual sólido. `backdrop-filter` não serve: o WebView não enxerga a área de trabalho. O acrílico fica opaco quando a janela perde o foco (comportamento do Windows).
- **Som de tick**: gerado por código em `TickSound` (Helpers.cs) e tocado pelo C#, não pelo WebView, para a sessão de áudio ser do próprio processo; `AudioService` esconde a sessão do próprio PID. O JS manda `tick` a cada 5% de mudança, no máximo um a cada 45 ms.
- **Cantos e sombra**: DWM (`DWMWA_WINDOW_CORNER_PREFERENCE` e `DwmExtendFrameIntoClientArea`). No Windows 10 os cantos ficam retos.
- **`wwwroot` fica ao lado do .exe** (mapeado em `https://app.volum/`), com cache desligado, para editar o visual sem recompilar.
- **Navegação restrita** a `https://app.volum/`; novas janelas bloqueadas.
- **`--dev`**: `Volum.exe --dev` liga F12 (DevTools), abre o mixer ao iniciar e impede o fechamento ao perder o foco.
- **Auto-início**: chave `HKCU\...\Run` com o nome `Volum`; não registra quando o processo é o `dotnet.exe` (`dotnet run`); corrige o caminho a cada abertura e troca a chave antiga `LeveMixer`, se existir.
- **Nome**: o app se chama **Volum** (exe, namespace, projeto `Volum.csproj`, host `app.volum`). As pastas e o repositório no GitHub ainda se chamam LeveMixer.

## Como compilar e rodar
Requisitos: Windows 10/11, .NET 8 SDK, WebView2 Runtime (já vem no Windows 11).

```
dotnet run -c Release
dotnet run -c Release -- --dev
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
O resultado fica em `bin\Release\net8.0-windows\win-x64\publish\`. Distribuir a **pasta inteira** (`.exe`, `WebView2Loader.dll`, `wwwroot`).

## Estado atual
- Uma versão anterior em WPF foi compilada e testada pelo Esdras com sucesso (tema Sistema/Escuro/Claro, animação, auto-início).
- A versão atual (WinForms + WebView2) foi escrita **sem ser compilada no ambiente do chat**: o primeiro `dotnet build` pode mostrar erros pequenos. Corrigir e validar: abrir/fechar, volume por app, mute, medidor, temas, auto-início depois de reiniciar o PC.
- Pontos a observar nos testes: consumo de RAM com a janela aberta e fechada, tempo da primeira abertura, apps que não aparecem na lista.

## Próximos passos planejados
1. Instalador com **Inno Setup**, publicando com `--self-contained true` (para funcionar sem instalar o .NET) e distribuição no **GitHub Releases** (não subir `.exe` no repositório).
2. README do repositório com print, o que o app faz e como instalar (avisar sobre o SmartScreen: "Mais informações" → "Executar assim mesmo").
3. Funcionalidades, por prioridade: escolher dispositivo de saída/microfone; lembrar o volume de cada app; perfis salvos ("Jogo", "Trabalho", "Noite"); atalhos globais; ocultar/fixar apps; roda do mouse no ícone da bandeja.
4. Futuramente: instalador com auto-update (**Velopack**) e, se for distribuir de verdade, assinatura de código.

## Combinados de trabalho
- Mudanças pequenas e pontuais; não reescrever arquivos inteiros sem necessidade.
- Pedir confirmação antes de `git commit`, `git push` e de publicar releases.
- Nunca colocar senhas, tokens ou chaves no repositório.
- Manter o app leve: evitar timers e polling quando a janela está fechada, evitar dependências pesadas.
- Visual: mexer em `wwwroot/` (HTML/CSS/JS) sempre que possível; manter a lógica de áudio no C#.
