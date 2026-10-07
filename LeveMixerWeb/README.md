# Volum (C# + HTML/CSS)

Mixer de volume por aplicativo para Windows, com visual em vidro (glassmorphism) e um "Modo leve" que desliga os efeitos.
- **Lógica (C#)**: `AudioService.cs` (áudio), `MixerForm.cs` (janela/WebView2), `TrayController.cs`, `Settings.cs`
- **Interface (HTML/CSS/JS)**: pasta `wwwroot/` — `index.html`, `style.css`, `app.js`

## Requisitos
- Windows 10/11 com **WebView2 Runtime** (já vem no Windows 11)
- .NET 8 SDK (para compilar)

## Rodar / gerar o executável
    dotnet run -c Release
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

Copie a **pasta `publish` inteira** (o .exe, o `WebView2Loader.dll` e a pasta `wwwroot`).

## Personalizar o visual
Edite `wwwroot/style.css` (cores em variáveis no topo, animações em `@keyframes`).
Não precisa recompilar: feche o mixer, espere uns segundos (ou saia pela bandeja) e abra de novo.

## Modo desenvolvedor
    Volum.exe --dev
- F12 abre o DevTools (inspecionar HTML/CSS como num site)
- O mixer abre assim que o app inicia e não fecha ao perder o foco
