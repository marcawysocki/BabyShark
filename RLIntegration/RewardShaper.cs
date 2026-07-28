namespace RLIntegration;

public sealed class RewardShaper
{
    public float DamageDealtWeight { get; set; } = 0.01f;
    public float DamageTakenWeight { get; set; } = -0.01f;
    public float UnitKillBonus { get; set; } = 1.0f;
    public float BuildingDestroyedBonus { get; set; } = 2.0f;
    public float ObjectiveProgressBonus { get; set; } = 0.5f;
    public float StepPenalty { get; set; } = -0.001f;
    public float ClipMin { get; set; } = -1.0f;
    public float ClipMax { get; set; } = 1.0f;

    public float Shape(float damageDealt, float damageTaken, int unitsKilled, int buildingsDestroyed, float objectiveProgress, int step)
    {
        var reward = (damageDealt * DamageDealtWeight)
            + (damageTaken * DamageTakenWeight)
            + (unitsKilled * UnitKillBonus)
            + (buildingsDestroyed * BuildingDestroyedBonus)
            + (objectiveProgress * ObjectiveProgressBonus)
            + (step * StepPenalty);

        if (reward < ClipMin) reward = ClipMin;
        if (reward > ClipMax) reward = ClipMax;
        return reward;
    }
}
