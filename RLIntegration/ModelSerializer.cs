using System.IO;
using System.Text.Json;

namespace RLIntegration;

public sealed class ModelSerializer
{
    public void SaveMetadata(string path, RLMatrixConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public RLMatrixConfig LoadMetadata(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RLMatrixConfig>(json) ?? new RLMatrixConfig();
    }
}
