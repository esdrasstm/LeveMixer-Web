# LeveMixer

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
| `Settings.cs` | `AppSettings` (tema, primeira execução) salvo em `%AppData%\LeveMixer\settings.json` |
| `Helpers.cs` | `AutoStart` (registro do Windows), `MemoryTrim`, `IconFactory` (ícone da bandeja por código) |
| `wwwroot/index.html` | Estrutura da interface |
| `wwwroot/style.css` | Visual: temas por variáveis CSS, animações |
| `wwwroot/app.js` | Comportamento da interface e mensagens com o C# |

### Mensagens (JSON) entre C# e JS
- **C# → JS:** `settings` (tema, auto-início), `state` (volumes e lista de apps, a cada 1 s), `peaks` (medidores, a cada 60 ms), `open`, `close`
- **JS → C#:** `ready`, `resize` (altura do conteúdo), `setVolume`, `setMute`, `setTheme`, `setAutostart`
- IDs: `__master` (volume geral), `__system` (sons do sistema), demais = nome do processo em minúsculas (apps com vários processos, como o Chrome, viram uma linha só).

## Decisões importantes (não desfazer sem motivo)
- **Janela redimensionada pelo conteúdo**: o JS mede a altura do `#card` e manda `resize`; o C# ajusta a janela ancorada no canto inferior direito. A largura é fixa (360 px) e precisa bater entre `MixerForm.cs` (`WidthDip`) e `style.css` (`.card { width }`).
- **Sem `Form.Opacity`**: o WebView2 pode não renderizar em janelas com opacidade. A animação de entrada é: janela sobe alguns pixels (C#, `Tween`) + conteúdo com fade e zoom (CSS `.enter`/`.leave`). Janela inteira com fade/zoom não é possível nessa arquitetura.
- **Economia de recursos**: timers e objetos COM de áudio só existem com a janela visível (`AudioService` é criado ao abrir e descartado ao fechar). O WebView fica vivo 45 s depois de fechar (reabrir rápido) e então o `MixerForm` é descartado.
- **Cantos e sombra**: DWM (`DWMWA_WINDOW_CORNER_PREFERENCE` e `DwmExtendFrameIntoClientArea`). No Windows 10 os cantos ficam retos.
- **`wwwroot` fica ao lado do .exe** (mapeado em `https://app.leve/`), com cache desligado, para editar o visual sem recompilar.
- **Navegação restrita** a `https://app.leve/`; novas janelas bloqueadas.
- **`--dev`**: `LeveMixer.exe --dev` liga F12 (DevTools) e impede o fechamento ao perder o foco.
- **Auto-início**: chave `HKCU\...\Run`; não registra quando o processo é o `dotnet.exe` (`dotnet run`); corrige o caminho a cada abertura.

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
