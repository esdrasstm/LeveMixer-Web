using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Volum;

enum ThemeMode { System, Dark, Light }

/// <summary>Configurações salvas em %AppData%\Volum\settings.json</summary>
sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // Na primeira execução o app se registra para abrir junto com o Windows
    public bool FirstRunDone { get; set; }

    // Modo leve: sem vidro (acrílico), sem animações e medidores mais lentos
    public bool LightMode { get; set; }

    static string Folder(string app) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app);

    static string FilePath => System.IO.Path.Combine(Folder("Volum"), "settings.json");

    // Nome antigo do app: as configurações são trazidas na primeira vez que o Volum abre
    static string OldFilePath => System.IO.Path.Combine(Folder("LeveMixer"), "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : OldFilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
