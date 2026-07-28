namespace RLIntegration;

public sealed class RLMatrixConfig
{
    public int EnvCount { get; set; } = 4;
    public int MaxStepsPerEpisode { get; set; } = 5000;
    public int TopKUnits { get; set; } = 32;
    public int ObservationGridSize { get; set; } = 8;
    public int MacroStrideFrames { get; set; } = 16;
    public float LearningRate { get; set; } = 1e-4f;
    public float ClipEpsilon { get; set; } = 0.2f;
    public float Gamma { get; set; } = 0.99f;
    public float Lambda { get; set; } = 0.95f;
    public string ModelPath { get; set; } = "model.pt";
    public string MetadataPath { get; set; } = "model.metadata.json";
    public string DatasetPath { get; set; } = "data";
}
