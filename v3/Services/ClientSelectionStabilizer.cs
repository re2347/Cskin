namespace CskinNative.Services;

// Keeps noisy LCU observations separate from the champion currently rendered
// by the UI. Each actual client change is emitted once after stable samples.
public sealed class ClientSelectionStabilizer
{
    private readonly int _requiredSamples;
    private int _candidateChampionId;
    private int _candidateSkinId;
    private int _consecutiveSamples;
    private int _synchronizedChampionId;

    public ClientSelectionStabilizer(int requiredSamples = 2)
    {
        _requiredSamples = Math.Max(1, requiredSamples);
    }

    public SelectionObservation Observe(int championId, int skinId)
    {
        if (championId <= 0)
            return new SelectionObservation(_candidateChampionId, _candidateSkinId, _consecutiveSamples, false, false);

        if (_candidateChampionId == championId && _candidateSkinId == skinId)
            _consecutiveSamples++;
        else
        {
            _candidateChampionId = championId;
            _candidateSkinId = skinId;
            _consecutiveSamples = 1;
        }

        var isStable = _consecutiveSamples >= _requiredSamples;
        var shouldSynchronize = isStable
            && _synchronizedChampionId != championId;
        if (shouldSynchronize) _synchronizedChampionId = championId;

        return new SelectionObservation(championId, skinId, _consecutiveSamples, isStable, shouldSynchronize);
    }

    public void Reset()
    {
        _candidateChampionId = 0;
        _candidateSkinId = 0;
        _consecutiveSamples = 0;
        _synchronizedChampionId = 0;
    }
}

public readonly record struct SelectionObservation(
    int ChampionId,
    int SkinId,
    int ConsecutiveSamples,
    bool IsStable,
    bool ShouldSynchronize);
