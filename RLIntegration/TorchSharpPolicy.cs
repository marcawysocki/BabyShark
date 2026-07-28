namespace RLIntegration;

public sealed class TorchSharpPolicy
{
    private readonly RLMatrixConfig _config;
    private bool _isReady;

    public TorchSharpPolicy(RLMatrixConfig config)
    {
        _config = config;
        _isReady = false;
    }

    public bool IsReady => _isReady;

    public bool TryGetAction(Observation observation, out Action action)
    {
        action = new Action
        {
            MacroAction = MacroAction.Idle,
            MicroMoveX = 0f,
            MicroMoveY = 0f
        };

        return _isReady;
    }
}
