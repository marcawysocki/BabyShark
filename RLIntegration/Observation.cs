using System.Collections.Generic;

namespace RLIntegration;

public sealed class Observation
{
    public const int UnitFeatureLength = 16;

    public float[] OwnUnitsTopK { get; set; } = new float[0];
    public float[] EnemyUnitsTopK { get; set; } = new float[0];
    public float[] Aggregates { get; set; } = new float[0];
    public float[] FogMaskSummary { get; set; } = new float[0];

    public static int GetVectorLength(int topK, int gridSize)
    {
        return (topK * UnitFeatureLength * 2) + 32 + (gridSize * gridSize);
    }

    public static Observation Create(int topK, int gridSize)
    {
        return new Observation
        {
            OwnUnitsTopK = new float[topK * UnitFeatureLength],
            EnemyUnitsTopK = new float[topK * UnitFeatureLength],
            Aggregates = new float[32],
            FogMaskSummary = new float[gridSize * gridSize]
        };
    }
}
